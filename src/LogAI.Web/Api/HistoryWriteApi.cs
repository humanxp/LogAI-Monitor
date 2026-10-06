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
    public static void Map(WebApplication app, RedisStore store, LogArchive archive, SessionCookie cookies)
    {
        app.MapDelete("/api/ai-history/{historyId}", async (HttpContext http, string historyId) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);

            // 守卫必须同时看冷库：归档后的条目在 Redis 里已经没有 key，只查 Redis 会
            // 直接返回 404 且什么都不删——实测那条会永远留在 SQLite 与时间线里。
            bool inRedis = await store.Db.KeyExistsAsync(historyId);
            bool inArchive = await archive.HashExistsAsync(historyId);
            if (!inRedis && !inArchive)
                return ReadApi.JsonBody(new { error = "History entry not found" }, 404);

            if (inRedis) await store.Db.KeyDeleteAsync(historyId);
            await store.Db.SortedSetRemoveAsync(Keys.AiHistoryTimeline, historyId);
            // 独立状态哈希 + 冷归档里的同一条也要删，否则残留。
            await store.Db.HashDeleteAsync(Keys.AiHistoryStatus, historyId);
            await archive.DeleteHashAsync(historyId);
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok" });
        });

        app.MapDelete("/api/ai-history", async (HttpContext http) =>
        {
            var session = AuthApi.CurrentUser(http, cookies);
            if (session is null) return Login(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AiHistoryTimeline, 0, -1);
            // 删除分批管道化:原来逐条 DEL,1.5 万条历史就是 1.5 万次串行往返。
            // 计数保持原语义:时间线上取到的每条都计入 deleted。
            long deleted = 0;
            const int Chunk = 500;
            for (int offset = 0; offset < ids.Length; offset += Chunk)
            {
                int size = Math.Min(Chunk, ids.Length - offset);
                var batch = store.Db.CreateBatch();
                var deletes = new Task<bool>[size];
                for (int i = 0; i < size; i++)
                    deletes[i] = batch.KeyDeleteAsync(ids[offset + i].ToString());
                batch.Execute();
                await Task.WhenAll(deletes);
                deleted += size;
            }
            await store.Db.KeyDeleteAsync(Keys.AiHistoryTimeline);
            // 状态哈希整条清掉（只装 ai_history 的 id），冷归档按前缀清。
            await store.Db.KeyDeleteAsync(Keys.AiHistoryStatus);
            long archived = await archive.DeleteHashesByPrefixAsync("ai_history:");

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deleted"] = deleted,
                ["archived_deleted"] = archived,
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
