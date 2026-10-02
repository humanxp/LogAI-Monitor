// User management endpoints.
//
// Security note: the stored hashes contain password_hash, which must never
// reach a client. The Python API strips it, and so does this — a naive
// "mirror the Redis hash" implementation would leak password hashes to any
// signed-in user, which is exactly the kind of difference a rewrite can
// introduce silently.

using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class UsersApi
{
    /// <summary>Fields the Python API exposes for a user record.</summary>
    private static readonly string[] ExposedFields =
        ["created_at", "email", "id", "role", "username"];

    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapGet("/api/users", async (HttpContext http) =>
        {
            // Admin only, mirroring the Python check at the top of the handler:
            // anonymous callers are sent to the login page, a signed-in
            // non-admin back to the dashboard. Without this the endpoint listed
            // every username, email and role to anyone who asked.
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return Results.Redirect("/");
            var ids = await store.Db.SetMembersAsync(Keys.Users);
            var users = new List<Dictionary<string, object?>>(ids.Length);

            foreach (var id in ids)
            {
                var hash = await store.Db.HashGetAllAsync(id.ToString());
                if (hash.Length == 0) continue;

                var stored = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string field in ExposedFields)
                    if (stored.TryGetValue(field, out string? value))
                        entry[field] = value;

                users.Add(entry);
            }

            users.Sort((a, b) => string.CompareOrdinal(
                a.GetValueOrDefault("created_at")?.ToString() ?? "",
                b.GetValueOrDefault("created_at")?.ToString() ?? ""));

            return ReadApi.JsonBody(users);
        });
    }
}
