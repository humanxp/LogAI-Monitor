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
- "recommendations": array of short FIX ACTIONS (at most 5), ONE concrete fix for EACH distinct problem in issues_found. Each starts with "[HOST] " (the exact hostname or IP to act on) and says what an admin should DO, using a verb such as check/remove/restart/fix/increase/review. Example: "[192.168.50.3] Check /dev/ipmi0 permissions". If issues_found is non-empty, recommendations MUST also be non-empty (at least one fix per issue). Do NOT copy raw log lines or repeat the problem here. Empty array ONLY when issues_found is empty.
- "affected_hosts": array of bare hostname/IP strings involved (no brackets), empty array if none
- "alert_message": short admin alert if critical, else ""

Base everything ONLY on the logs given. Keep the JSON compact. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.
""";

    /// <summary>
    /// 通用缓存版批量提示词：静态规则 + few-shot 示例放在日志之前，让 omlx/vLLM
    /// 命中前缀缓存（反复调用时只算日志那一段，缓存命中率更高）。few-shot 按
    /// Qwen3.5-9B 时代的行为调校。
    ///
    /// 现在它是**历史值 ai_cache_optimized=true 与任何未知 ai_prompt_mode 的落点**，
    /// 保证升级后老配置的提示词逐字节不变。qwen25/qwen35/qwen36 三个模式各有自己的
    /// 优化模板（见下面三个方法），不再共用这一份。
    /// </summary>
    public static string BatchPromptCached(string logSummary) => $$"""
You are a syslog security/health analyzer. You are given a batch of logs; rate the batch by its SINGLE WORST issue.

RATING RULES
- "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
- "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
- "healthy" = only routine / informational messages.
How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical". If you are unsure, use "warning".
The severity label on a line is NOT the rating: devices routinely mark routine chatter as "error" (see Example 1), and some even mark routine actions as "emergency" (see Example 4).
NEVER invent or extrapolate a failure. Judge ONLY by the literal words: "start NTP update" means a routine sync STARTED, not a failure; a cron line means the script was SCHEDULED, not failed. A message that LITERALLY says "failed" / "cURL Error" / "connection refused" / "timed out" / "No space left" / "unreachable" IS a real problem - rate the batch warning (or critical if a host is down). Never invent a failure, never ignore a stated one.

