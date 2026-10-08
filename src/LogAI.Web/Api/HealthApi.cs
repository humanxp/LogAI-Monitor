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

            var inputs = new HealthInputs(backlog, total, HealthState.AiAvailable,
                HealthState.LastAnalysisAgeSeconds, HealthState.WarnThreshold, HealthState.AnalysisMinutes);

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ai_available"] = HealthState.AiAvailable,
                // 模型名当场从设置里解析，不用巡检快照。快照只在 health_watch_minutes
                // （默认 5 分钟）那一拍更新，而模型现在每轮分析都会重读——用它就会
                // 出现"设置页已经改了、真正在跑的也是新模型，偏偏 /api/health 还报旧值
                // 五分钟"，正是把上一次排查带偏的那个现象。/api/stats 本来就是这个口径。
                // 解析不出来时退回巡检快照。backlog/total 本来就是当场读的，与它们一致。
                ["ai_model"] = AiClient.ResolveModel(store, HealthState.AiModel),
                ["backlog"] = backlog,
                ["last_analysis_age_s"] = HealthState.LastAnalysisAgeSeconds,
                ["ok"] = HealthCheck.IsOk(inputs),
                ["total_logs"] = total,
                ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
                                                         CultureInfo.InvariantCulture),
            };
            return ReadApi.JsonBody(payload);
        });
    }
}
