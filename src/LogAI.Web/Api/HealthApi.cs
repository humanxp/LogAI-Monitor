// GET /api/health — public, like the Python endpoint (which answers 200 with no
// session). Monitoring should not need credentials.
//
//   {"ai_available":…,"ai_model":…,"backlog":…,"last_analysis_age_s":…,
//    "ok":…,"total_logs":…,"ts":…}
//
// ts carries SIX fractional digits and a "+00:00" offset, matching Python's
// datetime.now(timezone.utc).isoformat(). The age is the time since the last
// analysis RUN: the scheduler publishes it, so an idle queue is not read as a
// stalled analyser.

using System.Globalization;
using LogAI.Core.Scheduler;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

/// <summary>Snapshot published by the scheduler's health job.</summary>
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
                ["ai_model"] = HealthState.AiModel,
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
