// AI history endpoints.
//
// Two deliberate differences from a plain hash passthrough, both required for
// fidelity with the Python API:
//
//   * The stored hash also carries log_ids and fail_count. log_ids is a JSON
//     array of up to ~500 identifiers per record (several KB), so returning it
//     would inflate the payload by orders of magnitude; the Python API exposes
//     neither field. Only analysis, id, logs_analyzed, timestamp and type are
//     returned.
//   * analysis is a JSON string in Redis and becomes a nested object in the
//     response, with keys sorted recursively (Flask's sort_keys applies inside
//     nested objects too), and logs_analyzed becomes a real integer.

using System.Text.Json;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class AiHistoryApi
{
    /// <summary>Buckets every stored analysis by its overall_status.</summary>
    public static void Map(WebApplication app, RedisStore store)
    {
        // 这是带参数会改变行为的端点：Python 的 /api/ai-history/stats 接受
        // start/end（AI 历史页的日期筛选要让汇总卡片跟着范围走），
        // 之前这里把参数完全忽略，选任何时间范围返回的都是全量统计。
        app.MapGet("/api/ai-history/stats", async (HttpRequest request) =>
        {
            var (startTime, endTime) = ParseWindow(request, out bool hasWindow);

            RedisValue[] ids = hasWindow
                ? await store.Db.SortedSetRangeByScoreAsync(Keys.AiHistoryTimeline, startTime, endTime)
                : await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1);

            int critical = 0, healthy = 0, warning = 0, other = 0;

            // 一批取回 type + analysis：原来每个 id 一次 HGET，全量 1.5 万条
            // 就是 1.5 万次往返（页面每次加载都跑一遍）。
            const int Chunk = 500;
            for (int offset = 0; offset < ids.Length; offset += Chunk)
            {
                int size = Math.Min(Chunk, ids.Length - offset);
                var batch = store.Db.CreateBatch();
                var tasks = new Task<RedisValue[]>[size];
                for (int i = 0; i < size; i++)
                {
                    var id = ids[offset + i];
                    tasks[i] = batch.HashGetAsync(id.ToString(), ["type", "analysis"]);
                }
                batch.Execute();

                foreach (var task in tasks)
                {
                    RedisValue[] fields = await task;
                    switch (Classify(fields[0].ToString(), fields[1].ToString()))
                    {
                        case "critical": critical++; break;
                        case "warning": warning++; break;
                        case "healthy": healthy++; break;
                        default: other++; break;
                    }
                }
            }

            return ReadApi.JsonBody(new
            {
                critical,
                healthy,
                other,
                total = ids.Length,
                warning,
            });
        });
    }

    /// <summary>
    /// Python 的分类：single 记录看 is_critical，否则看 category；
    /// batch 记录看 overall_status，退回 category。healthy 与 info 同桶，
    /// critical 与 error 同桶——只看 overall_status 会把 single/info/error
    /// 全部算进 other，汇总卡片因此与列表内容对不上。
    /// </summary>
    private static string Classify(string type, string analysisRaw)
    {
        if (analysisRaw.Length == 0) return "other";
        try
        {
            using var document = JsonDocument.Parse(analysisRaw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "other";
            var root = document.RootElement;

            string status;
            if (string.Equals(type, "single", StringComparison.Ordinal))
            {
                status = root.TryGetProperty("is_critical", out var isCritical)
                         && isCritical.ValueKind == JsonValueKind.True
                    ? "critical"
                    : Text(root, "category");
            }
            else
            {
                status = Text(root, "overall_status");
                if (status.Length == 0) status = Text(root, "category");
            }

            return status.ToLowerInvariant() switch
            {
                "healthy" or "info" => "healthy",
                "warning" => "warning",
                "critical" or "error" => "critical",
                _ => "other",
            };
        }
        catch (JsonException)
        {
            return "other";
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Newest-first page of analysis records.</summary>
    public static void MapList(WebApplication app, RedisStore store)
    {
        // The Analysis History page has a date filter. This endpoint used to read only
        // limit/offset, so choosing a range changed nothing. The timeline is scored by
        // write time, so the window becomes a plain score range.
        app.MapGet("/api/ai-history", async (HttpRequest request) =>
        {
            // 同 /api/logs：给 limit 加上限，避免一次请求水合整条历史。
            int limit = Math.Min(int.TryParse(request.Query["limit"], out int l) && l > 0 ? l : 100,
                                 ReadApi.MaxPageSize);
            int offset = int.TryParse(request.Query["offset"], out int o) && o > 0 ? o : 0;
            var (startTime, endTime) = ParseWindow(request, out bool hasWindow);

            long total = hasWindow
                ? await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline, startTime, endTime, Exclude.None)
                : await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline);

            RedisValue[] ids = hasWindow
                ? await store.Db.SortedSetRangeByScoreAsync(
                      Keys.AiHistoryTimeline, startTime, endTime, Exclude.None, Order.Descending, offset, limit)
                : await store.Db.SortedSetRangeByRankAsync(
                      Keys.AiHistoryTimeline, offset, offset + limit - 1, Order.Descending);

            var history = new List<Dictionary<string, object?>>(ids.Length);
            foreach (var id in ids)
            {
                var hash = await store.Db.HashGetAllAsync(id.ToString());
                if (hash.Length == 0) continue;
                var stored = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

                history.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["analysis"] = stored.TryGetValue("analysis", out string? analysis)
                        ? ReadApi.ParseSortedObject(analysis)
                        : new Dictionary<string, object?>(StringComparer.Ordinal),
                    ["id"] = id.ToString(),
                    ["logs_analyzed"] = int.TryParse(stored.GetValueOrDefault("logs_analyzed"), out int count) ? count : 0,
                    ["timestamp"] = stored.GetValueOrDefault("timestamp") ?? "",
                    ["type"] = stored.GetValueOrDefault("type") ?? "",
                });
            }

            return ReadApi.JsonBody(new { history, limit, offset, total });
        });
    }

    /// <summary>
    /// Same contract as the logs endpoint: epoch seconds or ISO-8601, both ends must
    /// parse, and an inverted pair is swapped rather than rejected. A partial or
    /// unparsable pair means "no window" rather than an empty result.
    /// </summary>
    private static (double Start, double End) ParseWindow(HttpRequest req, out bool hasWindow)
    {
        hasWindow = false;
        double? a = ParseOne(req.Query["start"]);
        double? b = ParseOne(req.Query["end"]);
        if (a is null || b is null) return (0, 0);
        hasWindow = true;
        return (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));

        static double? ParseOne(string? raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return null;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double epoch))
                return epoch;
            if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                return dt.ToUnixTimeMilliseconds() / 1000.0;
            return null;
        }
    }
}
