// AI history deletion and connected-client removal.
//
// Contracts:
//   DELETE /api/ai-history/<id>  -> {"status":"ok"} | 404 {"error":"History entry not found"}
//   DELETE /api/ai-history       -> {"deleted":<count>,"status":"ok"}   (count, not a bool)
//   DELETE /api/syslog/clients/<ip> -> {"deleted":<bool>}               (BOOL, not a count)
//
// The two "deleted" fields have different types, which is a trap for anything
// written against the endpoints generically.
//
// DELIBERATE DIVERGENCE: both ai-history routes carry only @require_redis_api in
// so an anonymous caller could erase the entire analysis record - the only
// evidence of what the model concluded. Deleting one entry requires a session,
// clearing everything requires admin.

using System.Globalization;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class HistoryWriteApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapDelete("/api/ai-history/{historyId}", async (HttpContext http, string historyId) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);

            if (!await store.Db.KeyExistsAsync(historyId))
                return ReadApi.JsonBody(new { error = "History entry not found" }, 404);

            await store.Db.KeyDeleteAsync(historyId);
            await store.Db.SortedSetRemoveAsync(Keys.AiHistoryTimeline, historyId);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });

        app.MapDelete("/api/ai-history", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1);
            long deleted = 0;
            foreach (var id in ids)
            {
                await store.Db.KeyDeleteAsync(id.ToString());
                deleted++;
            }
            await store.Db.KeyDeleteAsync(Keys.AiHistoryTimeline);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = deleted,
                ["status"] = "ok",
            });
        });

        app.MapDelete("/api/syslog/clients/{clientIp}", async (HttpContext http, string clientIp) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);

            bool removed = await LogMaintenance.DeleteClientAsync(store, clientIp);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = removed,          // bool here, counting elsewhere
            });
        });
    }

    private static IResult Login(HttpContext http) =>
        Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
}
