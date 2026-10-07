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

        // 100% 纯模型：status 直接用模型的 overall_status（Classify 只做同义词归一，
        // 不再做 critical 闸门 / warning 下限等任何修正）。
        string status = AiStatusClassifier.Classify(type, analysis);

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