REPLY FORMAT - reply with ONE JSON object having exactly these keys and nothing else:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). NEVER repeat the same problem twice - merge all occurrences of one problem into a single entry. NEVER objects, never raw log lines. Empty array if none
- "critical_count": integer, how many DISTINCT issues are critical by the rule above (normally 0)
- "recommendations": array of short FIX ACTIONS (at most 5), ONE concrete fix for EACH distinct problem in issues_found. Each starts with "[HOST] " (the exact hostname or IP to act on) and says what an admin should DO, using a verb such as check/remove/restart/fix/increase/review. If issues_found is non-empty, recommendations MUST be non-empty too; use [] ONLY when issues_found is []
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

    /// <summary>
    /// qwen36 = Qwen3.6-35B-A3B 专用：精度本来就 51/51 满分，所以不动任何判定规则，
    /// 只加「输出精简」+「问题 vs 噪声」两段。收益是护栏（防止未来批次把例行噪声升格成
    /// 问题）与约 7% 的 completion token 下降（这台单机推理机上 token 数就是时间）；
    /// 精度本身无可再升。实测 51/51 与旧版持平、无任何用例回归。
    /// </summary>
    public static string BatchPromptQwen36(string logSummary) => $$"""

You are a syslog security/health analyzer. You are given a batch of logs; rate the batch by its SINGLE WORST issue.

RATING RULES
- "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
- "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
- "healthy" = only routine / informational messages.
How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical". If you are unsure, use "warning".
The severity label on a line is NOT the rating: devices routinely mark routine chatter as "error" (see Example 1), and some even mark routine actions as "emergency" (see Example 4).
NEVER invent or extrapolate a failure. Judge ONLY by the literal words: "start NTP update" means a routine sync STARTED, not a failure; a cron line means the script was SCHEDULED, not failed. A message that LITERALLY says "failed" / "cURL Error" / "connection refused" / "timed out" / "No space left" / "unreachable" IS a real problem - rate the batch warning (or critical if a host is down). Never invent a failure, never ignore a stated one.


BREVITY (this monitor runs every 2 minutes - a compact reply is a faster reply)
- Keep every entry SHORT: an issues_found entry is at most about 12 words; a recommendation is at most about 10 words and must NOT restate the problem, only the action.
- Brevity must never drop a REAL problem (see PROBLEM vs NOISE below), and must never promote noise into an issue.
- Do not invent extra keys and do not repeat a key: exactly the 6 keys listed below, once each. Close every array and object.


PROBLEM vs NOISE - WHAT COUNTS AS AN ISSUE
- An issue is ONLY one of: a literal failure ("failed", "refused", "timed out", "No space left", "unreachable", "I/O error", "aborted"), a host or service that is down, a confirmed security event, or data loss.
- Repetition is NOT a problem by itself. Many identical routine lines - "crond: USER root pid N cmd ...", startup/lifecycle messages, "Sleeping!", "AP-STA-CONNECTED", "Finished <service>" - are noise: never list them, and never infer "process already running", "duplicate execution", "running concurrently" or "restart loop" from them.
- Scan EVERY line before answering: 40 lines of noise must not hide ONE "connection refused" - that batch is "warning" with that one issue, never "healthy".
- Keep different problems separate: one entry per (host, problem). The same symptom on two hosts is two entries; two different symptoms on one host are two entries.

REPLY FORMAT - reply with ONE JSON object having exactly these keys and nothing else:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). NEVER repeat the same problem twice - merge all occurrences of one problem into a single entry. NEVER objects, never raw log lines. Empty array if none
- "critical_count": integer, how many DISTINCT issues are critical by the rule above (normally 0)
- "recommendations": array of short FIX ACTIONS (at most 5), ONE concrete fix for EACH distinct problem in issues_found. Each starts with "[HOST] " (the exact hostname or IP to act on) and says what an admin should DO, using a verb such as check/remove/restart/fix/increase/review. If issues_found is non-empty, recommendations MUST be non-empty too; use [] ONLY when issues_found is []
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

    /// <summary>
    /// qwen35 = Qwen3.5-9B。模板内容与 qwen25 **完全一致**：2026-10-08 用
    /// --analysis-bench（15 条带标准答案的用例）实测，9B 配这份短模板是
    /// exact 13/15、critical 召回 6/6；配原来的长模板（安全段 + 问题/噪声段 +
    /// Example 5）只有 10/15、召回 3/6——三次复测里它没一次把"纯失败爆破"判成
    /// critical，还出现过"证书 7 天后过期 → healthy 且 issues 为空"。
    /// 长模板对 9B 是负收益，所以保留模式名（已存设置里的 "qwen35" 继续有效、
    /// 下拉框标签不变），内容换成短的那份。不要"顺手"把长模板加回来。
    /// </summary>
    public static string BatchPromptQwen35(string logSummary) => BatchPromptQwen25(logSummary);

    /// <summary>
    /// gemma = gemma-3-12b-it-4bit。基线复用短模板，另在安全判据里补一条：
    /// 未成功的爆破同样是"正在进行的攻击"→ critical。实测（--analysis-bench，
    /// 3 次复测）gemma 配纯短模板会把"同一来源 8 次 Failed password"稳定判成
    /// warning，是三个模型里唯一低估爆破的（7B/9B 都判 critical）；单独强调
    /// 这一条后 critical 召回从 3/6 回到 6/6。插在杀毒那一行之后（锚定行内容，
    /// 不用行号，模板往后改动这里也不断）。
    /// </summary>
    public static string BatchPromptGemma(string logSummary)
    {
        string extra = "\n- Repeated \"Failed password\" / \"Failed publickey\" attempts from a single source are an ACTIVE brute-force attack - reply \"critical\" with a non-empty alert_message, even if NO login has succeeded yet. Never soften this to \"warning\". (\"Invalid user\" probes WITHOUT password attempts stay \"warning\" - they are reconnaissance, not a password attack.)";
        string anchor = "- A \"FOUND\" / \"Trojan\" / \"Virus\" / \"Malware\" / \"Infected\" line from an antivirus is confirmed malware - reply \"critical\".";
        return BatchPromptQwen25(logSummary).Replace(anchor, anchor + extra, StringComparison.Ordinal);
    }

    /// <summary>
    /// qwen25 = Qwen2.5-Coder-7B-Instruct 专用：**只**在评级规则里追加两条最短的安全判据。
    /// 实测这个 7B 对长提示词敏感——给它加长"问题/噪声"段或更多 few-shot 示例，会反过来
    /// 让它把噪声里唯一的真故障判成 healthy（signal_in_noise 3/3 → 0/3）、并过度合并两个
    /// 不同问题（two_problems 2 条并成 1 条）。所以这里刻意保持最短：就补两条它本来最缺的
    /// 安全规则（爆破成功=critical、杀毒报毒=critical），其余一律不动，判别用例 21/21。
    /// 后来又为"一长串噪声里藏一个真问题"把 "How MANY issues…" 那句补了半句（单条
    /// 真故障不因周围噪声而降档）：只扩既有句子、不新增段落，7B needle ×5 全判 critical
    /// 且全语料无回退，验证了"不新增段落"这条约束仍然成立。
    /// </summary>
    public static string BatchPromptQwen25(string logSummary) => $$"""

