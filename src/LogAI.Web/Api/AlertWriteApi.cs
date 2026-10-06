// Alert write endpoints.
//
// DELIBERATE SECURITY CHOICE: these three could be exposed to any logged-in user,
// @require_redis_api - no login at all. An anonymous POST to
// /api/alerts/acknowledge-all marks every alert acknowledged, which removes them
// from the operator's unacknowledged view: the field means "a human has seen
// this", so an anonymous caller being able to set it destroys its meaning.
//
// These require a session instead. Admin is NOT required: acknowledging is a
// normal operator action, but they are gated to admins anyway.

using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class AlertWriteApi
{
    public static void Map(WebApplication app, RedisStore store, LogArchive archive, SessionCookie cookies)
    {
        app.MapPost("/api/alerts/{alertId}/acknowledge", async (HttpContext http, string alertId) =>
        {
            if (RequireLogin(http, cookies) is { } denied) return denied;

            bool found = await AlertMaintenance.AcknowledgeAsync(store, archive, alertId);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            return found
                ? ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" })
                : ReadApi.JsonBody(new { error = "Alert not found" }, 404);
        });

        app.MapPost("/api/alerts/acknowledge-all", async (HttpContext http) =>
        {
            if (RequireLogin(http, cookies) is { } denied) return denied;

            long count = await AlertMaintenance.AcknowledgeAllAsync(store, archive);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["acknowledged"] = count,
                ["status"] = "ok",
            });
        });

        app.MapPost("/api/alerts/clear", async (HttpContext http) =>
        {
            if (RequireLogin(http, cookies) is { } denied) return denied;

            long count = await AlertMaintenance.ClearAcknowledgedAsync(store, archive);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = count,
                ["status"] = "ok",
            });
        });
    }

    private static IResult? RequireLogin(HttpContext http, SessionCookie cookies) =>
        AuthApi.CurrentUser(http, cookies) is null
            ? Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path))
            : null;
}
