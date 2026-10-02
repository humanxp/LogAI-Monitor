// Verifies alert writing and the suppression window against an isolated
// database. Refuses database 0 so production data can never be touched.

using LogAI.Core.Filters;
using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Web;

internal static class AlertSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0)
        {
            Console.Error.WriteLine("refusing to run against database 0 (production)");
            return 2;
        }

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        };
        using var store = new RedisStore(options);
        await store.Db.ExecuteAsync("FLUSHDB");
        Console.WriteLine($"database {database} cleared");

        var rule = new FilterRule { Id = "filter:disk", Name = "磁盘/文件系统异常", NotifyTelegram = true };
        var entry = new SyslogEntry
        {
            Source = "10.10.10.7",
            Hostname = "GL-AXT1800",
            Program = "kernel",
            Facility = "kern",
            Severity = "error",
            Message = new string('x', 600),          // longer than the 500 cap
            Timestamp = SyslogParser.NowTimestamp(),
        };

        var writer = new AlertWriter(store);
        string id = await writer.WriteAsync(rule, entry, "log:1234567890");

        var hash = await store.Db.HashGetAllAsync(id);
        var stored = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

        string[] expected =
        [
            "acknowledged", "filter_id", "filter_name", "hostname", "id",
            "log_id", "message", "severity", "source", "timestamp",
        ];
        Check("alert id has the alert: prefix", id.StartsWith("alert:", StringComparison.Ordinal), id);
        Check("all ten fields present, no extras",
            stored.Count == expected.Length && expected.All(stored.ContainsKey),
            string.Join(",", stored.Keys.OrderBy(k => k)));
        Check("acknowledged defaults to false", stored.GetValueOrDefault("acknowledged") == "false",
            stored.GetValueOrDefault("acknowledged") ?? "");
        Check("message capped at 500 characters",
            stored.GetValueOrDefault("message")?.Length == 500,
            (stored.GetValueOrDefault("message")?.Length ?? 0).ToString());
        Check("timeline indexed",
            await store.Db.SortedSetScoreAsync(Keys.AlertsTimeline, id) is not null, "score missing");
        Check("retention applied (30 days)",
            await store.Db.KeyTimeToLiveAsync(id) is { } ttl && ttl.TotalDays > 29 && ttl.TotalDays <= 30,
            (await store.Db.KeyTimeToLiveAsync(id))?.ToString() ?? "no ttl");

        // Suppression window: first caller wins, the second must lose.
        var second = new AlertWriter(store);
        Check("first cooldown acquisition succeeds",
            await writer.AcquireCooldownAsync("GL-AXT1800", rule.Id, 60));
        Check("second acquisition is suppressed",
            !await second.AcquireCooldownAsync("GL-AXT1800", rule.Id, 60));
        Check("a different filter is unaffected",
            await writer.AcquireCooldownAsync("GL-AXT1800", "filter:other", 60));
        Check("a different host is unaffected",
            await writer.AcquireCooldownAsync("other-host", rule.Id, 60));
        Check("cooldown carries the configured expiry",
            await store.Db.KeyTimeToLiveAsync(Keys.NotifyCooldown("GL-AXT1800", rule.Id)) is { } window
                && window.TotalMinutes > 59 && window.TotalMinutes <= 60,
            (await store.Db.KeyTimeToLiveAsync(Keys.NotifyCooldown("GL-AXT1800", rule.Id)))?.ToString() ?? "");
        Check("zero minutes disables the window",
            await writer.AcquireCooldownAsync("GL-AXT1800", "filter:none", 0));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
