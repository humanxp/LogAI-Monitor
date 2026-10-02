// Filter create / update / delete.
//
// Contract from the Python handlers:
//   POST   -> {"error":"No data provided"} 400 when the body is empty/falsy,
//             otherwise {"id":"filter:<ms>","status":"ok"}
//   PUT    -> 400 on an empty body, 404 {"error":"Filter not found"} when absent,
//             otherwise {"status":"ok"}
//   DELETE -> 404 when absent, otherwise {"status":"ok"}
//
// The id is "filter:<ms>" and filters:all stores that same full key (verified
// against the live instance), which is why FilterLoader must not prefix it again.
//
// Updates are a MERGE: only the posted fields are written, so the filters page
// can send a partial payload without dropping the conditions it did not include.
//
// DELIBERATE DIVERGENCE: Python guards all three with @require_redis_api only.
// Deleting every filter stops all alerting, so these require admin.

using System.Globalization;
using System.Text.Json.Nodes;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class FilterWriteApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/filters", async (HttpContext http) =>
        {
            if (RequireAdmin(http, cookies) is { } denied) return denied;

            var payload = await ReadObjectAsync(http);
            if (payload is null) return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            string id = "filter:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
            // created_at only: the Python create_filter does NOT write updated_at,
            // that field appears on the first update.
            var entries = new List<HashEntry> { new("id", id), new("created_at", now) };

            // Defaults, exactly as create_filter applies them.
            if (!payload.ContainsKey("enabled")) entries.Add(new HashEntry("enabled", "true"));
            if (!payload.ContainsKey("notify_telegram")) entries.Add(new HashEntry("notify_telegram", "false"));
            if (!payload.ContainsKey("notify_any_severity")) entries.Add(new HashEntry("notify_any_severity", "false"));
            entries.AddRange(Encode(payload));
            await store.Db.HashSetAsync(id, entries.ToArray());
            await store.Db.SetAddAsync(Keys.Filters, id);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["status"] = "ok",
            });
        });

        app.MapPut("/api/filters/{filterId}", async (HttpContext http, string filterId) =>
        {
            if (RequireAdmin(http, cookies) is { } denied) return denied;

            var payload = await ReadObjectAsync(http);
            if (payload is null) return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            string key = Normalize(filterId);
            if (!await store.Db.KeyExistsAsync(key))
                return ReadApi.JsonBody(new { error = "Filter not found" }, 404);

            var entries = Encode(payload);
            entries.RemoveAll(e => e.Name == "created_at");   // never overwritten on update
            entries.Add(new HashEntry("updated_at",
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)));
            await store.Db.HashSetAsync(key, entries.ToArray());
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });

        app.MapDelete("/api/filters/{filterId}", async (HttpContext http, string filterId) =>
        {
            if (RequireAdmin(http, cookies) is { } denied) return denied;

            string key = Normalize(filterId);
            if (!await store.Db.KeyExistsAsync(key))
                return ReadApi.JsonBody(new { error = "Filter not found" }, 404);

            await store.Db.KeyDeleteAsync(key);
            await store.Db.SetRemoveAsync(Keys.Filters, key);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });
    }

    private static string Normalize(string id) =>
        id.StartsWith("filter:", StringComparison.Ordinal) ? id : "filter:" + id;

    /// <summary>Null when the body is not a non-empty JSON object (the 400 case).</summary>
    private static async Task<JsonObject?> ReadObjectAsync(HttpContext http)
    {
        string raw = await new StreamReader(http.Request.Body).ReadToEndAsync();
        try
        {
            return JsonNode.Parse(raw) is JsonObject obj && obj.Count > 0 ? obj : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Storage encoding, taken from the live data: only conditions and the three
    /// boolean flags are stored as JSON - exactly the four keys the Python
    /// get_filter decodes. Everything else (name, id, timestamps) is stored as a
    /// plain string. JSON-encoding the name produced quoted \uXXXX values that
    /// the UI displayed literally.
    /// </summary>
    private static readonly HashSet<string> JsonFields = new(StringComparer.Ordinal)
    {
        "conditions", "enabled", "notify_telegram", "notify_any_severity",
    };

    private static List<HashEntry> Encode(JsonObject payload)
    {
        var entries = new List<HashEntry>(payload.Count);
        foreach (var pair in payload)
        {
            string stored = JsonFields.Contains(pair.Key)
                ? pair.Value?.ToJsonString() ?? "null"
                : pair.Value is JsonValue plain && plain.TryGetValue(out string? text)
                    ? text
                    : pair.Value?.ToJsonString() ?? "";
            entries.Add(new HashEntry(pair.Key, stored));
        }
        return entries;
    }

    private static IResult? RequireAdmin(HttpContext http, SessionCookie cookies)
    {
        var session = AuthApi.CurrentUser(http, cookies);
        if (session is null)
            return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
        if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
            return ReadApi.JsonBody(new { error = "Access denied" }, 403);
        return null;
    }
}
