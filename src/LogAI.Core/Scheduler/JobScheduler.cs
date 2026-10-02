// Interval scheduler for the background jobs (analysis, cleanup, health check).
//
// Three behaviours are deliberate, all of them learned from the Python version:
//
//   * the interval is re-read every tick, so changing it in Settings takes
//     effect without a restart (the Python job had to reschedule itself);
//   * a tick is a short poll rather than a timer armed for the whole interval,
//     which is what made interval changes apply only after the current wait;
//   * a job that is still running is skipped, never started twice, so a slow
//     model call cannot pile up overlapping analysis runs.

namespace LogAI.Core.Scheduler;

public sealed class JobScheduler(TimeSpan tick)
{
    private sealed class Job
    {
        public required string Name { get; init; }
        public required Func<TimeSpan?> IntervalProvider { get; init; }
        public required Func<CancellationToken, Task> Body { get; init; }
        public TimeSpan? FirstDelay { get; init; }
        public DateTimeOffset? Started { get; set; }
        public DateTimeOffset? Completed { get; set; }
        public bool Running { get; set; }
        public int Runs { get; set; }
        public int Skips { get; set; }
    }

    private readonly List<Job> _jobs = [];
    private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;

    public void Add(string name, Func<TimeSpan?> intervalProvider, Func<CancellationToken, Task> body,
                    TimeSpan? firstDelay = null) =>
        _jobs.Add(new Job { Name = name, IntervalProvider = intervalProvider, Body = body, FirstDelay = firstDelay });

    public int RunCount(string name) => Snapshot().GetValueOrDefault(name).Runs;
    public int SkipCount(string name) => Snapshot().GetValueOrDefault(name).Skips;

    public Dictionary<string, (int Runs, int Skips)> Snapshot() =>
        _jobs.ToDictionary(j => j.Name, j => (j.Runs, j.Skips), StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var due = new List<Job>();

            foreach (var job in _jobs)
            {
                if (job.Running) { job.Skips++; continue; }

                TimeSpan? interval = job.IntervalProvider();
                if (interval is null || interval.Value <= TimeSpan.Zero) continue;

                if (job.Started is null)
                {
                    // Optional warm-up so a restart does not fire every job at once.
                    if (job.FirstDelay is { } delay && now - _epoch < delay) continue;
                }
                else if (now - job.Started.Value < interval.Value)
                {
                    continue;
                }

                job.Running = true;
                job.Started = now;
                job.Runs++;
                due.Add(job);
            }

            foreach (var job in due) _ = ExecuteAsync(job, cancellationToken);

            try
            {
                await Task.Delay(tick, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        try
        {
            await job.Body(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A failing job must not stop the scheduler, but it MUST be visible:
            // swallowing it silently makes "never ran" and "threw" look identical,
            // which cost a full debugging round.
            Console.Error.WriteLine("[scheduler] job '" + job.Name + "' failed: "
                + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
        }
        finally
        {
            job.Completed = DateTimeOffset.UtcNow;
            job.Running = false;
        }
    }
}
