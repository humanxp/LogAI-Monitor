// Runs the syslog receiver against an isolated Redis database so ingestion can
// be compared with another deployment without touching production.
//
//   dotnet LogAI.Web.dll --ingest <udpPort> <tcpPort> <seconds>

using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Web;

internal static class IngestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        int udpPort = int.Parse(args[1]);
        int tcpPort = int.Parse(args[2]);
        int seconds = args.Length > 3 ? int.Parse(args[3]) : 20;

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9"),
            Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD"),
        };
        int retention = int.Parse(Environment.GetEnvironmentVariable("LOG_RETENTION_HOURS") ?? "720");

        using var store = new RedisStore(options);
        Console.WriteLine($"redis {options.Host}:{options.Port}/{options.Database} ping={await store.PingAsync()}");
        Console.WriteLine($"listening udp={udpPort} tcp={tcpPort} retention={retention}h");

        var writer = new LogWriter(store, retention);
        var receiver = new SyslogReceiver(new ReceiverOptions { UdpPort = udpPort, TcpPort = tcpPort });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await receiver.RunAsync(async (entry, protocol) =>
            {
                string id = await writer.StoreAsync(entry);
                Console.WriteLine($"stored {id} proto={protocol} host={entry.Hostname} program={entry.Program} severity={entry.Severity}");
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine($"udp={receiver.UdpReceived} tcp={receiver.TcpReceived} stored={receiver.Stored} dropped={receiver.Dropped}");
        return 0;
    }
}
