// Verifies the four-key client layout, the sliding rate window and deletion.

using System.Globalization;
using LogAI.Core.Store;
using LogAI.Core.Syslog;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class ClientTrackerSelfTest
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

        var tracker = new ClientTracker(store);
        for (int i = 0; i < 3; i++) await tracker.TrackAsync("10.1.1.1", "UDP", "host-one");
        await tracker.TrackAsync("10.1.1.1", "UDP", "host-one", error: true, errorMessage: "WRONGTYPE on ZADD");
        await tracker.TrackAsync("10.1.1.2", "TCP", "host-two");

        // Types come first: a SET/ZSET mix-up is invisible to value assertions.
        Check("client hash is a hash", await store.Db.KeyTypeAsync("syslog:client:10.1.1.1") == RedisType.Hash,
            (await store.Db.KeyTypeAsync("syslog:client:10.1.1.1")).ToString());
        Check("protocols key is a set", await store.Db.KeyTypeAsync("syslog:client:10.1.1.1:protocols") == RedisType.Set,
            (await store.Db.KeyTypeAsync("syslog:client:10.1.1.1:protocols")).ToString());
        Check("recent key is a sorted set", await store.Db.KeyTypeAsync("syslog:client:10.1.1.1:recent") == RedisType.SortedSet,
            (await store.Db.KeyTypeAsync("syslog:client:10.1.1.1:recent")).ToString());
        Check("index is a sorted set", await store.Db.KeyTypeAsync(Keys.ClientsIndex) == RedisType.SortedSet,
            (await store.Db.KeyTypeAsync(Keys.ClientsIndex)).ToString());

        var fields = (await store.Db.HashGetAllAsync("syslog:client:10.1.1.1"))
            .ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
        Check("message_count counts successes only", fields.GetValueOrDefault("message_count") == "3",
            fields.GetValueOrDefault("message_count") ?? "");

        // Invariant that a millisecond-precision member name used to break: every
        // success lands in the window exactly once, so the rate equals the count
        // while everything is inside the 60 second window.
        Check("burst: every message is a distinct window entry",
            await tracker.MessagesPerMinuteAsync("10.1.1.1") == 3,
            (await tracker.MessagesPerMinuteAsync("10.1.1.1")).ToString());
        Check("an error does not count towards the rate",
            await tracker.MessagesPerMinuteAsync("10.1.1.1") == 3,
            (await tracker.MessagesPerMinuteAsync("10.1.1.1")).ToString());
        Check("error_count incremented", fields.GetValueOrDefault("error_count") == "1",
            fields.GetValueOrDefault("error_count") ?? "");
        Check("last_error recorded", fields.GetValueOrDefault("last_error") == "WRONGTYPE on ZADD",
            fields.GetValueOrDefault("last_error") ?? "");
        // Format, not mere presence: these are epoch floats in production and the
        // diagnostics endpoint parses them with float(v). An ISO string here made
        // every client look stale to readers of the index.
        Check("first_seen is an epoch number",
            double.TryParse(fields.GetValueOrDefault("first_seen"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double first)
            && first > 1_700_000_000 && first < 2_000_000_000,
            fields.GetValueOrDefault("first_seen") ?? "");
        Check("last_seen is an epoch number",
            double.TryParse(fields.GetValueOrDefault("last_seen"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double last)
            && last > 1_700_000_000 && last < 2_000_000_000,
            fields.GetValueOrDefault("last_seen") ?? "");
        Check("hostname recorded", fields.GetValueOrDefault("hostname") == "host-one",
            fields.GetValueOrDefault("hostname") ?? "");

        Check("protocol recorded",
            (await store.Db.SetMembersAsync("syslog:client:10.1.1.1:protocols")).Any(v => v == "UDP"));
        Check("second client uses its own protocol",
            (await store.Db.SetMembersAsync("syslog:client:10.1.1.2:protocols")).Any(v => v == "TCP"));
        Check("index scores are epoch seconds",
            await store.Db.SortedSetScoreAsync(Keys.ClientsIndex, "10.1.1.1") is { } score
                && score > 1_700_000_000 && score < 2_000_000_000,
            (await store.Db.SortedSetScoreAsync(Keys.ClientsIndex, "10.1.1.1"))?.ToString() ?? "");
        Check("index is ordered by recency", await store.Db.SortedSetLengthAsync(Keys.ClientsIndex) == 2,
            (await store.Db.SortedSetLengthAsync(Keys.ClientsIndex)).ToString());

        // Deletion must clear all four places, and report whether a record existed.
        Check("delete reports an existing record", await tracker.DeleteAsync("10.1.1.1"));
        Check("all four keys removed",
            await store.Db.KeyExistsAsync("syslog:client:10.1.1.1") == false
            && await store.Db.KeyExistsAsync("syslog:client:10.1.1.1:protocols") == false
            && await store.Db.KeyExistsAsync("syslog:client:10.1.1.1:recent") == false
            && await store.Db.SortedSetScoreAsync(Keys.ClientsIndex, "10.1.1.1") is null);
        Check("deleting again reports nothing to remove", !await tracker.DeleteAsync("10.1.1.1"));
        Check("the other client survives", await store.Db.KeyExistsAsync("syslog:client:10.1.1.2"));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
