// Verifies the post-analysis data shape, which /api/logs serves as-is.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class CommitSelfTest
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

        string[] ids = ["log:001", "log:002", "log:003"];
        foreach (string id in ids)
        {
            await store.Db.HashSetAsync(id, [new HashEntry("message", "m"), new HashEntry("analyzed", "false")]);
            await store.Db.SortedSetAddAsync(Keys.Unanalyzed, id, 1);
            await store.Db.SortedSetAddAsync(Keys.Timeline, id, 1);
        }

        var analysis = JsonNode.Parse("""{"overall_status":"critical","critical_count":2,"issues_found":["[XiaoBao] BTRFS csum failed"]}""")!;
        int updated = await AnalysisCommit.ApplyAsync(store, ids, analysis);

        Check("apply reports every log updated", updated == 3, updated.ToString());
        Check("batch left logs:unanalyzed", await store.Db.SortedSetLengthAsync(Keys.Unanalyzed) == 0,
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)).ToString());
        Check("timeline untouched", await store.Db.SortedSetLengthAsync(Keys.Timeline) == 3,
            (await store.Db.SortedSetLengthAsync(Keys.Timeline)).ToString());

        foreach (string id in ids)
        {
            var hash = await store.Db.HashGetAllAsync(id);
            var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
            Check($"{id} marked analyzed", fields.GetValueOrDefault("analyzed") == "true",
                fields.GetValueOrDefault("analyzed") ?? "");
            var stored = JsonNode.Parse(fields.GetValueOrDefault("analysis") ?? "null");
            Check($"{id} carries the analysis copy",
                stored?["overall_status"]?.GetValue<string>() == "critical"
                && stored?["issues_found"] is JsonArray { Count: 1 },
                fields.GetValueOrDefault("analysis") ?? "");
        }

        // Retirement path: leaves the queue, no analysis written.
        await store.Db.SortedSetAddAsync(Keys.Unanalyzed, "log:poison", 9);
        await store.Db.HashSetAsync("log:poison", [new HashEntry("analyzed", "false")]);
        await AnalysisCommit.RetireAsync(store, ["log:poison"]);
        Check("retired batch leaves the queue", await store.Db.SortedSetLengthAsync(Keys.Unanalyzed) == 0,
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)).ToString());
        Check("retired log is not marked analysed",
            (await store.Db.HashGetAsync("log:poison", "analyzed")) == "false");

        // Empty input is a no-op, not an error.
        Check("empty batch is a no-op", await AnalysisCommit.ApplyAsync(store, [], analysis) == 0);
        await AnalysisCommit.RetireAsync(store, []);
        Check("empty retirement is a no-op", true);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
