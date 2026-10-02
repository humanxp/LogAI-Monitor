// Self-contained end-to-end ingestion test.
//
// Everything happens inside one process: the receiver binds, the test sends
// datagrams to it over loopback, and the stored records are read back from
// Redis and checked. Relying on an external sender made the earlier attempt
// fail for reasons unrelated to the code (host/container UDP paths), so the
// loopback variant removes that variable entirely.
//
// It never touches a production database: REDIS_DB must not be 0.
//
//   dotnet LogAI.Web.dll --ingest-selftest

using System.Net;
using System.Net.Sockets;
using System.Text;
using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Web;

internal static class IngestSelfTest
{
    private const int UdpPort = 55160;
    private const int TcpPort = 55161;
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0)
        {
            Console.Error.WriteLine("refusing to run the self-test against database 0 (production)");
            return 2;
        }

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        };
        int retentionHours = 720;

        using var store = new RedisStore(options);
        Console.WriteLine($"redis {options.Host}:{options.Port}/{database} ping={await store.PingAsync()}");
        await store.Db.ExecuteAsync("FLUSHDB");
        Console.WriteLine($"database {database} cleared for the test");

        var writer = new LogWriter(store, retentionHours);
        var receiver = new SyslogReceiver(new ReceiverOptions { UdpPort = UdpPort, TcpPort = TcpPort });

        using var cancellation = new CancellationTokenSource();
        var run = receiver.RunAsync((entry, _) => writer.StoreAsync(entry), cancellation.Token);

        // Wait for the listeners, then deliver the samples over loopback.
        await Task.Delay(700);
        string stamp = DateTime.UtcNow.ToString("MMM d HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var samples = new[]
        {
            $"<134>{stamp} web01 sshd[1234]: Accepted password for root",
            $"<134>{stamp} connmand[351]: ntp: adjust (jump): +1474790149.998557 sec",
            $"<11>{stamp} GL-AXT1800 crond[4861]: USER root pid 4643 cmd /lib/functions/mptun.sh",
        };
        foreach (string sample in samples)
        {
            byte[] payload = Encoding.UTF8.GetBytes(sample);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                using var udp = new UdpClient();
                udp.Send(payload, payload.Length, "127.0.0.1", UdpPort);
                await Task.Delay(250);
                if (await store.Db.SortedSetLengthAsync(Keys.Timeline) >= samples.Length) break;
            }
        }

        // TCP path: one line terminated by a newline.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            long before = await store.Db.SortedSetLengthAsync(Keys.Timeline);
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, TcpPort);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(
                    $"<131>{stamp} tcp01 nginx[7]: upstream timed out\n"));
                await Task.Delay(300);
            }
            catch (SocketException) { }
            if (await store.Db.SortedSetLengthAsync(Keys.Timeline) > before) break;
            await Task.Delay(250);
        }

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        // ------------------------------------------------------- assertions
        var db = store.Db;
        long timeline = await db.SortedSetLengthAsync(Keys.Timeline);
        long unanalyzed = await db.SortedSetLengthAsync(Keys.Unanalyzed);
        Console.WriteLine($"\nreceived: udp={receiver.UdpReceived} tcp={receiver.TcpReceived} stored={receiver.Stored} dropped={receiver.Dropped}");
        Console.WriteLine($"keys: logs:timeline={timeline} logs:unanalyzed={unanalyzed}\n");

        Check("at least the 3 UDP samples were stored", timeline >= 3, $"timeline={timeline}");
        Check("every stored log is queued for analysis", unanalyzed == timeline, $"unanalyzed={unanalyzed} timeline={timeline}");
        Check("TCP line was accepted", receiver.TcpReceived >= 1, $"tcp={receiver.TcpReceived}");

        var records = new List<Dictionary<string, string>>();
        foreach (var id in await db.SortedSetRangeByRankAsync(Keys.Timeline, 0, -1))
        {
            var hash = await db.HashGetAllAsync(id.ToString());
            records.Add(hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal));
        }

        // The hostname guard: a program tag must never become the hostname.
        var guard = records.FirstOrDefault(r => r.GetValueOrDefault("program") == "connmand");
        Check("hostname guard: program tag is not taken for a hostname",
            guard is not null && guard["hostname"] == "127.0.0.1" && guard["pid"] == "351",
            guard is null ? "connmand record missing"
                : $"hostname={guard["hostname"]} pid={guard["pid"]}");

        var normal = records.FirstOrDefault(r => r.GetValueOrDefault("hostname") == "web01");
        Check("normal host is preserved",
            normal is not null && normal["program"] == "sshd" && normal["pid"] == "1234" &&
            normal["message"] == "Accepted password for root",
            normal is null ? "web01 record missing" : $"program={normal["program"]} message={normal["message"]}");

        // The PRI-decoded severity must reach the record and its index.
        var error = records.FirstOrDefault(r => r.GetValueOrDefault("severity") == "error");
        Check("PRI 11 decodes to severity=error",
            error is not null && error["facility"] == "user",
            error is null ? "no error record" : $"facility={error["facility"]}");

        Check("stored records carry analyzed=false",
            records.All(r => r.GetValueOrDefault("analyzed") == "false"),
            string.Join(",", records.Select(r => r.GetValueOrDefault("analyzed")).Distinct()));

        Check("id field matches the key name",
            records.All(r => r["id"].StartsWith("log:", StringComparison.Ordinal)),
            records.FirstOrDefault()?.GetValueOrDefault("id") ?? "none");

        // Indexes and registries.
        var sources = await db.SetMembersAsync(Keys.SourcesIndex);
        var hosts = await db.SetMembersAsync(Keys.HostsIndex);
        var severities = await db.SetMembersAsync(Keys.SeveritiesIndex);
        Check("sources registry", sources.Any(s => s == "127.0.0.1"), string.Join(",", sources.Select(s => s.ToString())));
        Check("hosts registry contains web01 and the loopback source",
            hosts.Any(h => h == "web01") && hosts.Any(h => h == "127.0.0.1"),
            string.Join(",", hosts.Select(h => h.ToString())));
        Check("severities registry", severities.Any(s => s == "error") && severities.Any(s => s == "info"),
            string.Join(",", severities.Select(s => s.ToString())));

        long hostWeb01 = await db.SortedSetLengthAsync(Keys.LogHost("web01"));
        long sevError = await db.SortedSetLengthAsync(Keys.LogSeverity("error"));
        long srcLoopback = await db.SortedSetLengthAsync(Keys.LogSource("127.0.0.1"));
        long expectedWeb01 = records.Count(r => r.GetValueOrDefault("hostname") == "web01");
        Check("per-host index agrees with the stored records", hostWeb01 == expectedWeb01, "index=" + hostWeb01 + " records=" + expectedWeb01);
        Check("per-severity index logs:severity:error", sevError >= 1, $"count={sevError}");
        Check("per-source index logs:source:127.0.0.1", srcLoopback == timeline, $"count={srcLoopback}");

        // Retention: the TTL has to match the configured 720 hours.
        var firstId = (await db.SortedSetRangeByRankAsync(Keys.Timeline, 0, 0)).FirstOrDefault();
        TimeSpan? ttl = await db.KeyTimeToLiveAsync(firstId.ToString());
        Check("retention TTL applied", ttl is not null && ttl.Value.TotalHours > retentionHours - 1 && ttl.Value.TotalHours <= retentionHours,
            ttl is null ? "no TTL" : $"{ttl.Value.TotalHours:0.0}h");

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            Console.WriteLine($"ok   {name}");
            return;
        }
        _failures++;
        Console.WriteLine($"FAIL {name}  ({detail})");
    }
}
