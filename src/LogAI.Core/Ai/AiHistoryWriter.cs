// Writes AI analysis history records.
//
// The stored record and the API representation differ deliberately, and both
// shapes are verified against real stored records:
//
//   stored (8 fields): id analysis log_ids timestamp type logs_analyzed fail_count status
//   served (5 fields): analysis id logs_analyzed timestamp type
//
// log_ids holds up to ~500 identifiers and is therefore never served; fail_count
// drives batch retirement, and status is the 7-tier classification used by the
// stats endpoint — all three are internal state. /api/ai-history reads its five
// fields from this record, so the writer must not add or drop any served field.

using System.Text.Json.Nodes;
using LogAI.Core.Store;
using LogAI.Core.Syslog;
using StackExchange.Redis;

namespace LogAI.Core.Ai;

public sealed class AiHistoryWriter(RedisStore store, int retentionHours = 720)
{
    private long _lastMilliseconds;

    /// <summary>Records one analysis run over a batch of log ids.</summary>
    /// <summary>
    /// extra carries the fields only some callers supply: a single-log analysis
    /// also stores log_source and log_severity, which is why the record shape is
    /// caller dependent rather than a fixed set.
    /// </summary>
    public async Task<string> WriteAsync(IReadOnlyList<string> logIds, JsonNode analysis,
                                         string type = "auto", int failCount = 0,
                                         IReadOnlyDictionary<string, string>? extra = null)
    {
        string id = $"ai_history:{NextMilliseconds()}";
        // 提前读设置：保留期用它，避免后面重复取。
        var settings = await store.GetSettingsAsync();

        // 确定性兜底：批分析里没有 emergency/alert/critical 级别的原始日志时不允许判
        // critical（单条分析走 is_critical，不受此限）。
        bool allowCritical = string.Equals(type, "single", StringComparison.Ordinal)
            || await AnyCriticalSeverityAsync(logIds);
        string status = AiStatusClassifier.Classify(type, analysis, allowCritical);

        // warning 下限：模型判 healthy 但原始日志里有真实故障词（Transfer failed、
        // refused、out of memory…）时，升到 warning 并把那条日志补进 issues，避免
        // "发现问题却判 healthy"或"warning 但 No issues"。只对批分析生效。
        if (string.Equals(status, "healthy", StringComparison.Ordinal)
            && !string.Equals(type, "single", StringComparison.Ordinal))
        {
            var failureLines = await FindFailureLinesAsync(logIds);
            if (failureLines.Count > 0)
            {
                status = "warning";
                if (analysis is JsonObject obj)
                {
                    var issues = obj["issues_found"] as JsonArray ?? new JsonArray();
                    foreach (string line in failureLines.Take(8))
                        if (issues.All(x => x?.ToString() != line)) issues.Add(line);
                    obj["issues_found"] = issues;
                    // 刚注入的 issues 是在 EnsureFields 之后才出现的，这里补一条建议，
                    // 避免"有 issues 无 recommendations"。
                    JsonExtractor.EnsureRecommendation(obj);
                }
            }
        }

        var ids = new JsonArray();
        foreach (string logId in logIds) ids.Add(logId);

        var entries = new List<HashEntry>
        {
            new HashEntry("id", id),
            new HashEntry("analysis", analysis.ToJsonString()),
            new HashEntry("log_ids", ids.ToJsonString()),
            new HashEntry("timestamp", SyslogParser.NowTimestamp()),
            new HashEntry("type", type),
            new HashEntry("logs_analyzed", logIds.Count.ToString()),
            new HashEntry("fail_count", failCount.ToString()),
            // 内部字段：分类结果落一条小字段，/api/ai-history/stats 只读它即可
            // 汇总，不必每次解析整份 analysis JSON（见 AiStatusClassifier）。
            new HashEntry("status", status),
        };
        if (extra is not null)
            foreach (var pair in extra) entries.Add(new HashEntry(pair.Key, pair.Value));

        await store.Db.HashSetAsync(id, entries.ToArray());
        // 状态另存一份独立小哈希：归档只搬主哈希，这条留在 Redis，stats 直接读它，
        // 不必为已归档记录回读 SQLite 的整份哈希（含 5KB 分析 JSON）。
        await store.Db.HashSetAsync(Keys.AiHistoryStatus, id, status);

        double score = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        await store.Db.SortedSetAddAsync(Keys.AiHistoryTimeline, id, score);

        // 保留期跟着 log_retention_hours 走（手动改设置也同步，不必重启）。
        // 设置缺失/非法时回退到构造函数参数（默认 720 = 30 天）。
        int hours = retentionHours;
        if (int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("log_retention_hours")), out int configured)
            && configured > 0)
            hours = configured;
        if (hours > 0)
            await store.Db.KeyExpireAsync(id, TimeSpan.FromHours(hours));

        return id;
    }

    /// <summary>
    /// Marks a batch as failed. After three consecutive failures the batch is
    /// retired rather than retried forever — the guard that was inert in the
    /// an earlier deployment (it raised every time) and let
    /// the pending queue grow to 41k entries.
    /// </summary>
    public const int MaxFailedRetries = 3;

    public static bool ShouldRetire(int failCount) => failCount >= MaxFailedRetries;

    /// <summary>
    /// 这批日志里是否存在"真正危急"的日志（级别危急 + 消息含故障特征词）。用来给
    /// "critical" 加一道确定性闸门：3B 模型会把重复的例行消息当成 critical，而设备
    /// 的级别标签本身不可信——线上有设备把 "start NTP update" 标成 emergency，只凭
    /// 级别会被骗。判断逻辑见 AiStatusClassifier.IsGenuinelyCritical。
    /// </summary>
    private async Task<bool> AnyCriticalSeverityAsync(IReadOnlyList<string> logIds)
    {
        if (logIds.Count == 0) return false;
        const int Chunk = 500;
        for (int offset = 0; offset < logIds.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, logIds.Count - offset);
            var batch = store.Db.CreateBatch();
            var sevReads = new Task<RedisValue>[size];
            var msgReads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
            {
                sevReads[i] = batch.HashGetAsync(logIds[offset + i], "severity");
                msgReads[i] = batch.HashGetAsync(logIds[offset + i], "message");
            }
            batch.Execute();
            var sevs = await Task.WhenAll(sevReads);
            var msgs = await Task.WhenAll(msgReads);
            for (int i = 0; i < size; i++)
                if (AiStatusClassifier.IsGenuinelyCritical(sevs[i].ToString(), msgs[i].ToString()))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// 找出消息里含故障词的日志行（"[host] message"），供 warning 下限补进 issues。
    /// 只取消息本身，不看级别——"Transfer failed" 之类即使设备标成 info 也值得 warning。
    /// </summary>
    private async Task<List<string>> FindFailureLinesAsync(IReadOnlyList<string> logIds)
    {
        var result = new List<string>();
        if (logIds.Count == 0) return result;
        const int Chunk = 500;
        for (int offset = 0; offset < logIds.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, logIds.Count - offset);
            var batch = store.Db.CreateBatch();
            var hostReads = new Task<RedisValue>[size];
            var msgReads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
            {
                hostReads[i] = batch.HashGetAsync(logIds[offset + i], "hostname");
                msgReads[i] = batch.HashGetAsync(logIds[offset + i], "message");
            }
            batch.Execute();
            var hosts = await Task.WhenAll(hostReads);
            var msgs = await Task.WhenAll(msgReads);
            for (int i = 0; i < size; i++)
            {
                string msg = msgs[i].ToString();
                if (AiStatusClassifier.HasFailureWord(msg))
                {
                    string host = hosts[i].ToString();
                    result.Add("[" + (host.Length > 0 ? host : "?") + "] " + msg);
                }
            }
        }
        return result;
    }

    private long NextMilliseconds()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            long previous = Interlocked.Read(ref _lastMilliseconds);
            long next = now > previous ? now : previous + 1;
            if (Interlocked.CompareExchange(ref _lastMilliseconds, next, previous) == previous) return next;
        }
    }
}
