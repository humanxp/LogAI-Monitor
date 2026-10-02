// Locks the filter id/key convention.
//
// This exists because the bug it guards against was invisible: FilterLoader
// prefixed the id a second time (filters:all members are ALREADY full keys), so
// every rule failed to load, no filter ever matched and no alert was ever
// written - while ingestion, storage and every page kept working normally.
//
// Unit tests on alert writing passed, endpoint tests on /api/filters passed, and
// neither covered "read the rule out of its hash". Hence this test.

using LogAI.Core.Filters;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class FilterLoadSelfTest
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

        // Production convention, verified against the live instance:
        //   filters:all member = "filter:<ms>"  and the hash lives under that key.
        const string fullKey = "filter:1790028200664";
        await store.Db.HashSetAsync(fullKey,
        [
            new HashEntry("id", fullKey),
            new HashEntry("name", "磁盘异常"),
            new HashEntry("enabled", "true"),
            new HashEntry("notify_telegram", "false"),
            new HashEntry("notify_any_severity", "false"),
            new HashEntry("conditions", """{"message_regex":"is full|buffer i/o error"}"""),
        ]);
        await store.Db.SetAddAsync(Keys.Filters, fullKey);

        // The exact path the alert pipeline takes.
        var members = await store.Db.SetMembersAsync(Keys.Filters);
        Check("filters:all holds the full key", members.Any(m => m == fullKey),
            string.Join(",", members.Select(m => m.ToString())));

        var rule = await FilterLoader.LoadAsync(store, fullKey);
        Check("rule loads from the full-key member", rule is not null,
            "null — the double-prefix bug is back");
        if (rule is not null)
        {
            Check("name loaded", rule.Name == "磁盘异常", rule.Name);
            Check("enabled loaded", rule.Enabled);
            Check("regex loaded from conditions", rule.MessageRegex == "is full|buffer i/o error", rule.MessageRegex);
            Check("notify flags loaded", !rule.NotifyTelegram && !rule.NotifyAnySeverity);
            Check("the loaded rule actually matches the log it is meant to catch",
                FilterMatcher.Matches(rule, "127.0.0.1", "error", "disk is full"),
                "no match — end to end this means alerts never fire");
            Check("and does not match an unrelated log",
                !FilterMatcher.Matches(rule, "127.0.0.1", "info", "routine tick"));
        }

        // A bare id must still work for callers that pass one.
        var bare = await FilterLoader.LoadAsync(store, "1790028200664");
        Check("bare id still resolves", bare is not null, "null");

        Check("unknown filter returns null", await FilterLoader.LoadAsync(store, "filter:does-not-exist") is null);
        Check("malformed conditions is skipped, not thrown",
            await SeedMalformedAsync(store) is null);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task<FilterRule?> SeedMalformedAsync(RedisStore store)
    {
        await store.Db.HashSetAsync("filter:bad", [new HashEntry("conditions", "{not json")]);
        return await FilterLoader.LoadAsync(store, "filter:bad");
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
