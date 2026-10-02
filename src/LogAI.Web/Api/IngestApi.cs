// POST /api/logs/ingest — the channel the Docker collector uses to push logs.
//
// Contract taken from the Python implementation:
//   * when LOG_INGEST_TOKEN is configured the request must carry it in the
//     X-Ingest-Token header, otherwise 401 {"error":"Unauthorized"};
//   * a body that is not a JSON object is 400 {"error":"No data provided"};
//   * a missing message is 400 {"error":"message required"};
//   * severity is lower-cased and falls back to "info" when not in the
//     allowed set; the message is capped at 20000 characters.

using System.Text.Json;
using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Web.Api;

internal static class IngestApi
{
    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal)
    {
        "emergency", "alert", "critical", "error", "warning", "notice", "info", "debug",
    };

    public static void Map(WebApplication app, RedisStore store, LogWriter writer)
    {
        string? configuredToken = Environment.GetEnvironmentVariable("LOG_INGEST_TOKEN");
        if (string.IsNullOrWhiteSpace(configuredToken)) configuredToken = null;

        app.MapPost("/api/logs/ingest", async (HttpContext http) =>
        {
            if (configuredToken is not null)
            {
                string provided = (http.Request.Headers["X-Ingest-Token"].ToString() ?? "").Trim();
                if (!string.Equals(provided, configuredToken, StringComparison.Ordinal))
                    return ReadApi.JsonBody(new { error = "Unauthorized" }, 401);
            }

            string body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(body);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return ReadApi.JsonBody(new { error = "No data provided" }, 400);
            }

            if (root.ValueKind != JsonValueKind.Object)
                return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            string severity = Clean(root, "severity", "info", 32).ToLowerInvariant();
            if (!Severities.Contains(severity)) severity = "info";

            string message = Clean(root, "message", "", 20000);
            if (message.Length == 0) return ReadApi.JsonBody(new { error = "message required" }, 400);

            string source = Clean(root, "source", "ingest", 256);
            string hostname = Clean(root, "hostname", source, 256);
            string program = Clean(root, "program", "ingest", 128);

            var entry = new SyslogEntry
            {
                Source = source,
                SourceType = "syslog",
                Hostname = hostname,
                Program = program,
                Facility = Clean(root, "facility", "unknown", 32),
                Severity = severity,
                Message = message,
                Timestamp = SyslogParser.NowTimestamp(),
            };
            string id = await writer.StoreAsync(entry);

            return ReadApi.JsonBody(new { status = "ok", id });
        });
    }

    private static string Clean(JsonElement root, string name, string fallback, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;
        string text = value.GetString() ?? fallback;
        return text.Length <= max ? text : text[..max];
    }
}
