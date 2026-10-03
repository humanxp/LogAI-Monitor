// Telegram message composition and delivery.
//
// The alert template is reproduced from services/telegram_notifier.py, including
// the leading and trailing newline of the f-string, the upper-cased severity,
// "hostname or source" fallback, and the 500/300 character caps. Byte-exact
// text matters here: the same message in a different shape looks like a
// different product to whoever reads the alerts.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAI.Core.Notify;

public sealed class TelegramNotifier(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public static string Emoji(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" or "emergency" or "alert" => "🔴",
        "error" => "🟠",
        "warning" => "🟡",
        "notice" => "🔵",
        "info" => "⚪",
        "debug" => "⚫",
        _ => "⚪",
    };

    /// <summary>HTML escaping (quote=True): &amp; &lt; &gt; &quot; &#x27;.</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#x27;",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    public static string BuildAlertText(string severity, string source, string message,
                                        string? hostname = null, string? analysis = null)
    {
        string hostDisplay = string.IsNullOrEmpty(hostname) ? source : hostname;
        string shortMessage = message.Length <= 500 ? message : message[..500];

        var text = new StringBuilder();
        text.Append('\n');
        text.Append($"{Emoji(severity)} <b>LogAI Monitor Alert</b>\n");
        text.Append('\n');
        text.Append($"<b>Severity:</b> {Escape(severity.ToUpperInvariant())}\n");
        text.Append($"<b>Host:</b> {Escape(hostDisplay)}\n");
        text.Append($"<b>Source:</b> {Escape(source)}\n");
        text.Append("<b>Message:</b>\n");
        text.Append($"<code>{Escape(shortMessage)}</code>\n");

        if (!string.IsNullOrEmpty(analysis))
        {
            string shortAnalysis = analysis.Length <= 300 ? analysis : analysis[..300];
            text.Append($"\n<b>AI Analysis:</b>\n{Escape(shortAnalysis)}");
        }

        return text.ToString();
    }

    public static string BuildSummaryText(JsonNode? stats, string? analysis = null)
    {
        int logsLastDay = stats?["logs_last_day"]?.GetValue<int>() ?? 0;
        int logsLastHour = stats?["logs_last_hour"]?.GetValue<int>() ?? 0;
        int activeAlerts = stats?["unacknowledged_alerts"]?.GetValue<int>() ?? 0;
        int sources = stats?["sources"] is JsonArray array ? array.Count : 0;

        var text = new StringBuilder();
        text.Append('\n');
        text.Append("📊 <b>LogAI Monitor Summary</b>\n");
        text.Append('\n');
        text.Append($"<b>Logs (24h):</b> {logsLastDay}\n");
        text.Append($"<b>Logs (1h):</b> {logsLastHour}\n");
        text.Append($"<b>Active Alerts:</b> {activeAlerts}\n");
        text.Append($"<b>Sources:</b> {sources}\n");

        if (string.IsNullOrEmpty(analysis)) return text.ToString();

        JsonNode? node;
        try { node = JsonNode.Parse(analysis); }
        catch (JsonException) { return text.ToString(); }   // never send a broken message
        if (node is null) return text.ToString();

        // Structured rendering; the summary line layout is fixed.
        string status = node["overall_status"]?.ToString() ?? "unknown";
        string emoji = status.ToLowerInvariant() switch
        {
            "healthy" => "✅",
            "warning" => "⚠️",
            "critical" => "🚨",
            _ => "❓",
        };
        text.Append('\n');
        text.Append($"<b>Status:</b> {emoji} {Escape(status).ToUpperInvariant()}\n");
        text.Append($"<b>Critical Issues:</b> {Escape(node["critical_count"]?.ToString() ?? "0")}\n");

        if (node["issues_found"] is JsonArray issues && issues.Count > 0)
        {
            text.Append("\n<b>Issues:</b>\n");
            foreach (var issue in issues.Take(5))
                text.Append($"• {Escape(Describe(issue))}\n");
        }
        if (node["recommendations"] is JsonArray recs && recs.Count > 0)
        {
            text.Append("\n<b>解决方法 (Recommendations):</b>\n");
            foreach (var rec in recs.Take(5))
                text.Append($"🔧 {Escape(Describe(rec))}\n");
        }
        if (node["affected_hosts"] is JsonArray hosts && hosts.Count > 0)
        {
            text.Append("\n<b>Affected Hosts:</b>\n");
            text.Append(string.Join(", ", hosts.Take(10).Select(h => Escape(Describe(h)))));
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Batch analyses sometimes return an issue as {"host": "message"} rather than a
    /// plain string; render that as "host: message" instead of raw JSON, which is what
    /// made the summary unreadable.
    /// </summary>
    private static string Describe(JsonNode? item)
    {
        if (item is null) return "";
        if (item is not JsonObject obj) return item.ToString();
        foreach (var pair in obj)
            return pair.Key + ": " + (pair.Value?.ToString() ?? "");
        return "";
    }

    /// <summary>Sends the message; returns false instead of throwing on failure.</summary>
    public async Task<bool> SendAsync(string token, string chatId, string text,
                                      CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId)) return false;

        string url = $"https://api.telegram.org/bot{token}/sendMessage";
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
        };

        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;                       // never let a notification failure break ingest
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
