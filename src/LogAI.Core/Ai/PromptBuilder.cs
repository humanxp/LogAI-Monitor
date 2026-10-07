// Prompt construction for batch analysis.
//
// The template is copied verbatim from services/ollama_analyzer.py: the wording
// is part of the contract, because the model's reply shape (and therefore the
// JSON the dashboard stores) depends on it.

using System.Text;

namespace LogAI.Core.Ai;

public static partial class PromptBuilder
{
    /// <summary>
    /// Neutralises characters that break JSON when a model echoes log content
    /// back verbatim: a raw line containing  disk "sda1" is full  used to make
    /// the whole reply unparseable, so quotes, backslashes and control
    /// characters are replaced before the text ever reaches the model.
    /// </summary>
    public static string Safe(string? value, int maxLen = 200)
    {
        if (value is null) return "";
        string text = value
            .Replace('\\', '/')
            .Replace('"', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        text = text.Trim();
        return text.Length <= maxLen ? text : text[..maxLen];
    }

    /// <summary>
    /// 日志在提示词里的优先级：越靠前越先交给模型。未知级别排最后，
    /// 这样"级别字段异常"的记录不会挤掉真正的严重问题。
    /// </summary>
    private static int SeverityRank(IReadOnlyDictionary<string, string> log) =>
        Field(log, "severity", "info").Trim().ToLowerInvariant() switch
        {
            "emerg" or "emergency" or "alert" or "crit" or "critical" or "fatal" => 0,
            "error" or "err" => 1,
            "warning" or "warn" => 2,
            "notice" => 3,
            "info" or "informational" => 4,
            "debug" => 5,
            _ => 6,
        };

    /// <summary>
    /// One line per log: [SEVERITY] [HOST] program: message.
    ///
    /// 顺序按级别从严重到轻微（同级保持原有先后），可用 sampleLimit 限制行数。
    /// 之所以要排序与限行：批次上限（每批分析条数）与送给模型的样本数是两个
    /// 独立的设置，界面上写着"每批按级别排序后取 N 行"，此前这里既没排序也没
    /// 限行——整批 1000 条会全部灌进提示词。当样本被截断时补一行提示，
    /// 让模型知道看到的是样本而不是全部，避免它给出"整体健康"的错误结论。
    /// </summary>
    public static string LogSummary(IEnumerable<IReadOnlyDictionary<string, string>> logs,
                                    int sampleLimit = 0, bool dedup = true)
    {
        var ordered = logs
            .Select((log, index) => (log, index))
            .OrderBy(pair => SeverityRank(pair.log))
            .ThenBy(pair => pair.index)          // 同级按到达顺序，保持稳定
            .Select(pair => pair.log)
            .ToList();

        int shown = sampleLimit > 0 && ordered.Count > sampleLimit ? sampleLimit : ordered.Count;

        var builder = new StringBuilder();
        if (dedup)
        {
            // 去重：把"只有 pid / 长数字 / IP / hex 不同"的例行消息折叠成一条 + ×N。
            // 设备的例行输出（crond USER root pid NNN、resolved endpoint 0x…）每条都带
            // 不同的 pid/会话号，但语义相同——给模型看几十条几乎一样的文本既不增加
            // 信息，又让提示词更长、前缀缓存更差（实测真实批次去重率约 61%）。重复次数
            // 用 ×N 保留（"反复出现"本身就是判断 warning 的信号）。
            var seen = new Dictionary<string, (int Count, string FirstLine)>(StringComparer.Ordinal);
            var order = new List<string>(shown);
            foreach (var log in ordered.Take(shown))
            {
                string host = Safe(Field(log, "hostname", Field(log, "source", "unknown")), 120);
                string program = Safe(Field(log, "program", "unknown"), 200);
                string message = Safe(Field(log, "message", ""), 200);
                string key = NormalizeKey(host, program, message);
                if (seen.TryGetValue(key, out var existing))
                {
                    seen[key] = (existing.Count + 1, existing.FirstLine);
                }
                else
                {
                    seen[key] = (1, "[" + host + "] " + program + ": " + message);
                    order.Add(key);
                }
            }
            foreach (string key in order)
            {
                var (count, firstLine) = seen[key];
                builder.Append(firstLine);
                if (count > 1) builder.Append("  [×").Append(count).Append(']');
                builder.Append('\n');
            }
        }
        else
        {
            // 不去重：逐条输出（同样不带 [SEVERITY] 前缀）。供设置页关闭去重时使用。
            foreach (var log in ordered.Take(shown))
            {
                string host = Safe(Field(log, "hostname", Field(log, "source", "unknown")), 120);
                string program = Safe(Field(log, "program", "unknown"), 200);
                string message = Safe(Field(log, "message", ""), 200);
                builder.Append('[').Append(host).Append("] ")
                       .Append(program).Append(": ").Append(message).Append('\n');
            }
        }
        string summary = builder.ToString().TrimEnd('\n');
        if (shown < ordered.Count)
        {
            summary += "\n[" + (ordered.Count - shown) + " more log(s) omitted: showing the "
                     + shown + " most severe]";
        }
        return summary;
    }

    /// <summary>
    /// 批量提示词。**静态块（规则 + few-shot 示例）必须在日志之前**——omlx/vLLM 的
    /// 前缀缓存按 256 token 块对齐、不足 256 不命中；静态块现在约 800+ token，
    /// 每批能稳定命中 3 个块。few-shot 的 token 成本近似为零（被缓存），
    /// 只有第一次调用付全价。
    ///
    /// 示例不是装饰：3B 小模型原先会把 issues_found 输出成 {"host":"msg"} 对象、
    /// 把 overall_status 写成 schema 里没有的 "error"，而且会把一长串例行 crond
    /// 记录（设备标成 error）当成 warning。示例 1 专门示范"标了 error 但其实是
    /// 例行噪音 → healthy"，示例 2/3 示范 critical/warning 的边界与 issues 的字符串格式。
    /// </summary>
    public static string BatchPrompt(string logSummary) => $$"""
You are a syslog security/health analyzer. You are given a batch of logs; rate the batch by its SINGLE WORST issue.

RATING RULES
- "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
- "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
- "healthy" = only routine / informational messages.
How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical". If you are unsure, use "warning".
The severity label on a line is NOT the rating: devices routinely mark routine chatter as "error" (see Example 1), and some even mark routine actions as "emergency" (see Example 4).
NEVER invent or extrapolate a failure. Judge ONLY by the literal message text: "start NTP update" means a routine NTP sync STARTED, not a failure. Do not rewrite "start X" / "Starting X" / "Finished X" / "Successfully acquired X" into "X failed", "X unreachable" or "X is down".

REPLY FORMAT - reply with ONE JSON object having exactly these keys and nothing else:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). NEVER objects, never raw log lines. Empty array if none
- "critical_count": integer, how many DISTINCT issues are critical by the rule above (normally 0)
- "recommendations": array of short "[HOST] action" strings (at most 5). Empty array if none
- "affected_hosts": array of bare hostname/IP strings, no brackets. Empty array if none
- "alert_message": short admin alert if critical, else ""
Every array and object MUST be closed. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.

EXAMPLES

Example 1 - routine chatter; the device marks lines "error" but nothing is actually wrong:
LOGS:
[ERROR] [routerA] crond: USER root pid 31556 cmd /usr/bin/wg-watchdog
[ERROR] [routerA] crond: USER root pid 31512 cmd sleep 50; /usr/bin/modem_sim_status_check.sh
[INFO] [routerA] hostapd: wlan0: AP-STA-CONNECTED 9c:5c:8e:11:22:33
[NOTICE] [nas1] Injector: Sleeping!
REPLY:
{
  "overall_status": "healthy",
  "issues_found": [],
  "critical_count": 0,
  "recommendations": [],
  "affected_hosts": [],
  "alert_message": ""
}

Example 2 - a service is down on a host that must be reachable:
LOGS:
[CRITICAL] [db01] systemd: mysqld.service: Main process exited, code=exited, status=1/FAILURE
[ERROR] [db01] mysqld: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock'
[ERROR] [web01] nagios: CRITICAL - db01:3306 - connection refused
REPLY:
{
  "overall_status": "critical",
  "issues_found": [
    "[db01] mysqld failed to start (socket unreachable)",
    "[db01] port 3306 refusing connections"
  ],
  "critical_count": 2,
  "recommendations": [
    "[db01] check mysqld logs and restart the service"
  ],
  "affected_hosts": [
    "db01"
  ],
  "alert_message": "db01 MySQL is down (3306 refused)"
}

Example 3 - a real problem that is not an outage:
LOGS:
[ERROR] [web02] nginx: connect() failed (111: Connection refused) while connecting to upstream
[WARNING] [web02] kernel: TCP: request_sock_TCP: Possible SYN flooding on port 443
[INFO] [web02] systemd: Started Session 9912 of user deploy.
REPLY:
{
  "overall_status": "warning",
  "issues_found": [
    "[web02] nginx upstream connection refused (repeated)"
  ],
  "critical_count": 0,
  "recommendations": [
    "[web02] check the service behind the nginx upstream"
  ],
  "affected_hosts": [
    "web02"
  ],
  "alert_message": ""
}

Example 4 - startup / lifecycle chatter; the device even marks a routine action as "emergency":
LOGS:
[INFO] [host1] hostd-probe: Glibc malloc guards disabled
[INFO] [host1] hostd-probe: Priority level 4 is now active
[INFO] [host1] hostd-probe: Successfully acquired hardware: M600
[INFO] [host1] hostd-probe: Finished sysstat-collect.service
[INFO] [host1] hostd-probe: Starting wg-watchdog.service
[EMERGENCY] [routerA] ntp: start NTP update
REPLY:
{
  "overall_status": "healthy",
  "issues_found": [],
  "critical_count": 0,
  "recommendations": [],
  "affected_hosts": [],
  "alert_message": ""
}

NOW ANALYZE THIS BATCH.
LOGS:
{{logSummary}}
""";

    /// <summary>Second attempt sent when the first reply could not be parsed.</summary>
    public static string CorrectivePrompt(string originalPrompt) => originalPrompt +
        "\n\nYour previous reply was NOT a single valid JSON object (e.g. missing separators / truncated). " +
        "Reply NOW with ONE complete, valid JSON object using exactly the keys above. Every string must be proper JSON.";

    private static string Field(IReadOnlyDictionary<string, string> log, string name, string fallback) =>
        log.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;

    /// <summary>
    /// 归一化去重的 key：去掉 pid、长数字、IP、hex 这些"每行都不同但无语义"的噪声，
    /// 让只有这些差异的行折叠成一条。只影响分组，不影响输出（输出保留首条原文）。
    /// </summary>
    private static string NormalizeKey(string host, string program, string message)
    {
        string m = message.ToLowerInvariant();
        m = System.Text.RegularExpressions.Regex.Replace(m, @"pid \d+", "pid N");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\d+\.\d+\.\d+\.\d+", "IP");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"[0-9a-f]{8,}", "HEX");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\d{4,}", "N");
        return host.ToLowerInvariant() + "|" + program.ToLowerInvariant() + "|" + m;
    }
}

