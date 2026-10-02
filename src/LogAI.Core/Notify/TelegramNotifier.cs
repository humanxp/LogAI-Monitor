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

    /// <summary>Python html.escape(quote=True): &amp; &lt; &gt; &quot; &#x27;.</summary>
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

        if (!string.IsNullOrEmpty(analysis)) text.Append($"\n<b>AI Analysis:</b>\n{Escape(analysis)}");
        return text.ToString();
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
