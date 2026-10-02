// Boundary cases for the ok rule.

using LogAI.Core.Scheduler;

namespace LogAI.Web;

internal static class HealthSelfTest
{
    private static int _failures;

    public static int Run()
    {
        // Baseline: everything healthy (interval 2 min -> floor of 600s).
        HealthInputs Healthy() => new(Backlog: 0, TotalLogs: 100, AiAvailable: true,
                                      LastAnalysisAgeSeconds: 30, WarnThreshold: 2000, AnalysisIntervalMinutes: 2);

        Check("healthy input is ok", HealthCheck.IsOk(Healthy()));
        Check("age ceiling is the 600s floor for a 2-minute interval",
            HealthCheck.MaxAnalysisAgeSeconds(2) == 600, HealthCheck.MaxAnalysisAgeSeconds(2).ToString());
        Check("age ceiling scales with the interval",
            HealthCheck.MaxAnalysisAgeSeconds(10) == 1500, HealthCheck.MaxAnalysisAgeSeconds(10).ToString());

        Check("backlog above the threshold fails",
            !HealthCheck.IsOk(Healthy() with { Backlog = 2001 }));
        Check("backlog equal to the threshold passes",
            HealthCheck.IsOk(Healthy() with { Backlog = 2000, TotalLogs = 2000 }));

        Check("stale analyser fails",
            !HealthCheck.IsOk(Healthy() with { LastAnalysisAgeSeconds = 601 }));
        Check("age exactly at the ceiling passes",
            HealthCheck.IsOk(Healthy() with { LastAnalysisAgeSeconds = 600 }));

        // The escape clause: AI down but the analyser has never run.
        Check("ai down with no run yet is still ok",
            HealthCheck.IsOk(Healthy() with { AiAvailable = false, LastAnalysisAgeSeconds = -1 }));
        // The escape clause only covers "never ran": once a run has happened an
        // unavailable AI endpoint is unhealthy immediately, however recent.
        Check("ai down after a run fails immediately",
            !HealthCheck.IsOk(Healthy() with { AiAvailable = false, LastAnalysisAgeSeconds = 30 }));
        Check("ai down with an old run fails",
            !HealthCheck.IsOk(Healthy() with { AiAvailable = false, LastAnalysisAgeSeconds = 4000 }));

        // The production state that reported ok:false: a 34k backlog.
        var production = new HealthInputs(Backlog: 34446, TotalLogs: 2478536, AiAvailable: true,
                                          LastAnalysisAgeSeconds: 38, WarnThreshold: 2000, AnalysisIntervalMinutes: 2);
        Check("the observed 34k backlog reports not ok", !HealthCheck.IsOk(production));

        // After the backlog drained, the same service reports ok.
        Check("the same service reports ok once drained",
            HealthCheck.IsOk(production with { Backlog = 41, TotalLogs = 2599761 }));

        var payload = HealthCheck.Build(production with { Backlog = 41 }, "Llama-3.2-3B-Instruct-4bit",
                                        "2026-10-02T01:00:00.000000+00:00");
        Check("payload carries the seven fields",
            payload is { aiAvailable: true, ok: true, backlog: 41, totalLogs: 2478536 }
            && payload.aiModel.StartsWith("Llama", StringComparison.Ordinal)
            && payload.ts.EndsWith("+00:00", StringComparison.Ordinal)
            && payload.lastAnalysisAge == 38,
            $"{payload.backlog}/{payload.totalLogs}/{payload.lastAnalysisAge}");

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
