// Alerts endpoint.
//
// Field set comes from the stored alert hash: acknowledged, filter_id,
// filter_name, hostname, id, log_id, message, severity, source, timestamp.
// acknowledged is stored as the string "true"/"false" and is served as a real
// boolean, as the API contract requires.

using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class AlertsApi
{
    public static void Map(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/alerts", async (HttpRequest request) =>
        {
            // 上限：没有它时 limit 很大就会把整条告警时间线全部水合成列表。
            int limit = Math.Min(int.TryParse(request.Query["limit"], out int l) && l > 0 ? l : 100,
                                 ReadApi.MaxPageSize);
            int offset = int.TryParse(request.Query["offset"], out int o) && o > 0 ? o : 0;

            // ?acknowledged=false asks for the unacknowledged ones only; when the
            // parameter is absent every alert is returned.
            bool? wanted = null;
            if (request.Query.TryGetValue("acknowledged", out var flag) && flag.Count > 0)
            {
                if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) wanted = false;
                else if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) wanted = true;
            }

            long total = await store.Db.SortedSetLengthAsync(Keys.AlertsTimeline);

            // 无过滤时只需要"这一页"的 id。过去一律先取整条时间线再在内存里切片，
            // 于是 limit=10 也要为全部告警逐条 HGETALL：实测 1000 条 0.37s、
            // 5000 条 0.92s、20000 条 2.41s，线性增长而响应始终 2.5KB。
            if (!wanted.HasValue)
            {
                var pageIds = await store.Db.SortedSetRangeByRankAsync(
                    Keys.AlertsTimeline, offset, offset + limit - 1, Order.Descending);
                var page = new List<Dictionary<string, object?>>(pageIds.Length);
                foreach (var id in pageIds)
                {
                    var entry = await BuildEntryAsync(store, id.ToString());
                    if (entry is not null) page.Add(entry);
                }
                return ReadApi.JsonBody(page);
            }

            // 带 acknowledged 过滤时必须读哈希才能判断（该字段不是索引），所以扫描
            // 范围要设上限。代价：被请求的那一类在时间线上极稀疏时页可能不满；
            // 换掉的是"为一条查询把整条时间线读进内存"这种随规模无界的开销。
            const int Lookahead = 500;
            long scanCount = Math.Min(total, (long)(offset + limit) * 4 + Lookahead);
            if (scanCount <= 0) return ReadApi.JsonBody(Array.Empty<object>());

            var ids = await store.Db.SortedSetRangeByRankAsync(
                Keys.AlertsTimeline, 0, scanCount - 1, Order.Descending);

            var matched = new List<Dictionary<string, object?>>();
            foreach (var id in ids)
            {
                var entry = await BuildEntryAsync(store, id.ToString());
                if (entry is null) continue;
                if (entry["acknowledged"] is bool ack && ack != wanted.Value) continue;
                matched.Add(entry);
            }

            var result = matched.Skip(offset).Take(limit).ToList();
            _ = total;
            return ReadApi.JsonBody(result);
        });
    }

    /// <summary>
    /// 读一条告警并归一化为 API 形状：acknowledged 变成真正的布尔，
    /// 键按字母序排列（Flask 的 sort_keys）。哈希不存在时返回 null。
    /// </summary>
    private static async Task<Dictionary<string, object?>?> BuildEntryAsync(RedisStore store, string id)
    {
        var hash = await store.Db.HashGetAllAsync(id);
        if (hash.Length == 0) return null;

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in hash)
        {
            string name = field.Name.ToString();
            fields[name] = name == "acknowledged"
                ? field.Value.ToString() == "true"
                : field.Value.ToString();
        }

        return fields.OrderBy(p => p.Key, StringComparer.Ordinal)
                     .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
}
