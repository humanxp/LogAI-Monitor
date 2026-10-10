// AI history endpoints.
//
// Two deliberate differences from a plain hash passthrough, both required for
// fidelity with the stored records and the API contract:
//
//   * The stored hash also carries log_ids and fail_count. log_ids is a JSON
//     array of up to ~500 identifiers per record (several KB), so returning it
//     would inflate the payload by orders of magnitude; the API exposes
//     neither field. Only analysis, id, logs_analyzed, timestamp and type are
//     returned.
//   * analysis is a JSON string in Redis and becomes a nested object in the
//     response, with keys sorted recursively (Flask's sort_keys applies inside
//     nested objects too), and logs_analyzed becomes a real integer.

using System.Text.Json;
using LogAI.Core.Ai;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class AiHistoryApi
{
    // 全量统计（无时间窗）是 O(N) 的：要拉取全部 1.8 万条 id + status。它是汇总，
    // 无需实时（分析每 ~2 分钟才提交一条），短 TTL 缓存即可把后续加载降到毫秒级。
    // 带时间窗的筛选很快（几 ms）且窗口每次不同，不缓存。
    private static readonly object StatsCacheLock = new();
    private static (Dictionary<string, int> Counts, int Total, long Cold)? _statsCache;
    private static DateTimeOffset _statsCacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan StatsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Buckets every stored analysis by its overall_status.</summary>
    public static void Map(WebApplication app, RedisStore store, LogArchive archive)
    {
        // 这是带参数会改变行为的端点：/api/ai-history/stats 接受
        // start/end（AI 历史页的日期筛选要让汇总卡片跟着范围走），
        // 之前这里把参数完全忽略，选任何时间范围返回的都是全量统计。
        app.MapGet("/api/ai-history/stats", async (HttpRequest request) =>
        {
            var (startTime, endTime) = ParseWindow(request, out bool hasWindow);

            // 全量统计走缓存（见类头注释）。
            if (!hasWindow)
            {
                lock (StatsCacheLock)
                {
                    if (_statsCache is { } cached && DateTimeOffset.UtcNow - _statsCacheAt < StatsCacheTtl)
                        return ReadApi.JsonBody(StatsPayload(cached.Counts, cached.Total, cached.Cold));
                }
            }

            RedisValue[] ids = hasWindow
                ? await store.Db.SortedSetRangeByScoreAsync(Keys.AiHistoryTimeline, startTime, endTime)
                : await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1);

            // 7 档严重程度各自计数（critical/error/warning/notice/info/healthy/other）。
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string s in AiStatusClassifier.Statuses) counts[s] = 0;
            void Bump(string s)
            {
                // 兼容 7 档时期落库的旧值（error/notice→warning、info→healthy），
                // 免得它们在统计里掉进 other。
                string k = s switch
                {
                    "error" or "err" or "notice" or "warn" => "warning",
                    "info" or "informational" => "healthy",
                    _ => s,
                };
                if (counts.TryGetValue(k, out int c)) counts[k] = c + 1;
                else counts["other"]++;
            }

            const int Chunk = 500;
            // 第一遍读独立的状态小哈希 ai_history:status（id → 分类结果）。它不随归档
            // 搬走，因此已归档记录的状态也在 Redis 里，不必回读 SQLite 的整份哈希
            // （含 5KB 分析 JSON）——这是冷调用从 444ms 降到毫秒级的关键。
            // status 极小（~10B），批次可以很大，把往返压到几次。
            const int StatusChunk = 5000;
            var fallback = new List<string>();
            for (int offset = 0; offset < ids.Length; offset += StatusChunk)
            {
                int size = Math.Min(StatusChunk, ids.Length - offset);
                var batch = store.Db.CreateBatch();
                var tasks = new Task<RedisValue>[size];
                for (int i = 0; i < size; i++)
                    tasks[i] = batch.HashGetAsync(Keys.AiHistoryStatus, ids[offset + i].ToString());
                batch.Execute();
                var loaded = await Task.WhenAll(tasks);
                for (int i = 0; i < size; i++)
                {
                    string s = loaded[i].ToString();
                    if (s.Length == 0) fallback.Add(ids[offset + i].ToString());   // 未回填的旧记录
                    else Bump(s);
                }
            }

            // 第二遍：无 status 的旧记录回退。已归档记录的哈希在 SQLite，
            // 用 HydrateHashesAsync 一并取回（Redis 缺失 → SQLite）。
            foreach (var chunk in fallback.Chunk(Chunk))
            {
                var hashes = await ReadApi.HydrateHashesAsync(store, archive,
                    Array.ConvertAll(chunk, x => (RedisValue)x));
                for (int i = 0; i < chunk.Length; i++)
                {
                    var hash = hashes[i];
                    if (hash.Length == 0) { Bump("other"); continue; }
                    var dict = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
                    string status = dict.GetValueOrDefault("status") ?? "";
                    if (status.Length == 0)
                        status = Classify(dict.GetValueOrDefault("type") ?? "", dict.GetValueOrDefault("analysis") ?? "");
                    Bump(status);
                }
            }

            // 冷热拆分：冷 = SQLite 里已归档的分析条数。只对全量有意义——按时间窗筛
            // 出来的记录冷热混在一起，拆开反而误导。SQLite COUNT 很快，且跟着 30s 缓存走。
            long? cold = hasWindow ? null : await archive.CountHashesByPrefixAsync("ai_history:");

            if (!hasWindow)
            {
                lock (StatsCacheLock)
                {
                    _statsCache = (counts, ids.Length, cold!.Value);
                    _statsCacheAt = DateTimeOffset.UtcNow;
                }
            }

            return ReadApi.JsonBody(StatsPayload(counts, ids.Length, cold));
        });
    }

    /// <summary>
    /// 分类口径：single 记录看 is_critical，否则看 category；
    /// batch 记录看 overall_status，退回 category。healthy 与 info 同桶，
    /// critical 与 error 同桶。逻辑在 AiStatusClassifier 里（写入时也用它），
    /// 这里只做字符串解析的适配。
    /// </summary>
    private static string Classify(string type, string analysisRaw) =>
        AiStatusClassifier.Classify(type, analysisRaw);

    /// <summary>
    /// 按归一化后的状态筛 id（"critical"/"warning"/"healthy"/"other"）。
    /// 状态存在 ai_history:status 小哈希里、归档时不搬走，所以冷热记录都能筛到。
    /// </summary>
    private static async Task<List<RedisValue>> FilterIdsByStatusAsync(RedisStore store, RedisValue[] ids, string bucket)
    {
        var matched = new List<RedisValue>(ids.Length);
        const int Chunk = 5000;
        for (int offset = 0; offset < ids.Length; offset += Chunk)
        {
            int size = Math.Min(Chunk, ids.Length - offset);
            var batch = store.Db.CreateBatch();
            var reads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
                reads[i] = batch.HashGetAsync(Keys.AiHistoryStatus, ids[offset + i].ToString());
            batch.Execute();
            var loaded = await Task.WhenAll(reads);
            for (int i = 0; i < size; i++)
                if (AiStatusClassifier.NormalizeBucket(loaded[i].ToString()) == bucket)
                    matched.Add(ids[offset + i]);
        }
        return matched;
    }

    /// <summary>统计响应：7 档各自计数 + total + 冷热拆分（键排序由序列化统一处理）。</summary>
    private static Dictionary<string, object?> StatsPayload(IReadOnlyDictionary<string, int> counts, int total, long? cold)    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string s in AiStatusClassifier.Statuses)
            payload[s] = counts.TryGetValue(s, out int v) ? v : 0;
        payload["total"] = total;
        // 冷 = SQLite 里已归档的分析条数；热 = 时间线总数 - 冷。
        // 时间线在归档后仍保留 id（只搬哈希），所以 total 就是冷+热。
        // 按时间窗筛时冷热混在一起，拆开误导 —— 此时 cold 传 null，不下发这两个键。
        if (cold is { } c)
        {
            payload["analyses_cold"] = c;
            payload["analyses_hot"] = Math.Max(0, total - c);
        }
        return payload;
    }

    /// <summary>Newest-first page of analysis records.</summary>
    public static void MapList(WebApplication app, RedisStore store, LogArchive archive)
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

            // ?status=critical|warning|healthy|other —— 点上面的分级卡片时按档筛选。
            string rawStatus = request.Query["status"].ToString();
            string statusFilter = rawStatus.Length > 0 ? AiStatusClassifier.NormalizeBucket(rawStatus) : "";

            long total;
            RedisValue[] ids;
            if (statusFilter.Length > 0)
            {
                // 状态不在索引里（存在 ai_history:status 小哈希），得读出来才能筛，所以
                // 先取时间窗内全量 id → 批量读状态 → 过滤 → 再切片。口径与 stats 卡片一致。
                var allIds = hasWindow
                    ? await store.Db.SortedSetRangeByScoreAsync(
                          Keys.AiHistoryTimeline, startTime, endTime, Exclude.None, Order.Descending)
                    : await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1, Order.Descending);
                var matched = await FilterIdsByStatusAsync(store, allIds, statusFilter);
                total = matched.Count;
                ids = matched.Skip(offset).Take(limit).ToArray();
            }
            else
            {
                total = hasWindow
                    ? await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline, startTime, endTime, Exclude.None)
                    : await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline);
                ids = hasWindow
                    ? await store.Db.SortedSetRangeByScoreAsync(
                          Keys.AiHistoryTimeline, startTime, endTime, Exclude.None, Order.Descending, offset, limit)
                    : await store.Db.SortedSetRangeByRankAsync(
                          Keys.AiHistoryTimeline, offset, offset + limit - 1, Order.Descending);
            }

            var history = new List<Dictionary<string, object?>>(ids.Length);
            // 整页哈希一批取回(与上面 stats 端点同一做法):原来逐条 HGETALL,
            // limit=1000 实测 0.13s,几乎全是串行往返的等待。已归档记录从 SQLite 回填。
            var hashes = await ReadApi.HydrateHashesAsync(store, archive, ids);
            for (int i = 0; i < ids.Length; i++)
            {
                var hash = hashes[i];
                if (hash.Length == 0) continue;
                var stored = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

                history.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["analysis"] = stored.TryGetValue("analysis", out string? analysis)
                        ? ReadApi.ParseSortedObject(analysis)
                        : new Dictionary<string, object?>(StringComparer.Ordinal),
                    ["id"] = ids[i].ToString(),
                    ["logs_analyzed"] = int.TryParse(stored.GetValueOrDefault("logs_analyzed"), out int count) ? count : 0,
                    ["status"] = stored.GetValueOrDefault("status") ?? "",
                    ["timestamp"] = stored.GetValueOrDefault("timestamp") ?? "",
                    ["type"] = stored.GetValueOrDefault("type") ?? "",
                    // 单条分析会带 log_source（老记录没有 → 前端按空处理）
                    ["log_source"] = stored.GetValueOrDefault("log_source") ?? "",
                    // 逐条用量：这批用了多少 token、花了多久、哪个模型（老记录没有 → 0/空）
                    ["model"] = stored.GetValueOrDefault("model") ?? "",
                    ["duration_seconds"] = double.TryParse(stored.GetValueOrDefault("duration_seconds"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double dur) ? dur : 0,
                    ["ai_calls"] = int.TryParse(stored.GetValueOrDefault("ai_calls"), out int calls) ? calls : 0,
                    ["tokens_prompt"] = long.TryParse(stored.GetValueOrDefault("tokens_prompt"), out long tp) ? tp : 0,
                    ["tokens_completion"] = long.TryParse(stored.GetValueOrDefault("tokens_completion"), out long tc) ? tc : 0,
                    ["tokens_total"] = long.TryParse(stored.GetValueOrDefault("tokens_total"), out long tt) ? tt : 0,
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
