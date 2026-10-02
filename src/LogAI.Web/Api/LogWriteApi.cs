// Log write endpoints.
//
// Contract notes (from the Python source):
//   POST /api/logs/delete-source  login + admin, 403 {"error":"Access denied"},
//                                 400 {"error":"source required"},
//                                 -> {"client_deleted":bool,"deleted":N,"status":"ok"}
//   POST /api/logs/clear          -> {"deleted":N,"status":"ok"}
//
// DELIBERATE DIVERGENCE: the Python /api/logs/clear carries NO authentication at
// all (only @require_redis_api), so an anonymous POST wipes every log in the
// database. This implementation requires login plus admin. Reproducing an
// anonymous data-destruction endpoint is not "preserving functionality"; say the
// word and it can be relaxed, but it is not the default.

using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class LogWriteApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/logs/clear", async (HttpContext http) =>
        {
            if (RequireAdmin(http, cookies) is { } denied) return denied;

            long deleted = await LogMaintenance.ClearAllAsync(store);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            await LogMaintenance.DeleteAllClientsAsync(store);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = deleted,
                ["status"] = "ok",
            });
        });

        app.MapPost("/api/logs/delete-source", async (HttpContext http) =>
        {
            if (RequireAdmin(http, cookies) is { } denied) return denied;

            string raw = await new StreamReader(http.Request.Body).ReadToEndAsync();
            var body = ReadApi.ParseSortedObject(raw);
            string source = RedisStore.ToText(body.GetValueOrDefault("source")).Trim();
            if (source.Length == 0) return ReadApi.JsonBody(new { error = "source required" }, 400);

            long deleted = await LogMaintenance.DeleteBySourceAsync(store, source);
            bool clientDeleted = await LogMaintenance.DeleteClientAsync(store, source);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["client_deleted"] = clientDeleted,
                ["deleted"] = deleted,
                ["status"] = "ok",
            });
        });
    }

    /// <summary>Null when allowed, otherwise the rejection to return.</summary>
    private static IResult? RequireAdmin(HttpContext http, SessionCookie cookies)
    {
        var session = AuthApi.CurrentUser(http, cookies);
        if (session is null) return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
        if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
            return ReadApi.JsonBody(new { error = "Access denied" }, 403);
        return null;
    }
}
