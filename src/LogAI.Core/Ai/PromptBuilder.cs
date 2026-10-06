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
                                    int sampleLimit = 0)
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
            builder.Append('[').Append(severity).Append("] [").Append(host).Append("] ")
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

    public static string BatchPrompt(string logSummary) => $"""
You are a syslog security/health analyzer. Assess the logs below.

LOGS:
{logSummary}

Reply with ONLY a JSON object having exactly these keys:
- "overall_status": rate the batch by its SINGLE WORST issue, one of "healthy", "warning", "critical".
  * "critical" = a host/server is DOWN or UNREACHABLE right now, a confirmed security breach (break-in, malware, credential theft), or data loss. Nothing else qualifies.
  * "warning" = real problems that are NOT an outage or breach (a service failed to restart, disk filling up, repeated DNS/cURL errors, permission failures, master-browser election failures).
  * "healthy" = only routine / informational messages.
  How MANY issues there are must NOT change the rating. A long list of minor, repetitive or service-restart messages is "warning", never "critical". If you are unsure, use "warning".
- "issues_found": array of short "[HOST] description" strings, one per DISTINCT problem (at most 8). Do NOT copy raw log lines; summarize each distinct pattern in one short line (max 150 chars). Empty array if none
- "critical_count": integer, how many DISTINCT issues meet the strict "critical" definition above (host down/unreachable, confirmed breach, data loss). This is normally 0. NEVER count warnings, retries, "already registered", "Sleeping!", service-restart chatter, or benign repeated messages here
- "recommendations": array of short "[HOST] action" strings (actions to fix the issues), at most 5. Empty array if none
- "affected_hosts": array of bare hostname/IP strings involved (no brackets), empty array if none
- "alert_message": short admin alert if critical, else ""

Base everything ONLY on the logs given. Keep the JSON compact. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.
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
