// Verifies batch selection semantics, including the two skip rules.

using LogAI.Core.Ai;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class BatchSelfTest
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

        // Five pending logs, oldest first by score.
        for (int i = 1; i <= 5; i++)
        {
            string id = $"log:{i:D3}";
            await store.Db.HashSetAsync(id, [new HashEntry("message", $"m{i}"), new HashEntry("analyzed", "false")]);
            await store.Db.SortedSetAddAsync(Keys.Unanalyzed, id, i);
        }

        // Already analysed, but still present in the index.
        await store.Db.HashSetAsync("log:900", [new HashEntry("message", "done"), new HashEntry("analyzed", "true")]);
        await store.Db.SortedSetAddAsync(Keys.Unanalyzed, "log:900", 6);

        // Index entry whose hash expired: no hash written at all.
        await store.Db.SortedSetAddAsync(Keys.Unanalyzed, "log:expired", 7);

        var batch = await UnanalyzedBatch.FetchAsync(store, 10);
        Check("expired index entries are skipped", batch.All(i => i.Id != "log:expired"),
            string.Join(",", batch.Select(i => i.Id)));
        Check("already-analysed entries are skipped", batch.All(i => i.Id != "log:900"),
            string.Join(",", batch.Select(i => i.Id)));
        Check("the five pending logs are returned", batch.Count == 5, batch.Count.ToString());
        Check("oldest first", batch.Select(i => i.Id).SequenceEqual(["log:001", "log:002", "log:003", "log:004", "log:005"]),
            string.Join(",", batch.Select(i => i.Id)));
        Check("fields travel with the id", batch[0].Fields.GetValueOrDefault("message") == "m1",
            batch[0].Fields.GetValueOrDefault("message") ?? "");

        // The batch is not topped up past skipped entries.
        var limited = await UnanalyzedBatch.FetchAsync(store, 3);
        Check("limit bounds the batch", limited.Count == 3, limited.Count.ToString());
        Check("limit keeps the oldest ones",
            limited.Select(i => i.Id).SequenceEqual(["log:001", "log:002", "log:003"]),
            string.Join(",", limited.Select(i => i.Id)));

        // A window that consists only of skipped entries yields an empty batch.
        var skewed = await UnanalyzedBatch.FetchAsync(store, 2);
        Check("window of skipped entries still returns the pending ones",
            skewed.Count == 2 && skewed[0].Id == "log:001", string.Join(",", skewed.Select(i => i.Id)));

        Check("zero limit yields an empty batch", (await UnanalyzedBatch.FetchAsync(store, 0)).Count == 0);
        Check("empty index yields an empty batch", (await UnanalyzedBatch.FetchAsync(new RedisStore(
            new RedisOptions { Host = options.Host, Port = options.Port, Database = 8 }), 10)).Count == 0);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
