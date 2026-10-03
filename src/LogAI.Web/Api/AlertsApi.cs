// Alerts endpoint.
//
// Field set comes from the stored alert hash: acknowledged, filter_id,
// filter_name, hostname, id, log_id, message, severity, source, timestamp.
// acknowledged is stored as the string "true"/"false" and is served as a real
// boolean, like the Python API does.

using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class AlertsApi
{
    public static void Map(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/alerts", async (HttpRequest request) =>
        {
            // 同 /api/logs：这里没有上限时，limit 很大就会把整条告警时间线
            // 全部水合成列表后再切片，属于同一类"一次请求拖垮进程"的问题。
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
            var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1, Order.Descending);

            var alerts = new List<Dictionary<string, object?>>();
            foreach (var id in ids)
            {
                var hash = await store.Db.HashGetAllAsync(id.ToString());
                if (hash.Length == 0) continue;

                var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                bool acknowledged = false;
                foreach (var field in hash)
                {
                    string name = field.Name.ToString();
                    string value = field.Value.ToString();
                    if (name == "acknowledged")
                    {
                        acknowledged = value == "true";
                        entry[name] = acknowledged;
                    }
                    else
                    {
                        entry[name] = value;
                    }
                }

                if (wanted.HasValue && acknowledged != wanted.Value) continue;

                alerts.Add(entry.OrderBy(p => p.Key, StringComparer.Ordinal)
                                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
            }

            var page = alerts.Skip(offset).Take(limit).ToList();
            _ = total;
            return ReadApi.JsonBody(page);
        });
    }
}
