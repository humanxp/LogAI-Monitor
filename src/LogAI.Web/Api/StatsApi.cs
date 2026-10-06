// GET /api/stats — the dashboard's polling endpoint (13 fields, alphabetical).
//
// logs_last_day / logs_last_hour / total_logs / total_alerts are live counts, so
// they drift by a few between two calls; the rest are configuration or index
// content and must match exactly.
//
// ollama_last_check_age is an UNROUNDED double in the payload; rounding it
// here would change the rendered "checked N seconds ago" text.

using LogAI.Core.Ai;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class StatsApi
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset? _lastCheck;
    private static bool _available;

    /// <summary>
    /// 冷库引用：统计"已确认告警"必须同时看 SQLite——告警参与冷热分层，归档后
    /// Redis 里没有哈希，只读 Redis 会把它们永远算作未确认（"全部确认"后角标
    /// 不归零）。PushIfNeeded 由采集/告警/历史等多处静态调用，逐层透传 archive 会
    /// 污染整条链路，故按 EngineIoServer.Current / ReceiverState.Receiver 的先例在
    /// 启动时注入一次。
    /// </summary>
    internal static LogArchive? Archive { get; set; }

    public static void Map(WebApplication app, RedisStore store, LogArchive archive)
    {
        Archive = archive;
        app.MapGet("/api/stats", async () => ReadApi.JsonBody(await BuildPayloadAsync(store)));
    }

    /// <summary>
    /// The dashboard payload, shared by the HTTP endpoint and the realtime
    /// broadcast - the builder is documented as "one dict used by
    /// both /api/stats and the realtime stats broadcast".
    /// </summary>
    internal static async Task<Dictionary<string, object?>> BuildPayloadAsync(RedisStore store)
    {
            var db = store.Db;
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            var settings = await store.GetSettingsAsync();
            string host = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
            string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
            if (provider.Length == 0) provider = "openai";
            string model = AiClient.ResolveModel(store);
            string token = RedisStore.ToText(settings.GetValueOrDefault("telegram_bot_token"));
            string chat = RedisStore.ToText(settings.GetValueOrDefault("telegram_chat_id"));

            // Never block the response on the inference endpoint. A cold availability
            // probe measured ~4s, and every page fetches /api/stats, so it stalled the
            // whole UI. Serve the last known value and refresh in the background - the
            // same shape the contract documents ("trigger a background refresh so
            // UI updates quickly"). age is -1 while nothing has been checked yet.
            bool available;
            double age;
            bool needRefresh;
            await Gate.WaitAsync();
            try
            {
                needRefresh = _lastCheck is null
                    || (DateTimeOffset.UtcNow - _lastCheck.Value).TotalSeconds > 5;
                available = _available;
                age = _lastCheck is null
                    ? -1
                    : (DateTimeOffset.UtcNow - _lastCheck!.Value).TotalSeconds;
                if (needRefresh) _lastCheck = DateTimeOffset.UtcNow;   // one refresh at a time
            }
            finally
            {
                Gate.Release();
            }

            if (needRefresh)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        bool ok = await new AiClient
                        {
                            Provider = provider,
                            BaseUrl = AiClient.NormalizeBaseUrl(host, provider),
                            Model = model,
                            ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
                        }.IsAvailableAsync();
                        await Gate.WaitAsync();
                        try { _available = ok; } finally { Gate.Release(); }
                    }
                    catch
                    {
                        // Availability is best-effort; the next payload triggers another try.
                    }
                });
            }

            // 七个计数/索引读取并成一个批:这个 payload 每个页面都会拉,
            // 实时推送每 2 秒也构建一次,原来七次串行往返都是纯等待。
            var countBatch = db.CreateBatch();
            var lastDayTask = countBatch.SortedSetLengthAsync(Keys.Timeline, now - 86400, now);
            var lastHourTask = countBatch.SortedSetLengthAsync(Keys.Timeline, now - 3600, now);
            var totalLogsTask = countBatch.SortedSetLengthAsync(Keys.Timeline);
            var totalAlertsTask = countBatch.SortedSetLengthAsync(Keys.AlertsTimeline);
            var totalFiltersTask = countBatch.SetLengthAsync(Keys.Filters);
            var severitiesTask = countBatch.SetMembersAsync(Keys.SeveritiesIndex);
            var sourcesTask = countBatch.SetMembersAsync(Keys.SourcesIndex);
            countBatch.Execute();

            long lastDay = await lastDayTask;
            long lastHour = await lastHourTask;
            long totalLogs = await totalLogsTask;
            long totalAlerts = await totalAlertsTask;
            long totalFilters = await totalFiltersTask;

            var severities = (await severitiesTask)
                .Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();
            var sources = (await sourcesTask)
                .Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();

            // One round trip instead of one per alert: the previous shape was N+1 and
            // grew with the alert count, on a payload both main pages request.
            long unacknowledged = 0;
            var alertIds = await db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
            if (alertIds.Length > 0)
            {
                if (Archive is not null)
                {
                    // 归档告警的 acknowledged 存在冷库；只读 Redis 会把它们永远算作
                    // 未确认，"全部确认"之后角标也就永远不归零。
                    foreach (var state in await AlertMaintenance.ReadAckStatesAsync(store, Archive, alertIds))
                        if (!state.Acknowledged) unacknowledged++;
                }
                else
                {
                    var batch = db.CreateBatch();
                    var reads = new Task<StackExchange.Redis.RedisValue>[alertIds.Length];
                    for (int i = 0; i < alertIds.Length; i++)
                        reads[i] = batch.HashGetAsync(alertIds[i].ToString(), "acknowledged");
                    batch.Execute();
                    foreach (var value in await Task.WhenAll(reads))
                        if (value.ToString() != "true") unacknowledged++;
                }
            }

            bool telegramEnabled = await LogAI.Core.Notify.TelegramState.EnsureAsync(store);

            // AI token 用量（每次调用后由 AiClient 累计，见 AiUsage）。
            var tokens = await LogAI.Core.Ai.AiUsage.ReadAsync(store);

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ai_calls"] = tokens.Calls,
                ["ai_model"] = model,
                ["ai_tokens_completion"] = tokens.Completion,
                ["ai_tokens_prompt"] = tokens.Prompt,
                ["ai_tokens_today"] = tokens.Today,
                ["ai_tokens_total"] = tokens.Total,
                ["logs_last_day"] = lastDay,
                ["logs_last_hour"] = lastHour,
                ["ollama_available"] = available,
                ["ollama_last_check_age"] = age,
                ["redis_connected"] = true,
                ["severities"] = severities,
                ["sources"] = sources,
                ["telegram_enabled"] = telegramEnabled,
                ["total_alerts"] = totalAlerts,
                ["total_filters"] = totalFilters,
                ["total_logs"] = totalLogs,
                ["unacknowledged_alerts"] = unacknowledged,
            };
            return payload;
    }

    private static readonly object PushLock = new();
    private static double _lastPush;

    /// <summary>
    /// Broadcasts fresh stats at most once per interval, mirroring
    /// _push_stats_if_needed (2.0s there, documented as "matches
    /// redis_client.STATS_CACHE_TTL"). Cheap when throttled, so the ingest, alert
    /// and analysis paths can all call it. The push is fire-and-forget: building
    /// the payload touches Redis and must never delay log ingestion.
    /// </summary>
    internal static void PushIfNeeded(RedisStore store)
    {
        lock (PushLock)
        {
            double now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks)
                         / (double)TimeSpan.TicksPerSecond;
            if (now - _lastPush < StatsPushIntervalSeconds) return;
            _lastPush = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var payload = await BuildPayloadAsync(store);
                LogAI.Web.Realtime.EngineIoServer.Current?.Broadcast("stats", payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[StatsApi] stats push failed: " + ex.Message);
            }
        });
    }

    private const double StatsPushIntervalSeconds = 2.0;
}
