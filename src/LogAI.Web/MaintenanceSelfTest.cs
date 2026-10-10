// Verifies bulk deletion on an isolated database. Never run against database 0:
// clearing is destructive by definition.

using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class MaintenanceSelfTest
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

        // ---- delete by source ----
        await store.Db.ExecuteAsync("FLUSHDB");
        async Task Seed(string id, string source, string host, string severity, double score)
        {
            await store.Db.HashSetAsync(id,
            [
                new HashEntry("source", source), new HashEntry("hostname", host),
                new HashEntry("severity", severity), new HashEntry("analyzed", "false"),
            ]);
            await store.Db.SortedSetAddAsync(Keys.Timeline, id, score);
            await store.Db.SortedSetAddAsync(Keys.Unanalyzed, id, score);
            await store.Db.SortedSetAddAsync("logs:source:" + source, id, score);
            await store.Db.SortedSetAddAsync("logs:host:" + host, id, score);
            await store.Db.SortedSetAddAsync("logs:severity:" + severity, id, score);
        }

        await Seed("log:1", "10.0.0.1", "box-a", "error", 1);
        await Seed("log:2", "10.0.0.1", "box-a", "warning", 2);
        await Seed("log:3", "10.0.0.2", "box-b", "error", 3);
        await store.Db.SetAddAsync(Keys.SourcesIndex, ["10.0.0.1", "10.0.0.2"]);
        await store.Db.SortedSetAddAsync(Keys.ClientsIndex, "10.0.0.1", 1);

        // The per-client hash, not just the index entry: DeleteClientAsync reports
        // whether a RECORD existed (the delete_client semantics), so a
        // fixture that only touches the index makes that assertion fail.
        await store.Db.HashSetAsync("syslog:client:10.0.0.1",
            [new HashEntry("hostname", "box-a"), new HashEntry("message_count", 1)]);
        long removed = await LogMaintenance.DeleteBySourceAsync(store, "10.0.0.1");
        Check("deleted both logs of the source", removed == 2, removed.ToString());
        Check("hashes gone", !await store.Db.KeyExistsAsync("log:1") && !await store.Db.KeyExistsAsync("log:2"));
        Check("other source untouched", await store.Db.KeyExistsAsync("log:3"));
        Check("timeline keeps only the other source",
            (await store.Db.SortedSetLengthAsync(Keys.Timeline)) == 1,
            (await store.Db.SortedSetLengthAsync(Keys.Timeline)).ToString());
        Check("unanalyzed queue cleaned",
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)) == 1,
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)).ToString());
        Check("source index no longer lists it",
            !(await store.Db.SetMembersAsync(Keys.SourcesIndex)).Any(v => v == "10.0.0.1"),
            string.Join(",", (await store.Db.SetMembersAsync(Keys.SourcesIndex)).Select(v => v.ToString())));
        Check("per-source zset removed", !await store.Db.KeyExistsAsync("logs:source:" + "10.0.0.1"));
        Check("host index still holds the surviving log",
            (await store.Db.SortedSetLengthAsync("logs:host:" + "box-a")) == 0,
            (await store.Db.SortedSetLengthAsync("logs:host:" + "box-a")).ToString());
        // Type assertion, not just a value assertion: production stores this index
        // as a ZSET scored by last-seen. Seeding and checking it as a SET made a
        // wrong-type implementation pass, which is how the SET/ZSET bug survived.
        Check("clients index is a sorted set, not a set",
            await store.Db.KeyTypeAsync(Keys.ClientsIndex) == RedisType.SortedSet,
            (await store.Db.KeyTypeAsync(Keys.ClientsIndex)).ToString());

        Check("client record removabe",
            await LogMaintenance.DeleteClientAsync(store, "10.0.0.1")
            && !(await store.Db.SortedSetRangeByRankAsync(Keys.ClientsIndex)).Any());

        // ---- clear all ----
        await store.Db.ExecuteAsync("FLUSHDB");
        await Seed("log:1", "10.0.0.1", "box-a", "error", 1);
        await Seed("log:2", "10.0.0.2", "box-b", "warning", 2);
        await store.Db.SetAddAsync(Keys.SourcesIndex, ["10.0.0.1", "10.0.0.2"]);
        await store.Db.SetAddAsync(Keys.HostsIndex, ["box-a", "box-b"]);
        await store.Db.SetAddAsync(Keys.SeveritiesIndex, ["error", "warning"]);
        await store.Db.SortedSetAddAsync(Keys.ClientsIndex, "10.0.0.1", 1);

        long cleared = await LogMaintenance.ClearAllAsync(store);
        Check("clear reports the log count", cleared == 2, cleared.ToString());
        Check("timeline empty", (await store.Db.SortedSetLengthAsync(Keys.Timeline)) == 0);
        // 这一条正是漏掉过的 bug：旧版只删 ZSET、不删 log:<id> 哈希，线上攒了 13 万孤儿。
        Check("log hashes deleted",
            !await store.Db.KeyExistsAsync("log:1") && !await store.Db.KeyExistsAsync("log:2"));
        Check("unanalyzed empty", (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)) == 0);
        Check("index sets empty",
            (await store.Db.SetLengthAsync(Keys.SourcesIndex)) == 0
            && (await store.Db.SetLengthAsync(Keys.HostsIndex)) == 0
            && (await store.Db.SetLengthAsync(Keys.SeveritiesIndex)) == 0,
            $"{await store.Db.SetLengthAsync(Keys.SourcesIndex)}/{await store.Db.SetLengthAsync(Keys.HostsIndex)}/{await store.Db.SetLengthAsync(Keys.SeveritiesIndex)}");
        Check("client records cleared", (await store.Db.SortedSetLengthAsync(Keys.ClientsIndex)) == 0);
        Check("per-source zsets removed", !await store.Db.KeyExistsAsync("logs:source:" + "10.0.0.1"));

        // ---- purge orphan log hashes ----
        // 孤儿 = 只有 log:<id> 哈希、不在 logs:timeline 里（旧版清空留下的残渣）。
        await store.Db.ExecuteAsync("FLUSHDB");
        await Seed("log:1", "10.0.0.1", "box-a", "error", 1);                 // 活日志：进时间线
        await store.Db.HashSetAsync("log:2", [new HashEntry("source", "10.0.0.2")]);   // 孤儿
        long purged = await LogMaintenance.PurgeOrphanLogHashesAsync(store);
        Check("purged exactly the orphan", purged == 1, purged.ToString());
        Check("live log hash kept", await store.Db.KeyExistsAsync("log:1"));
        Check("orphan hash gone", !await store.Db.KeyExistsAsync("log:2"));
        Check("timeline untouched", (await store.Db.SortedSetLengthAsync(Keys.Timeline)) == 1);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
