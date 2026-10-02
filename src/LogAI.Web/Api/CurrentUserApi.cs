// GET /api/users/current
//
// The field set differs from the /api/users list items, which is easy to miss:
//
//   list:    created_at email id role username
//   current: email id is_admin role username
//
// current carries the computed is_admin and omits created_at. Reusing the list
// whitelist here would drop is_admin and the dashboard would stop showing the
// admin-only navigation to an admin.

using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class CurrentUserApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapGet("/api/users/current", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return ReadApi.JsonBody(new { error = "Unauthorized" }, 401);

            string id = RedisStore.ToText(await store.Db.StringGetAsync(Keys.UserByName(session.Username)));
            if (id.Length == 0) return ReadApi.JsonBody(new { error = "User not found" }, 404);

            var hash = await store.Db.HashGetAllAsync(id);
            var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

            string role = fields.GetValueOrDefault("role") ?? "viewer";
            string username = fields.GetValueOrDefault("username") ?? session.Username;

            // Insertion order is the alphabetical order Flask emits.
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["email"] = fields.GetValueOrDefault("email") ?? "",
                ["id"] = id,
                ["is_admin"] = string.Equals(role, "admin", StringComparison.Ordinal),
                ["role"] = role,
                ["username"] = username,
            };
            return ReadApi.JsonBody(payload);
        });
    }
}
