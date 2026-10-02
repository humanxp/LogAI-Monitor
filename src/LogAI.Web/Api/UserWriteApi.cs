// User create / update / delete.
//
// Permission rules copied from the Python handlers:
//   POST   admin only                         -> 403 {"error":"Access denied"}
//   PUT    admin OR self                      -> 403 when neither
//   DELETE admin only, never yourself          -> 400 {"error":"Cannot delete your own account"}
//
// Two details that are easy to get wrong:
//   * the role field is gated SEPARATELY: passing the "admin or self" check is
//     not enough, only an admin may change a role;
//   * deleting yourself is refused, which prevents locking every admin out.
//
// The session payload deliberately has no user id (changing it would invalidate
// the already verified cookie format), so "is this me?" is answered by resolving
// users:username:<session user> and comparing ids.

using System.Globalization;
using System.Text.Json.Nodes;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class UserWriteApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/users", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);
            if (!IsAdmin(session)) return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var data = await ReadObjectAsync(http);
            if (data is null) return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            string username = Text(data, "username").Trim();
            string password = Text(data, "password");
            string email = Text(data, "email").Trim();
            string role = Text(data, "role");
            if (role.Length == 0) role = "user";

            if (username.Length == 0 || password.Length == 0)
                return ReadApi.JsonBody(new { error = "Username and password are required" }, 400);
            if (password.Length < 6)
                return ReadApi.JsonBody(new { error = "Password must be at least 6 characters" }, 400);

            string existing = RedisStore.ToText(await store.Db.StringGetAsync(Keys.UserByName(username)));
            if (existing.Length > 0)
                return ReadApi.JsonBody(new { error = "Username already exists" }, 400);

            string id = "user:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
            await store.Db.HashSetAsync(id,
            [
                new HashEntry("id", id),
                new HashEntry("username", username),
                new HashEntry("password_hash", WerkzeugPasswordGenerator.Generate(password)),
                new HashEntry("email", email),
                new HashEntry("role", role),
                new HashEntry("created_at", now),
            ]);
            await store.Db.SetAddAsync(Keys.Users, id);
            await store.Db.StringSetAsync(Keys.UserByName(username), id);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["status"] = "ok",
            });
        });

        app.MapPut("/api/users/{userId}", async (HttpContext http, string userId) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);

            bool admin = IsAdmin(session);
            bool self = await IsSelfAsync(store, session.Username, userId);
            if (!admin && !self) return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var data = await ReadObjectAsync(http);
            if (data is null) return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            if (!await store.Db.KeyExistsAsync(userId))
                return ReadApi.JsonBody(new { error = "User not found" }, 404);

            var entries = new List<HashEntry>();
            if (data.ContainsKey("email")) entries.Add(new HashEntry("email", Text(data, "email").Trim()));

            string password = Text(data, "password");
            if (password.Length > 0)
            {
                if (password.Length < 6)
                    return ReadApi.JsonBody(new { error = "Password must be at least 6 characters" }, 400);
                entries.Add(new HashEntry("password_hash", WerkzeugPasswordGenerator.Generate(password)));
            }

            // Role changes need admin even when the caller is the account owner.
            if (admin && data.ContainsKey("role")) entries.Add(new HashEntry("role", Text(data, "role")));

            if (entries.Count > 0) await store.Db.HashSetAsync(userId, entries.ToArray());
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });

        app.MapDelete("/api/users/{userId}", async (HttpContext http, string userId) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);
            if (!IsAdmin(session)) return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            if (await IsSelfAsync(store, session.Username, userId))
                return ReadApi.JsonBody(new { error = "Cannot delete your own account" }, 400);

            if (!await store.Db.KeyExistsAsync(userId))
                return ReadApi.JsonBody(new { error = "User not found" }, 404);

            string username = RedisStore.ToText(await store.Db.HashGetAsync(userId, "username"));
            await store.Db.KeyDeleteAsync(userId);
            await store.Db.SetRemoveAsync(Keys.Users, userId);
            if (username.Length > 0) await store.Db.KeyDeleteAsync(Keys.UserByName(username));

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });
    }

    private static bool IsAdmin(SessionPayload session) =>
        string.Equals(session.Role, "admin", StringComparison.Ordinal);

    private static async Task<bool> IsSelfAsync(RedisStore store, string sessionUser, string targetId)
    {
        string mine = RedisStore.ToText(await store.Db.StringGetAsync(Keys.UserByName(sessionUser)));
        return mine.Length > 0 && string.Equals(mine, targetId, StringComparison.Ordinal);
    }

    private static IResult Login(HttpContext http) =>
        Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

    private static string Text(JsonObject data, string name) =>
        data.TryGetPropertyValue(name, out JsonNode? node) && node is JsonValue value
            && value.TryGetValue(out string? text) ? text : "";

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
}
