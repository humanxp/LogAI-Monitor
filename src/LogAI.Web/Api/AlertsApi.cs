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
    /// <summary>
    /// 级别分桶：与告警级别门限同一套 syslog 编码（emergency/alert/critical 并成一档，
    /// 因为它们在门限里就是同一档）。
    /// </summary>
    private static readonly string[] SeverityBuckets =
        ["critical", "error", "warning", "notice", "info", "debug", "other"];

    private static string SeverityBucket(string severity) =>
        LogAI.Core.Filters.FilterMatcher.SeverityCode(severity) switch
        {
            0 or 1 or 2 => "critical",
            3 => "error",
            4 => "warning",
            5 => "notice",
            6 => "info",
            7 => "debug",
            _ => "other",
        };

    // 页面每次加载都会请求，30 秒缓存（与 /api/ai-history/stats 同一哲学）。
    private static readonly object StatsCacheLock = new();
    private static Dictionary<string, object?>? _statsCache;
    private static DateTimeOffset _statsCacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan StatsCacheTtl = TimeSpan.FromSeconds(30);

    public static void Map(WebApplication app, RedisStore store, LogArchive archive)
    {
        app.MapGet("/api/alerts/stats", async () =>
        {
            lock (StatsCacheLock)
            {
                if (_statsCache is { } cached && DateTimeOffset.UtcNow - _statsCacheAt < StatsCacheTtl)
                    return ReadApi.JsonBody(cached);
            }

            var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string bucket in SeverityBuckets) counts[bucket] = 0;

            // 热库批量取 severity；缺失的（已归档）交给冷库。告警参与冷热分层，
            // 只读 Redis 会让归档告警在各档计数里凭空消失。
            const int Chunk = 500;
            var severities = new string[ids.Length];
            var missing = new List<string>();
            var missingIndex = new List<int>();
            for (int offset = 0; offset < ids.Length; offset += Chunk)
            {
                int size = Math.Min(Chunk, ids.Length - offset);
                var batch = store.Db.CreateBatch();
                var reads = new Task<RedisValue>[size];
                for (int i = 0; i < size; i++)
                    reads[i] = batch.HashGetAsync(ids[offset + i].ToString(), "severity");
                batch.Execute();
                var loaded = await Task.WhenAll(reads);
                for (int i = 0; i < size; i++)
                {
                    string raw = loaded[i].ToString();
                    if (raw.Length == 0)
                    {
                        missing.Add(ids[offset + i].ToString());
                        missingIndex.Add(offset + i);
                    }
                    else severities[offset + i] = raw;
                }
            }
            if (missing.Count > 0)
            {
                var archived = await archive.GetHashFieldBatchAsync(missing, "severity");
                for (int i = 0; i < missing.Count; i++)
                    if (archived.TryGetValue(missing[i], out string? value))
                        severities[missingIndex[i]] = value;
            }

            foreach (string severity in severities)
            {
                string bucket = SeverityBucket(severity);
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string bucket in SeverityBuckets) payload[bucket] = counts[bucket];
            payload["total"] = ids.Length;
            // 冷热拆分：冷 = SQLite 里已归档的告警条数；热 = 时间线总数 - 冷。
            // 时间线归档后仍保留 id（只搬哈希），所以 total 就是冷+热。
            long cold = await archive.CountHashesByPrefixAsync("alert:");
            payload["alerts_cold"] = cold;
            payload["alerts_hot"] = Math.Max(0, ids.Length - cold);

            lock (StatsCacheLock)
            {
                _statsCache = payload;
                _statsCacheAt = DateTimeOffset.UtcNow;
            }
            return ReadApi.JsonBody(payload);
        });

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

            // ?severity=critical|error|warning|notice|info|debug|other —— 点上面的分级卡片时筛选。
            string severityFilter = (request.Query["severity"].ToString() ?? "").Trim().ToLowerInvariant();

            long total = await store.Db.SortedSetLengthAsync(Keys.AlertsTimeline);

            // 无过滤时只需要"这一页"的 id。过去一律先取整条时间线再在内存里切片，
            // 于是 limit=10 也要为全部告警逐条 HGETALL：实测 1000 条 0.37s、
            // 5000 条 0.92s、20000 条 2.41s，线性增长而响应始终 2.5KB。
            if (!wanted.HasValue && severityFilter.Length == 0)
            {
                var pageIds = await store.Db.SortedSetRangeByRankAsync(
                    Keys.AlertsTimeline, offset, offset + limit - 1, Order.Descending);
                // 整页哈希一批取回:逐条 HGETALL 是纯往返等待(见 RedisStore 的做法)。
                // 已归档告警从 SQLite 回填。
                var hashes = await ReadApi.HydrateHashesAsync(store, archive, pageIds);
                var page = new List<Dictionary<string, object?>>(pageIds.Length);
                for (int i = 0; i < pageIds.Length; i++)
                {
                    var entry = BuildEntry(hashes[i]);
                    if (entry is not null) page.Add(entry);
                }
                return ReadApi.JsonBody(page);
            }

            // 带 acknowledged / severity 过滤时必须读哈希才能判断（都不是索引），所以扫描
            // 范围要设上限。代价：被请求的那一类在时间线上极稀疏时页可能不满；
            // 换掉的是"为一条查询把整条时间线读进内存"这种随规模无界的开销。
            const int Lookahead = 500;
            long scanCount = Math.Min(total, (long)(offset + limit) * 4 + Lookahead);
            if (scanCount <= 0) return ReadApi.JsonBody(Array.Empty<object>());

            var ids = await store.Db.SortedSetRangeByRankAsync(
                Keys.AlertsTimeline, 0, scanCount - 1, Order.Descending);

            // 扫描窗口内的哈希同样一批取回:原来逐条 HGETALL,扫描上限越大等得越久。
            // 已归档告警从 SQLite 回填。
            var scanHashes = await ReadApi.HydrateHashesAsync(store, archive, ids);
            var matched = new List<Dictionary<string, object?>>();
            for (int i = 0; i < ids.Length; i++)
            {
                var entry = BuildEntry(scanHashes[i]);
                if (entry is null) continue;
                if (wanted.HasValue && entry["acknowledged"] is bool ack && ack != wanted.Value) continue;
                if (severityFilter.Length > 0
                    && !string.Equals(SeverityBucket(entry["severity"]?.ToString() ?? ""), severityFilter, StringComparison.Ordinal))
                    continue;
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
    private static Dictionary<string, object?>? BuildEntry(HashEntry[] hash)
    {
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
