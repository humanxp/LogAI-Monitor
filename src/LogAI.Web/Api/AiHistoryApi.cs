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
        app.MapGet("/api/ai-history/stats", async () =>
        {
            var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1);

            int critical = 0, healthy = 0, warning = 0, other = 0;
            foreach (var id in ids)
            {
                switch (await OverallStatusAsync(store, id.ToString()))
                {
                    case "critical": critical++; break;
                    case "warning": warning++; break;
                    case "healthy": healthy++; break;
                    default: other++; break;
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

    /// <summary>Newest-first page of analysis records.</summary>
    public static void MapList(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/ai-history", async (HttpRequest request) =>
        {
            int limit = int.TryParse(request.Query["limit"], out int l) && l > 0 ? l : 100;
            int offset = int.TryParse(request.Query["offset"], out int o) && o > 0 ? o : 0;

            long total = await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline);
            var ids = await store.Db.SortedSetRangeByRankAsync(
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

    private static async Task<string> OverallStatusAsync(RedisStore store, string historyId)
    {
        var analysis = await store.Db.HashGetAsync(historyId, "analysis");
        if (!analysis.HasValue) return "";
        try
        {
            using var document = JsonDocument.Parse(analysis.ToString());
            return document.RootElement.TryGetProperty("overall_status", out var status)
                ? status.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