// ---------------------------------------------------------------------------
// Single-log prompt. Same shape as the batch one but with its own field limits
// (message 1200 here, 200 for batch) and its own reply schema.

public static partial class PromptBuilderSingle
{
    /// <summary>
    /// 单条日志的提示词。顺序与 BatchPrompt 保持一致（字段在前、指令在后），
    /// 理由见 BatchPrompt 的注释。
    /// </summary>
    public static string Build(IReadOnlyDictionary<string, string> log) => $"""
You are a syslog security/health analyzer. Assess this single log entry.

Hostname/IP: {PromptBuilder.Safe(Field(log, "hostname", Field(log, "source", "unknown")), 120)}
Source: {PromptBuilder.Safe(Field(log, "source", "unknown"), 120)}
Severity: {PromptBuilder.Safe(Field(log, "severity", "unknown"), 32)}
Program: {PromptBuilder.Safe(Field(log, "program", "unknown"), 60)}
Message: {PromptBuilder.Safe(Field(log, "message", ""), 1200)}

Reply with ONLY a JSON object having exactly these keys:
- "is_critical": boolean (true if it needs immediate attention)
- "category": one of "security", "performance", "application", "system", "network", "other"
- "summary": short one-line summary based only on this log
- "recommendation": short action to take based only on this log, or ""
- "alert_user": boolean (true if the user should be notified)

No markdown, no extra text, only JSON.
""";

    private static string Field(IReadOnlyDictionary<string, string> log, string name, string fallback) =>
        log.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;
}
