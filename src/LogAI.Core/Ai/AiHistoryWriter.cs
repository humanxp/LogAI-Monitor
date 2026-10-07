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
        string status;
        if (single)
        {
            // 单条分析：走模型的 is_critical / category（保持原逻辑）。
            status = AiStatusClassifier.Classify(type, analysis, allowCritical: true);
        }
        else
        {
            // 方向 A：批分析的 status / critical_count / issues 完全由程序按原始日志
            // 确定性判定，不看模型的 overall_status（3B 模型只会 healthy/critical 两极）。
            // 模型只负责 recommendations（找问题与给建议）。
            var (failureLines, criticalCount) = await ScanBatchAsync(logIds);
            status = AiStatusClassifier.ClassifyFromLogs(criticalCount > 0, failureLines.Count > 0);

            if (analysis is JsonObject obj)
            {
                obj["overall_status"] = status;
                obj["critical_count"] = status == "critical" ? Math.Min(criticalCount, 8) : 0;

                // issues = 去重后的真故障行（程序扫出的、消息含故障词的行），封顶 8 条；
                // 例行消息（crond / already registered / Sleeping! / Load template 等）
                // 不含故障词，自然被过滤掉。
                var issueArray = new JsonArray();
                foreach (string line in Dedupe(failureLines).Take(8))
                    issueArray.Add(line);
                obj["issues_found"] = issueArray;

                // 建议交给模型生成（针对检出的故障行）。
                if (recommendAsync is not null && failureLines.Count > 0)
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
    /// 一次读完整批日志的 severity + hostname + message，返回：
    /// - FailureLines：消息含故障词的行（"[host] message"，供 issues 与模型建议）；
    /// - CriticalCount：真正 critical 的行数（级别 emergency/alert/critical 且消息含故障词）。
    /// 方向 A 的 status 判定完全靠它、不看模型的 overall_status。级别标签不可信——
    /// 线上有设备把 "start NTP update" 标成 emergency，只凭级别会被骗，见
    /// AiStatusClassifier.IsGenuinelyCritical。
    /// </summary>
    private async Task<(List<string> FailureLines, int CriticalCount)> ScanBatchAsync(IReadOnlyList<string> logIds)
    {
        var failureLines = new List<string>();
        int criticalCount = 0;
        if (logIds.Count == 0) return (failureLines, criticalCount);
        const int Chunk = 500;
        for (int offset = 0; offset < logIds.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, logIds.Count - offset);
            var batch = store.Db.CreateBatch();
            var sevReads = new Task<RedisValue>[size];
            var hostReads = new Task<RedisValue>[size];
            var msgReads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
            {
                sevReads[i] = batch.HashGetAsync(logIds[offset + i], "severity");
                hostReads[i] = batch.HashGetAsync(logIds[offset + i], "hostname");
                msgReads[i] = batch.HashGetAsync(logIds[offset + i], "message");
            }
            batch.Execute();
            var sevs = await Task.WhenAll(sevReads);
            var hosts = await Task.WhenAll(hostReads);
            var msgs = await Task.WhenAll(msgReads);
            for (int i = 0; i < size; i++)
            {
                string msg = msgs[i].ToString();
                if (AiStatusClassifier.IsGenuinelyCritical(sevs[i].ToString(), msg))
                    criticalCount++;
                if (AiStatusClassifier.HasFailureWord(msg))
                {
                    string host = hosts[i].ToString();
                    failureLines.Add("[" + (host.Length > 0 ? host : "?") + "] " + msg);
                }
            }
        }
        return (failureLines, criticalCount);
    }

    /// <summary>按原顺序去重字符串列表。</summary>
    private static List<string> Dedupe(List<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string line in lines)
            if (seen.Add(line)) result.Add(line);
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
