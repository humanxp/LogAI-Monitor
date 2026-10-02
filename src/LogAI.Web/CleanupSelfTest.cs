// Verifies retention removal and dead-id purging on an isolated database.

using LogAI.Core.Scheduler;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class CleanupSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0) { Console.Error.WriteLine("refusing database 0"); return 2; }

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        };
        using var store = new RedisStore(options);
        await store.Db.ExecuteAsync("FLUSHDB");

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double ancient = now - 100 * 3600;      // well past a 24h retention
        double recent = now - 60;

        async Task Seed(string id, double score, bool withHash)
        {
            if (withHash)
                await store.Db.HashSetAsync(id,
                [
                    new HashEntry("source", "10.0.0.1"), new HashEntry("hostname", "box"),
                    new HashEntry("severity", "error"), new HashEntry("analyzed", "false"),
                ]);
            await store.Db.SortedSetAddAsync(Keys.Timeline, id, score);
            await store.Db.SortedSetAddAsync(Keys.Unanalyzed, id, score);
            if (!withHash) return;
            await store.Db.SortedSetAddAsync(Keys.LogSource("10.0.0.1"), id, score);
            await store.Db.SortedSetAddAsync(Keys.LogHost("box"), id, score);
            await store.Db.SortedSetAddAsync(Keys.LogSeverity("error"), id, score);
        }

        await Seed("log:old1", ancient, withHash: true);
        await Seed("log:old2", ancient, withHash: true);
        await Seed("log:new1", recent, withHash: true);
        // Inside the retention window but its hash is gone: exercises the dead-id
        // path only, not the retention path (an expired score would be removed by
        // retention first and the dead-id counter would stay zero).
        await Seed("log:dead", recent, withHash: false);

        var result = await CleanupJob.RunAsync(store, retentionHours: 24);
        Console.WriteLine($"  removed={result.Removed} deadPurged={result.DeadPurged}");

        Check("both expired logs removed", result.Removed == 2, result.Removed.ToString());
        Check("dead index entry purged", result.DeadPurged == 1, result.DeadPurged.ToString());
        Check("expired hashes deleted", !await store.Db.KeyExistsAsync("log:old1") && !await store.Db.KeyExistsAsync("log:old2"));
        Check("recent log untouched",
            await store.Db.KeyExistsAsync("log:new1") && await store.Db.SortedSetScoreAsync(Keys.Timeline, "log:new1") is not null);
        Check("timeline keeps only the recent log",
            (await store.Db.SortedSetRangeByRankAsync(Keys.Timeline, 0, -1)).Select(v => v.ToString()).SequenceEqual(["log:new1"]),
            string.Join(",", (await store.Db.SortedSetRangeByRankAsync(Keys.Timeline, 0, -1)).Select(v => v.ToString())));
        Check("unanalyzed queue drained of both kinds",
            await store.Db.SortedSetLengthAsync(Keys.Unanalyzed) == 1,
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)).ToString());
        Check("indexes cleaned",
            await store.Db.SortedSetLengthAsync(Keys.LogSource("10.0.0.1")) == 1
            && await store.Db.SortedSetLengthAsync(Keys.LogHost("box")) == 1
            && await store.Db.SortedSetLengthAsync(Keys.LogSeverity("error")) == 1,
            $"{await store.Db.SortedSetLengthAsync(Keys.LogSource("10.0.0.1"))}/{await store.Db.SortedSetLengthAsync(Keys.LogHost("box"))}/{await store.Db.SortedSetLengthAsync(Keys.LogSeverity("error"))}");
        Check("cleanup bookkeeping recorded",
            (await store.Db.StringGetAsync(Keys.CleanupLastRemoved)) == "2"
            && (await store.Db.StringGetAsync(Keys.CleanupLastRun)).HasValue,
            (await store.Db.StringGetAsync(Keys.CleanupLastRemoved)).ToString());

        // Cutoff maths: a longer retention must keep more.
        Check("cutoff moves with retention",
            CleanupJob.Cutoff(1000, 1) == 1000 - 3600 && CleanupJob.Cutoff(1000, 24) == 1000 - 86400);

        // Second run over a clean database changes nothing.
        var again = await CleanupJob.RunAsync(store, retentionHours: 24);
        Check("second run is a no-op", again is { Removed: 0, DeadPurged: 0 }, $"{again.Removed}/{again.DeadPurged}");

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