You are a syslog security/health analyzer. You are given a batch of logs; rate the batch by its SINGLE WORST issue.

RATING RULES
- "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
- "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
- "healthy" = only routine / informational messages.

- A successful remote login ("Accepted password" / "Accepted publickey" / "session opened for user ... by (uid=0)") after repeated "Failed password" / "invalid user" lines is a CONFIRMED BREAK-IN - reply "critical" with a non-empty alert_message. Never soften this to "warning".
- A "FOUND" / "Trojan" / "Virus" / "Malware" / "Infected" line from an antivirus is confirmed malware - reply "critical".
How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical" — but if ANY single line in the batch is a confirmed breach, malware, data loss, or a host that is down, the batch is "critical", no matter how many routine lines surround it. If you are unsure, use "warning".
The severity label on a line is NOT the rating: devices routinely mark routine chatter as "error" (see Example 1), and some even mark routine actions as "emergency" (see Example 4).
NEVER invent or extrapolate a failure. Judge ONLY by the literal words: "start NTP update" means a routine sync STARTED, not a failure; a cron line means the script was SCHEDULED, not failed. A message that LITERALLY says "failed" / "cURL Error" / "connection refused" / "timed out" / "No space left" / "unreachable" IS a real problem - rate the batch warning (or critical if a host is down). Never invent a failure, never ignore a stated one.

REPLY FORMAT - reply with ONE JSON object having exactly these keys and nothing else:
- "overall_status": one of "healthy", "warning", "critical"
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). NEVER repeat the same problem twice - merge all occurrences of one problem into a single entry. NEVER objects, never raw log lines. Empty array if none
- "critical_count": integer, how many DISTINCT issues are critical by the rule above (normally 0)
- "recommendations": array of short FIX ACTIONS (at most 5), ONE concrete fix for EACH distinct problem in issues_found. Each starts with "[HOST] " (the exact hostname or IP to act on) and says what an admin should DO, using a verb such as check/remove/restart/fix/increase/review. If issues_found is non-empty, recommendations MUST be non-empty too; use [] ONLY when issues_found is []
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
    /// <summary>
    /// 模式 → 模板的唯一入口（AnalysisRunner / AnalyzeApi 两个批次路径都走这里）。
    ///
    /// 四个模式各有自己的模板：default（Llama3.2 简单版，不变）、qwen25、qwen35、qwen36。
    /// 兼容性刻意保持：空值与 "default" → 简单版；
    /// **其它任何未知值 → BatchPromptCached**，与"升级前 ai_prompt_mode 是任意非 default
    /// 值都走缓存版"的老行为完全一致，避免老配置升级后提示词悄悄变样。
    /// </summary>
    public static string BatchPromptFor(string? mode, string logSummary)
    {
        string normalized = (mode ?? "").Trim().Trim('"').ToLowerInvariant();
        if (normalized.Length == 0 || normalized == "default") return BatchPrompt(logSummary);
        return normalized switch
        {
            "qwen25" => BatchPromptQwen25(logSummary),
            "qwen35" => BatchPromptQwen35(logSummary),
            "qwen36" => BatchPromptQwen36(logSummary),
            "gemma" => BatchPromptGemma(logSummary),
            _ => BatchPromptCached(logSummary),
        };
    }

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
