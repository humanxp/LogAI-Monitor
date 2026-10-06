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

        // ---- 冷热分层下的确认 / 清除 ------------------------------------
        // 告警参与冷热分层，所以这两个动作必须能落到冷库。曾经它们只吃 Redis：
        //   ① 归档告警会被凭空造出"只有 acknowledged 一个字段"的残缺哈希，而读路径
        //      只在哈希完全不存在时才回退冷库 → 整条告警渲染成空行；
        //   ② "清除已确认"删不掉 SQLite 里那份：时间线摘了、冷库还留着。
        string archivePath = Path.Combine(Path.GetTempPath(), "logai-alert-selftest.db");
        if (File.Exists(archivePath)) File.Delete(archivePath);
        var archive = new LogArchive(archivePath);

        var archivedRule = new FilterRule { Id = "filter:arch", Name = "归档测试" };
        string archId = await new AlertWriter(store).WriteAsync(archivedRule, entry, "log:999");
        await archive.ArchiveHashesAsync([(archId, await store.Db.HashGetAllAsync(archId))]);
        await store.Db.KeyDeleteAsync(archId);                  // 模拟归档任务搬走哈希
        Check("archived alert left Redis", !await store.Db.KeyExistsAsync(archId));
        Check("archived alert is in SQLite", await archive.HashExistsAsync(archId));

        Check("acknowledging an archived alert succeeds",
            await AlertMaintenance.AcknowledgeAsync(store, archive, archId));
        Check("ack wrote to SQLite without creating a partial Redis hash",
            !await store.Db.KeyExistsAsync(archId)
            && (await archive.GetHashFieldBatchAsync([archId], "acknowledged")).GetValueOrDefault(archId) == "true",
            "a partial Redis hash would shadow the full SQLite record");

        string archId2 = await new AlertWriter(store).WriteAsync(archivedRule, entry, "log:1000");
        await archive.ArchiveHashesAsync([(archId2, await store.Db.HashGetAllAsync(archId2))]);
        await store.Db.KeyDeleteAsync(archId2);
        Check("acknowledge-all reaches archived alerts",
            await AlertMaintenance.AcknowledgeAllAsync(store, archive) >= 1
            && (await archive.GetHashFieldBatchAsync([archId2], "acknowledged")).GetValueOrDefault(archId2) == "true");

        Check("clear-acknowledged deletes the archived rows from SQLite",
            await AlertMaintenance.ClearAcknowledgedAsync(store, archive) >= 1
            && !await archive.HashExistsAsync(archId) && !await archive.HashExistsAsync(archId2));
        Check("clear-acknowledged removes them from the timeline too",
            await store.Db.SortedSetScoreAsync(Keys.AlertsTimeline, archId) is null
            && await store.Db.SortedSetScoreAsync(Keys.AlertsTimeline, archId2) is null);

        try { File.Delete(archivePath); } catch (IOException) { }

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
