// Polls the real Docker daemon twice and checks that the second pass stores
// nothing new (the dedupe marker) and that the stored source names follow the
// docker:<container> convention.

using LogAI.Core.Docker;
using LogAI.Core.Store;
using LogAI.Core.Syslog;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class DockerPollSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0) { Console.Error.WriteLine("refusing database 0"); return 2; }

        using var store = new RedisStore(new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        });
        await store.Db.ExecuteAsync("FLUSHDB");

        using var api = new DockerApi();
        // The production application container is a stable, chatty log source.
        var all = await api.ListContainersAsync();
        var target = all.FirstOrDefault(c => c.Name == "logaimonitor") ?? all.FirstOrDefault();
        if (target is null) { Console.Error.WriteLine("no container to read logs from"); return 2; }
        Console.WriteLine($"reading logs from {target.Name} ({target.Id})");

        // Exclude everything except the target so only it is ingested.
        var others = all.Where(c => c.Id != target.Id).Select(c => c.Name).ToList();
        var collector = new DockerCollector(api, new LogWriter(store, 720), () => others)
        {
            TailLines = 20,
        };

        int first = await collector.PollOnceAsync();
        long afterFirst = await store.Db.SortedSetLengthAsync(Keys.Timeline);
        int second = await collector.PollOnceAsync();
        long afterSecond = await store.Db.SortedSetLengthAsync(Keys.Timeline);
        Console.WriteLine($"first poll stored {first}, second stored {second} (timeline {afterFirst} -> {afterSecond})");

        Check("first poll ingested lines", first > 0, first.ToString());
        Check("timeline holds exactly those lines", afterFirst == first, $"{afterFirst} vs {first}");
        Check("second poll ingested nothing (dedupe marker)", second == 0, second.ToString());
        Check("timeline unchanged by the second poll", afterSecond == afterFirst, $"{afterSecond} vs {afterFirst}");
        Check("excluded containers were not ingested",
            (await store.Db.SetMembersAsync(Keys.SourcesIndex)).All(s => s.ToString().StartsWith("docker:", StringComparison.Ordinal)),
            string.Join(",", (await store.Db.SetMembersAsync(Keys.SourcesIndex)).Select(s => s.ToString())));

        string expectedSource = $"docker:{target.Name}";
        var sources = await store.Db.SetMembersAsync(Keys.SourcesIndex);
        Check("source follows the docker:<container> convention",
            sources.Any(s => s == expectedSource), string.Join(",", sources.Select(s => s.ToString())));

        var newest = await store.Db.SortedSetRangeByRankAsync(Keys.Timeline, -1, -1);
        var fields = (await store.Db.HashGetAllAsync(newest[0].ToString()))
            .ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
        Check("stored entry carries source_type=docker",
            fields.GetValueOrDefault("source_type") == "docker", fields.GetValueOrDefault("source_type") ?? "");
        Check("stored entry is queued for analysis",
            await store.Db.SortedSetScoreAsync(Keys.Unanalyzed, newest[0]) is not null);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
