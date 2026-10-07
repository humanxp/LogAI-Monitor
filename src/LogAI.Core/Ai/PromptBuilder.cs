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
    /// 每行一条日志："[SEVERITY] [HOST] program: message"。与 python-legacy 版
    /// log_summary 完全一致：按级别从严重到轻微排序（同级保持先后），可用 sampleLimit
    /// 限行。不去重——把原始行原样交给模型。
    /// </summary>
    public static string LogSummary(IEnumerable<IReadOnlyDictionary<string, string>> logs, int sampleLimit = 0)
    {
        var ordered = logs
            .Select((log, index) => (log, index))
            .OrderBy(pair => SeverityRank(pair.log))
            .ThenBy(pair => pair.index)          // 同级按到达顺序，保持稳定
            .Select(pair => pair.log)
            .ToList();

        int shown = sampleLimit > 0 && ordered.Count > sampleLimit ? sampleLimit : ordered.Count;

        var builder = new StringBuilder();
        foreach (var log in ordered.Take(shown))
        {
            string severity = Safe(Field(log, "severity", "info"), 16).ToUpperInvariant();
            string host = Safe(Field(log, "hostname", Field(log, "source", "unknown")), 120);
            string program = Safe(Field(log, "program", "unknown"), 200);
            string message = Safe(Field(log, "message", ""), 200);
            builder.Append('[').Append(severity).Append("] [")
                   .Append(host).Append("] ")
                   .Append(program).Append(": ").Append(message).Append('\n');
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
    /// 批量提示词。恢复成与 python-legacy 版 services/ollama_analyzer.py 完全一致：
    /// 简单提示 + 日志 + 6 个 JSON 键，没有 few-shot 示例、没有前缀缓存优化。
    /// </summary>
    public static string BatchPrompt(string logSummary) => $$"""
You are a syslog security/health analyzer. Assess the logs below.

LOGS:
{{logSummary}}

Reply with ONLY a JSON object having exactly these keys:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short PROBLEM descriptions, one per DISTINCT problem (at most 8). Each starts with "[HOST] " (the exact hostname or IP). Summarize the problem, do NOT copy the raw log line. Example: "[192.168.50.3] /dev/ipmi0 open failed repeatedly". Empty array if none.
- "critical_count": integer, count of DISTINCT critical problems, 0 if none
- "recommendations": array of short FIX ACTIONS (at most 5). Each starts with "[HOST] " (the exact hostname or IP to act on) and says what an admin should DO, using a verb such as check/remove/restart/fix/increase/review. Example: "[192.168.50.3] Check /dev/ipmi0 permissions". Do NOT copy raw log lines or repeat the problem here. Empty array if none.
- "affected_hosts": array of bare hostname/IP strings involved (no brackets), empty array if none
- "alert_message": short admin alert if critical, else ""

Base everything ONLY on the logs given. Keep the JSON compact. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.
""";

    /// <summary>
    /// 前缀缓存优化版批量提示词：静态规则 + few-shot 示例放在日志之前，让 omlx/vLLM
    /// 命中前缀缓存（反复调用时只算日志那一段，缓存命中率更高）。设置页开关「优化
    /// AI 模型缓存效率」开启时用；默认关闭（用上面的简单版 BatchPrompt）。
    /// </summary>
    public static string BatchPromptCached(string logSummary) => $$"""
You are a syslog security/health analyzer. You are given a batch of logs; rate the batch by its SINGLE WORST issue.

RATING RULES
- "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
- "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
- "healthy" = only routine / informational messages.
How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical". If you are unsure, use "warning".
The severity label on a line is NOT the rating: devices routinely mark routine chatter as "error" (see Example 1), and some even mark routine actions as "emergency" (see Example 4).
NEVER invent or extrapolate a failure. Judge ONLY by the literal message text: "start NTP update" means a routine NTP sync STARTED, not a failure. Do not rewrite "start X" / "Starting X" / "Finished X" / "Successfully acquired X" into "X failed", "X unreachable" or "X is down". A cron line like "cmd sleep N; /usr/bin/some_script.sh" means the script was SCHEDULED to run, NOT that it failed - do not rewrite it into "some_script.sh failed to run". Conversely, a message that LITERALLY says "failed", "cURL Error", "connection refused", "timed out", "No space left", or "unreachable" IS a real problem - rate the batch warning (or critical if it is a host down). The rule is: judge by the literal words in the message, never invent a failure, never ignore a stated one.

REPLY FORMAT - reply with ONE JSON object having exactly these keys and nothing else:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). NEVER repeat the same problem twice - merge all occurrences of one problem into a single entry. NEVER objects, never raw log lines. Empty array if none
- "critical_count": integer, how many DISTINCT issues are critical by the rule above (normally 0)
- "recommendations": array of short "[HOST] action" strings (at most 5), one concrete fix for each distinct problem in issues_found. If issues_found is non-empty, recommendations MUST be non-empty too; use [] ONLY when issues_found is []
- "affected_hosts": array of bare hostname/IP strings, no brackets. Empty array if none
- "alert_message": short admin alert if critical, else ""
The fields MUST agree with overall_status: a "healthy" batch has issues_found [], critical_count 0, recommendations [], alert_message "". A "warning" or "critical" batch has at least one issue in issues_found and at least one action in recommendations. Never list routine chatter (cron lines, startup messages, "already registered", "Sleeping!") in issues_found.
ALL 6 keys MUST appear in the output every time, even if their value is empty - never omit a key. Every array and object MUST be closed. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.

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
