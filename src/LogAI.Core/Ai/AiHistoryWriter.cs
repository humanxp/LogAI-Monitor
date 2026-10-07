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
                                         IReadOnlyDictionary<string, string>? extra = null,
                                         Func<List<string>, Task<List<string>>>? recommendAsync = null)
    {
        string id = $"ai_history:{NextMilliseconds()}";
        // 提前读设置：保留期用它，避免后面重复取。
        var settings = await store.GetSettingsAsync();

        bool single = string.Equals(type, "single", StringComparison.Ordinal);
        bool guard = AiClient.AiStatusGuardEnabledIn(settings);
        string status;

        if (single || !guard)
        {
            // 单条分析，或关闭了"程序兜底"：纯模型判定。
            status = AiStatusClassifier.Classify(type, analysis, allowCritical: true, guard: guard);
        }
        else
        {
            // 程序兜底（设置页「AI 判定修正」开启）：critical 闸门 + warning 下限。
            bool allowCritical = await AnyCriticalSeverityAsync(logIds);
            status = AiStatusClassifier.Classify(type, analysis, allowCritical, guard: true);

            if (string.Equals(status, "healthy", StringComparison.Ordinal))
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

                        // 建议交给模型生成（针对检出的故障行）；失败就留空。
                        if (recommendAsync is not null)
                        {
                            try
                            {
                                var modelRecs = await recommendAsync(failureLines.Take(8).ToList());
                                if (modelRecs.Count > 0)
                                {
                                    var recArray = obj["recommendations"] as JsonArray ?? new JsonArray();
                                    foreach (string r in modelRecs)
                                        if (recArray.All(x => x?.ToString() != r)) recArray.Add(r);
                                    obj["recommendations"] = recArray;
                                }
                            }
                            catch { /* 模型失败就留空，不影响写库 */ }
                        }
                    }
                }
            }

            // 程序兜底模式：把模型原始 overall_status 覆盖成修正后的 status，
            // 让 Analysis Details 界面显示正确值（与列表/统计一致）。
            if (analysis is JsonObject objFinal)
                objFinal["overall_status"] = status;
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
    /// 批里是否存在"真正危急"的日志（级别危急 + 消息含故障词）。critical 闸门用：
    /// 设备级别标签不可信（"start NTP update" 被标 emergency 不算）。
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
    /// 找出消息含故障词的日志行（"[host] message"），供 warning 下限补进 issues。
    /// 只取消息本身、不看级别——"Transfer failed" 即使设备标成 info 也值得 warning。
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
