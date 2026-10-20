// GET /api/health — public (answers 200 with no
// session). Monitoring should not need credentials.
//
//   {"ai_available":…,"ai_model":…,"backlog":…,"last_analysis_age_s":…,
//    "ok":…,"total_logs":…,"ts":…}
//
// ts carries SIX fractional digits and a "+00:00" offset, matching
// datetime.now(timezone.utc).isoformat(). The age is the time since the last
// analysis RUN: the scheduler publishes it, so an idle queue is not read as a
// stalled analyser.

using System.Globalization;
using LogAI.Core.Ai;
using LogAI.Core.Scheduler;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

/// <summary>
/// Snapshot published by the scheduler's health job. AiModel is only the fallback
/// now: the endpoint resolves the model live, because the model can change every
/// analysis round while this snapshot is republished every health_watch_minutes.
/// </summary>
internal static class HealthState
{
    public static bool AiAvailable { get; set; }
    public static string AiModel { get; set; } = "";
    public static int LastAnalysisAgeSeconds { get; set; } = -1;
    public static int WarnThreshold { get; set; } = 2000;
    public static int AnalysisMinutes { get; set; } = 2;
}

internal static class HealthApi
{
    public static void Map(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/health", async () =>
        {
            long backlog = await store.Db.SortedSetLengthAsync(Keys.Unanalyzed);
            long total = await store.Db.SortedSetLengthAsync(Keys.Timeline);

            // 待分析队列「最老一条」的入队时间：用来判断积压是刚堆起来的还是压了很久，
            // 也是 ETA 预测里"这批日志已经等了多久"的依据。
            var head = await store.Db.SortedSetRangeByRankWithScoresAsync(Keys.Unanalyzed, 0, 0);
            double queueSince = head.Length > 0 ? head[0].Score : 0;

            // 每轮消化条数与运行间隔：前端用它算"处理能力"，不用再拉设置页接口。
            var settings = await store.GetSettingsAsync();
            int batch = 500;
            if (int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("max_logs_per_analysis")), out int b) && b > 0)
                batch = b;
            else if (int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("batch_size")), out int b2) && b2 > 0)
                batch = b2;
            int interval = HealthState.AnalysisMinutes > 0 ? HealthState.AnalysisMinutes : 2;
            if (int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("analysis_interval")), out int iv) && iv > 0)
                interval = iv;

            // 上轮分析距现在多久：当场从 ai_history 时间线读，不用巡检快照。
            // 快照只在 health_watch_minutes 那一拍更新，重启后到下一拍之间一直是 -1，
            // 页面上就会一直显示"尚无记录"，而分析其实刚跑过。
            double? liveAge = await store.LastAnalysisAgeSecondsAsync();
            int age = liveAge.HasValue ? (int)liveAge.Value : HealthState.LastAnalysisAgeSeconds;

            // AI 可达性：巡检快照默认每 health_watch_minutes（5 分钟）才更新一拍，
            // 容器刚重启时快照还是 false，页面上就会红着报"后端不可达"，而模型其实好好的。
            // 所以只在快照说"不可达"时当场复核一次。
            bool aiAvailable = HealthState.AiAvailable;
            if (!aiAvailable)
            {
                try
                {
                    string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
                    if (provider.Length == 0) provider = "openai";
                    string host = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
                    if (host.Length == 0) host = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
                    aiAvailable = await new AiClient
                    {
                        Provider = provider,
                        BaseUrl = AiClient.NormalizeBaseUrl(host, provider),
                        Model = AiClient.ResolveModel(store, HealthState.AiModel),
                        ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
                    }.IsAvailableAsync();
                }
                catch
                {
                    // 复核失败就沿用快照，不因为探测本身出错而改判。
                }
            }

            var inputs = new HealthInputs(backlog, total, aiAvailable,
                age, HealthState.WarnThreshold, HealthState.AnalysisMinutes);

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ai_available"] = aiAvailable,
                // 模型名当场从设置里解析，不用巡检快照。快照只在 health_watch_minutes
                // （默认 5 分钟）那一拍更新，而模型现在每轮分析都会重读——用它就会
                // 出现"设置页已经改了、真正在跑的也是新模型，偏偏 /api/health 还报旧值
                // 五分钟"，正是把上一次排查带偏的那个现象。/api/stats 本来就是这个口径。
                // 解析不出来时退回巡检快照。backlog/total 本来就是当场读的，与它们一致。
                ["ai_model"] = AiClient.ResolveModel(store, HealthState.AiModel),
                ["backlog"] = backlog,
                ["backlog_threshold"] = HealthState.WarnThreshold,
                ["queue_since"] = queueSince,
                ["analysis_batch"] = batch,
                ["analysis_interval_min"] = interval,
                ["last_analysis_age_s"] = age,
                ["ok"] = HealthCheck.IsOk(inputs),
                ["total_logs"] = total,
                ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
                                                         CultureInfo.InvariantCulture),
            };
            return ReadApi.JsonBody(payload);
        });
    }
}
