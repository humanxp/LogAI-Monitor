// Syslog ingestion: UDP/TCP reception, parsing and Redis persistence.
//
// The stored shape is byte-for-byte what the Python application writes, so the
// two can run against the same Redis layout — that is what makes a parallel
// verification (and a later cut-over) possible without a data migration.
//
// Key layout written per message:
//   HSET  log:<µs>  id source source_type hostname program facility severity
//                   message timestamp analyzed=false   (+ pid | proc_id msg_id)
//   ZADD  logs:timeline  logs:unanalyzed  logs:source:<s>  logs:host:<h>
//         logs:severity:<sev>            (score = arrival epoch seconds)
//   SADD  logs:index:sources|hosts|severities
//   EXPIRE log:<µs>  retention hours
//   HSET  syslog:client:<ip> ...   (connected-client view)

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Syslog;

/// <summary>Writes parsed entries with the layout the Python application uses.</summary>
public sealed class LogWriter(RedisStore store, int retentionHours)
{
    private long _lastMicro;

    public async Task<string> StoreAsync(SyslogEntry entry, CancellationToken cancellationToken = default)
    {
        long micro = NextMicroseconds();
        string id = $"log:{micro}";
        double score = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var hash = new List<HashEntry>
        {
            new("id", id),
            new("source", entry.Source),
            new("source_type", entry.SourceType),
            new("hostname", entry.Hostname),
            new("program", entry.Program),
            new("facility", entry.Facility),
            new("severity", entry.Severity),
            new("message", entry.Message),
            new("timestamp", entry.Timestamp),
            new("analyzed", "false"),
        };
        if (!string.IsNullOrEmpty(entry.Pid)) hash.Add(new HashEntry("pid", entry.Pid));
        if (!string.IsNullOrEmpty(entry.ProcId)) hash.Add(new HashEntry("proc_id", entry.ProcId));
        if (!string.IsNullOrEmpty(entry.MsgId)) hash.Add(new HashEntry("msg_id", entry.MsgId));

        var db = store.Db;
        await db.HashSetAsync(id, hash.ToArray());

        var batch = db.CreateBatch();
        var pending = new List<Task>
        {
            batch.SortedSetAddAsync(Keys.Timeline, id, score),
            batch.SortedSetAddAsync(Keys.Unanalyzed, id, score),
            batch.SortedSetAddAsync(Keys.LogSource(entry.Source), id, score),
            batch.SortedSetAddAsync(Keys.LogHost(entry.Hostname), id, score),
            batch.SortedSetAddAsync(Keys.LogSeverity(entry.Severity), id, score),
            batch.SetAddAsync(Keys.SourcesIndex, entry.Source),
            batch.SetAddAsync(Keys.HostsIndex, entry.Hostname),
            batch.SetAddAsync(Keys.SeveritiesIndex, entry.Severity),
        };
        if (retentionHours > 0)
            pending.Add(batch.KeyExpireAsync(id, TimeSpan.FromHours(retentionHours)));
        batch.Execute();
        await Task.WhenAll(pending);

        return id;
    }

    /// <summary>Monotonic microsecond id, so messages in the same microsecond cannot collide.</summary>
    private long NextMicroseconds()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        while (true)
        {
            long previous = Interlocked.Read(ref _lastMicro);
            long next = now > previous ? now : previous + 1;
            if (Interlocked.CompareExchange(ref _lastMicro, next, previous) == previous) return next;
        }
    }
}

public sealed class ReceiverOptions
{
    public int UdpPort { get; init; } = 5514;
    public int TcpPort { get; init; } = 5515;
    public int Workers { get; init; } = 4;
    public int QueueCapacity { get; init; } = 8192;
}

/// <summary>UDP and TCP syslog receivers feeding a bounded queue.</summary>
public sealed class SyslogReceiver(ReceiverOptions options)
{
    private readonly Channel<(byte[] Data, string Source, bool Tcp)> _queue =
        Channel.CreateBounded<(byte[], string, bool)>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>Set when the listener actually bound; a failed bind is non-fatal
    /// but must be reported truthfully to the diagnostics endpoint.</summary>
    public bool UdpBound { get; private set; }
    public bool TcpBound { get; private set; }

    public long UdpReceived;
    public long TcpReceived;
    public long Stored;
    public long Dropped;

    /// <summary>Runs both listeners until cancelled; each stored entry is handed to onStored.</summary>
    public async Task RunAsync(Func<SyslogEntry, string, Task> onStored, CancellationToken cancellationToken)
    {
        var workers = Enumerable.Range(0, options.Workers)
            .Select(_ => Task.Run(() => ConsumeAsync(onStored, cancellationToken), cancellationToken))
            .ToArray();

        var listeners = new List<Task>
        {
            Task.Run(() => ListenUdpAsync(cancellationToken), cancellationToken),
            Task.Run(() => ListenTcpAsync(cancellationToken), cancellationToken),
        };

        await Task.WhenAll(listeners);
        _queue.Writer.TryComplete();
        await Task.WhenAll(workers);
    }

    private async Task ConsumeAsync(Func<SyslogEntry, string, Task> onStored, CancellationToken cancellationToken)
    {
        // The protocol was previously discarded here; the client tracker needs it
        // to fill syslog:client:<ip>:protocols.
        await foreach (var (data, source, tcp) in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            var entry = SyslogParser.Parse(data, source);
            await onStored(entry, tcp ? "TCP" : "UDP");
            Interlocked.Increment(ref Stored);
        }
    }

    private async Task ListenUdpAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, options.UdpPort));
        }
        catch (SocketException ex)
        {
            // A port conflict must not take the whole collector down: report it
            // and let the TCP listener (and the process) keep running.
            Console.Error.WriteLine($"UDP bind failed on port {options.UdpPort}: {ex.SocketErrorCode}");
            return;
        }
        UdpBound = true;
        Console.WriteLine($"udp bound on 0.0.0.0:{options.UdpPort}");
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            Interlocked.Increment(ref UdpReceived);
            if (!_queue.Writer.TryWrite((result.Buffer, result.RemoteEndPoint.Address.ToString(), false)))
                Interlocked.Increment(ref Dropped);
        }
    }

    private async Task ListenTcpAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, options.TcpPort);
        try
        {
            listener.Start(128);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"TCP bind failed on port {options.TcpPort}: {ex.SocketErrorCode}");
            return;
        }
        TcpBound = true;
        Console.WriteLine($"tcp bound on 0.0.0.0:{options.TcpPort}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                _ = Task.Run(() => ReadTcpClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ReadTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        using (client)
        {
            var buffer = new byte[64 * 1024];
            var pending = new List<byte>(8192);
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken);
                }
                catch (Exception) { break; }
                if (read <= 0) break;

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') { pending.Add(buffer[i]); continue; }
                    if (pending.Count > 0)
                    {
                        Interlocked.Increment(ref TcpReceived);
                        if (!_queue.Writer.TryWrite((pending.ToArray(), source, true)))
                            Interlocked.Increment(ref Dropped);
                        pending.Clear();
                    }
                }
                if (pending.Count > 64 * 1024) pending.Clear();   // absurdly long line
            }

            if (pending.Count > 0)
            {
                Interlocked.Increment(ref TcpReceived);
                if (!_queue.Writer.TryWrite((pending.ToArray(), source, true)))
                    Interlocked.Increment(ref Dropped);
            }
        }
    }
}
