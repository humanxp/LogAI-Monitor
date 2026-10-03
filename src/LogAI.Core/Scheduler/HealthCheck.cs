// Health evaluation for /api/health and the watchdog.
//
// The ok rule ANDs three conditions:
//
//   ok = backlog <= warn_threshold
//        and (ai_available or last_analysis_age_s < 0)
//        and last_analysis_age_s <= max(analysis_interval * 150, 600)
//
// The middle clause is narrow: an unavailable AI endpoint is tolerated ONLY
// before the first run (age -1). Once any run has happened, ai_available=false
// makes the service unhealthy immediately, however recent that run was.
//
// last_analysis_age_s is in-process state (the moment the
// analyser last completed a run, which is updated even when a run analyses
// nothing). Reproducing that faithfully means the scheduler owns the value; the
// evaluator below therefore takes it as input rather than deriving it from
// ai_history, which would silently differ whenever the analyser is idle.

namespace LogAI.Core.Scheduler;

public sealed record HealthInputs(
    long Backlog,
    long TotalLogs,
    bool AiAvailable,
    int LastAnalysisAgeSeconds,      // -1 when no run has happened yet
    int WarnThreshold,
    int AnalysisIntervalMinutes);

public static class HealthCheck
{
    /// <summary>Age above which a silent analyser counts as a failure.</summary>
    public static int MaxAnalysisAgeSeconds(int analysisIntervalMinutes) =>
        Math.Max(analysisIntervalMinutes * 150, 600);

    public static bool IsOk(HealthInputs inputs) =>
        inputs.Backlog <= inputs.WarnThreshold
        && (inputs.AiAvailable || inputs.LastAnalysisAgeSeconds < 0)
        && inputs.LastAnalysisAgeSeconds <= MaxAnalysisAgeSeconds(inputs.AnalysisIntervalMinutes);

    /// <summary>Fields of the /api/health payload, in Flask's alphabetical order.</summary>
    public static (bool aiAvailable, string aiModel, long backlog, int lastAnalysisAge,
                   bool ok, long totalLogs, string ts) Build(HealthInputs inputs, string aiModel, string timestamp) =>
        (inputs.AiAvailable, aiModel, inputs.Backlog, inputs.LastAnalysisAgeSeconds,
         IsOk(inputs), inputs.TotalLogs, timestamp);
}
