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
        // 提前读设置：strict mode 与保留期共用，避免重复取。
        var settings = await store.GetSettingsAsync();
        bool strictMode = AiClient.AiStrictModeIn(settings);

        // 确定性兜底：批分析里没有 emergency/alert/critical 级别的原始日志时不允许判
        // critical（单条分析走 is_critical，不受此限）。
        bool allowCritical = string.Equals(type, "single", StringComparison.Ordinal)
            || await AnyCriticalSeverityAsync(logIds);
        string status = AiStatusClassifier.Classify(type, analysis, allowCritical);

        // 严格模式（设置页开关，默认关）：模型判 healthy，但批次里有 error/warning
        // 及以上级别的日志时升到 warning——"宁可多看 warning 也不漏"。
        status = AiStatusClassifier.ApplyStrictMode(status, strictMode,
            await AnyWarningOrHigherSeverityAsync(logIds));

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
    /// 批次里是否存在 warning 及以上级别的原始日志（只看级别，不看内容）。
    /// 供严格模式使用：error/warning 级日志即使内容例行，也把 healthy 升到 warning。
    /// </summary>
    private async Task<bool> AnyWarningOrHigherSeverityAsync(IReadOnlyList<string> logIds)
    {
        if (logIds.Count == 0) return false;
        const int Chunk = 500;
        for (int offset = 0; offset < logIds.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, logIds.Count - offset);
            var batch = store.Db.CreateBatch();
            var reads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
                reads[i] = batch.HashGetAsync(logIds[offset + i], "severity");
            batch.Execute();
            var loaded = await Task.WhenAll(reads);
            foreach (var value in loaded)
                if (AiStatusClassifier.IsWarningOrHigherSeverity(value.ToString())) return true;
        }
        return false;
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
