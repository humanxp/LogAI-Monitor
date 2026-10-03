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

    public static void Map(WebApplication app, RedisStore store)
    {
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
            string model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "";
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

            long lastDay = await db.SortedSetLengthAsync(Keys.Timeline, now - 86400, now);
            long lastHour = await db.SortedSetLengthAsync(Keys.Timeline, now - 3600, now);
            long totalLogs = await db.SortedSetLengthAsync(Keys.Timeline);
            long totalAlerts = await db.SortedSetLengthAsync(Keys.AlertsTimeline);
            long totalFilters = await db.SetLengthAsync(Keys.Filters);

            var severities = (await db.SetMembersAsync(Keys.SeveritiesIndex))
                .Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();
            var sources = (await db.SetMembersAsync(Keys.SourcesIndex))
                .Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();

            // One round trip instead of one per alert: the previous shape was N+1 and
            // grew with the alert count, on a payload both main pages request.
            long unacknowledged = 0;
            var alertIds = await db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
            if (alertIds.Length > 0)
            {
                var batch = db.CreateBatch();
                var reads = new Task<StackExchange.Redis.RedisValue>[alertIds.Length];
                for (int i = 0; i < alertIds.Length; i++)
                    reads[i] = batch.HashGetAsync(alertIds[i].ToString(), "acknowledged");
                batch.Execute();
                foreach (var value in await Task.WhenAll(reads))
                    if (value.ToString() != "true") unacknowledged++;
            }

            bool telegramEnabled = await LogAI.Core.Notify.TelegramState.EnsureAsync(store);

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ai_model"] = model,
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
