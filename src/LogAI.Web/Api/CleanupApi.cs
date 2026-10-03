// POST /api/logs/cleanup - manual retention cleanup (login + admin).
//
// Response shape:
//   {"cleanup_status":{"last_removed":N,"last_run":<iso|null>,"timeline_count":N},
//    "deleted":N,"redis_connected":bool,"retention_hours":N,"status":"ok",
//    "timeline_count":N}
//
// retention_hours is ECHOED, not accepted as input: the endpoint always uses the
// configured value, and reporting it lets the operator confirm what was applied
// ("this ran with 720h") instead of guessing.
//
// Error split is preserved: Redis unavailable -> 503 with a message, anything
// else -> 500, so a client can tell "backend down" from "cleanup failed".

using System.Globalization;
using LogAI.Core.Auth;
using LogAI.Core.Scheduler;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class CleanupApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/logs/cleanup", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            int retention = 720;
            bool redisConnected;
            long deleted;
            try
            {
                var settings = await store.GetSettingsAsync();
                if (int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("log_retention_hours")), out int configured)
                    && configured > 0)
                    retention = configured;

                var result = await CleanupJob.RunAsync(store, retention);
                deleted = result.Removed;
                redisConnected = await store.Db.PingAsync() < TimeSpan.FromSeconds(5);
            }
            catch (RedisConnectionException ex)
            {
                return ReadApi.JsonBody(new { error = "Redis connection failed", message = ex.Message }, 503);
            }
            catch (Exception ex)
            {
                return ReadApi.JsonBody(new { error = "Cleanup failed", message = ex.Message }, 500);
            }

            long timelineCount = await store.Db.SortedSetLengthAsync(Keys.Timeline);
            string? lastRun = await store.Db.StringGetAsync(Keys.CleanupLastRun);
            string rawRemoved = await store.Db.StringGetAsync(Keys.CleanupLastRemoved);
            long lastRemoved = long.TryParse(rawRemoved, out long parsed) ? parsed : 0;

            var status = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["last_removed"] = lastRemoved,
                ["last_run"] = lastRun is { Length: > 0 } text ? text : null,
                ["timeline_count"] = timelineCount,
            };

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cleanup_status"] = status,
                ["deleted"] = deleted,
                ["redis_connected"] = redisConnected,
                ["retention_hours"] = retention,
                ["status"] = "ok",
                ["timeline_count"] = timelineCount,
            };
            return ReadApi.JsonBody(payload);
        });
    }
}
