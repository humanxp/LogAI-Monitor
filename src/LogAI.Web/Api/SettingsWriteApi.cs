// POST /api/settings - MERGE semantics.
//
// Ported from redis_client.save_settings: only the keys present in the payload
// are written, so a partial client payload (the Docker page posts just its
// exclusion list) can never wipe the Telegram / AI / retention configuration
// that was not part of it.
//
// Values are stored JSON encoded. A string
// becomes "abc", a number 42, a list ["a","b"] - GetSettingsAsync decodes the
// same way, so the two implementations interoperate on one hash.
//
// Tuning integers are clamped, so a bad UI value can never set a 0-minute
// interval or a negative retention.
//
// DELIBERATE SECURITY CHOICE: the settings write route used to be reachable without
// anonymous POST can repoint ollama_host (sending every analysed log to a
// foreign server) or replace telegram_bot_token (intercepting every alert).
// Settings change behaviour and data destinations, so this requires admin.

using System.Text.Json.Nodes;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class SettingsWriteApi
{
    private static readonly Dictionary<string, (int Lo, int Hi, int Fallback)> Ranges = new(StringComparer.Ordinal)
    {
        ["analysis_interval"] = (1, 1440, 2),
        ["max_logs_per_analysis"] = (10, 5000, 500),
        ["batch_sample_limit"] = (1, 5000, 200),
        ["log_retention_hours"] = (1, 8760, 720),
        ["archive_after_hours"] = (0, 8760, 168),
        ["alert_retention_days"] = (1, 3650, 30),
        ["health_watch_minutes"] = (1, 1440, 5),
    };

    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        // GET is part of the same resource; the raw settings hash is served
        // with no auth at all, which is how the Telegram bot token leaked. A
        // session is required here (documented divergence).
        app.MapGet("/api/settings", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

            var settings = await store.GetSettingsAsync();
            // jsonify sorts keys (sort_keys=True); a dictionary serializes in
            // insertion order, so the payload has to be rebuilt alphabetically or
            // the bytes differ even though every value matches.
            var ordered = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in settings.Keys.OrderBy(k => k, StringComparer.Ordinal))
                ordered[key] = settings[key];
            return ReadApi.JsonBody(ordered);
        });

        app.MapPost("/api/settings", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            string raw = await new StreamReader(http.Request.Body).ReadToEndAsync();
            JsonNode? parsed = null;
            try { parsed = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { }
            if (parsed is not JsonObject payload)
                return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            var entries = new List<HashEntry>();
            foreach (var pair in payload)
            {
                if (Ranges.TryGetValue(pair.Key, out var range))
                {
                    int value = pair.Value is not null && int.TryParse(pair.Value.ToString(), out int given)
                        ? given : range.Fallback;
                    entries.Add(new HashEntry(pair.Key, Math.Max(range.Lo, Math.Min(range.Hi, value)).ToString()));
                }
                else
                {
                    entries.Add(new HashEntry(pair.Key, pair.Value?.ToJsonString() ?? "null"));
                }
            }

            if (entries.Count > 0) await store.Db.HashSetAsync(Keys.Settings, entries.ToArray());
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });
    }
}
