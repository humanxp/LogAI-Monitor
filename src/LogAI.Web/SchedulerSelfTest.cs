// Verifies the scheduler's interval, hot-reload and no-overlap behaviours.

using LogAI.Core.Scheduler;

namespace LogAI.Web;

internal static class SchedulerSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        var scheduler = new JobScheduler(TimeSpan.FromMilliseconds(50));
        var interval = TimeSpan.FromMilliseconds(200);
        int concurrent = 0, maxConcurrent = 0;
        bool enabled = true;

        scheduler.Add("slow",
            () => enabled ? interval : null,
            async token =>
            {
                int now = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, now);
                await Task.Delay(120, token);           // longer than the tick
                Interlocked.Decrement(ref concurrent);
            });

        int quickRuns = 0;
        scheduler.Add("quick", () => Timespan(80), _ => { Interlocked.Increment(ref quickRuns); return Task.CompletedTask; });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        await scheduler.RunAsync(cancellation.Token);

        var snapshot = scheduler.Snapshot();
        Console.WriteLine($"  slow: runs={snapshot["slow"].Runs} skips={snapshot["slow"].Skips}  quick: runs={snapshot["quick"].Runs}");

        Check("job runs repeatedly at its interval", snapshot["slow"].Runs >= 3, snapshot["slow"].Runs.ToString());
        Check("overlapping runs are skipped", snapshot["slow"].Skips >= 1, snapshot["slow"].Skips.ToString());
        Check("no overlapping execution", maxConcurrent == 1, maxConcurrent.ToString());
        Check("a faster interval runs more often", snapshot["quick"].Runs > snapshot["slow"].Runs,
            $"{snapshot["quick"].Runs} vs {snapshot["slow"].Runs}");

        // Disabling by interval provider stops the job without a restart.
        int before = snapshot["slow"].Runs;
        enabled = false;
        using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await scheduler.RunAsync(second.Token);
        Check("interval provider of null disables the job", scheduler.RunCount("slow") == before,
            $"{before} -> {scheduler.RunCount("slow")}");

        // A job that throws must not stop the scheduler.
        var resilient = new JobScheduler(TimeSpan.FromMilliseconds(50));
        int attempts = 0;
        resilient.Add("boom", () => Timespan(80), _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("job failure");
        });
        using var third = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await resilient.RunAsync(third.Token);
        Check("a throwing job keeps being scheduled", attempts >= 2, attempts.ToString());

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static TimeSpan Timespan(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
