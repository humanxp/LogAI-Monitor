// Engine.IO v4 over HTTP long-polling, enough for the socket.io client the
// dashboard already ships.
//
// The wire format was captured from a running server rather than taken
// from the specification, because three details differ from what one would
// assume and each of them breaks the client silently:
//
//   * the reply is text/plain; charset=UTF-8, not application/json;
//   * the handshake JSON lists pingTimeout BEFORE pingInterval;
//   * multiple packets in one reply are joined with the record separator \x1e,
//     and a POST is acknowledged with the literal body "OK".
//
// Packet prefixes: 0 handshake · 2 ping · 3 pong · 4 message · 40 namespace
// connect · 42 event.

using LogAI.Web.Api;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace LogAI.Web.Realtime;

public sealed class EngineIoServer
{
    private const char Separator = '\x1e';

    private sealed class Session
    {
        public required string Sid { get; init; }
        public ConcurrentQueue<string> Outbox { get; } = new();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public bool NamespaceConnected { get; set; }
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private Func<HttpContext, bool> _isAuthenticated = _ => false;

    /// <summary>The live instance, so background jobs can publish events.</summary>
    public static EngineIoServer? Current { get; private set; }

    public void Map(WebApplication app, Func<HttpContext, bool> isAuthenticated, string path = "/socket.io/")
    {
        Current = this;
        _isAuthenticated = isAuthenticated;
        // Block bodies with an explicit return: an expression-bodied async lambda
        // converts to RequestDelegate (Func<HttpContext, Task>) and silently
        // DISCARDS the IResult, which produced 200 responses with an empty body
        // and no content type.
        app.MapGet(path, async (HttpContext http) => { return await PollAsync(http); });
        app.MapPost(path, async (HttpContext http) => { return await ReceiveAsync(http); });
    }

    /// <summary>Queues an application event for every connected client.</summary>
    /// <summary>
    /// A session that nobody polls keeps accumulating packets. The original code had
    /// no removal path at all, so every page refresh leaked a queue that grew with the
    /// log stream (measured ~40MB/hour in production) and kept the ConnectedClients
    /// count inflated. An active client drains its queue on every poll, so a queue
    /// that reaches the cap belongs to a session that is gone.
    /// </summary>
    private const int MaxOutboxPackets = 256;

    private void RetireSession(string sid, Session session)
    {
        session.NamespaceConnected = false;
        while (session.Outbox.TryDequeue(out _)) { }
        _sessions.TryRemove(sid, out _);
    }

    public void Broadcast(string eventName, object? payload)
    {
        string packet = "42" + ReadApi.SerializeLikeFlask(new object?[] { eventName, payload });
        foreach (var kv in _sessions)
        {
            var session = kv.Value;
            if (!session.NamespaceConnected) continue;
            if (session.Outbox.Count >= MaxOutboxPackets)
            {
                RetireSession(kv.Key, session);
                continue;
            }
            session.Outbox.Enqueue(packet);
        }
    }

    public int ConnectedClients => _sessions.Values.Count(s => s.NamespaceConnected);

    /// <summary>Total sessions, including ones that stopped polling but have not
    /// yet been retired. Watched together with QueuedPackets: if sessions stay flat
    /// while memory still climbs, the growth comes from somewhere else.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Packets waiting to be delivered across all sessions.</summary>
    public int QueuedPackets
    {
        get
        {
            int total = 0;
            foreach (var s in _sessions.Values) total += s.Outbox.Count;
            return total;
        }
    }

    private async Task<IResult> PollAsync(HttpContext http)
    {
        string? sid = http.Request.Query["sid"];

        if (string.IsNullOrEmpty(sid))
        {
            var session = new Session { Sid = NewSid() };
            _sessions[session.Sid] = session;
            // Field order matches the captured handshake byte for byte.
            string handshake = "0" + JsonSerializer.Serialize(new
            {
                sid = session.Sid,
                upgrades = Array.Empty<string>(),
                pingTimeout = 20000,
                pingInterval = 25000,
                maxPayload = 1000000,
            });
            return Text(handshake);
        }

        if (!_sessions.TryGetValue(sid, out var existing)) return Text("1");   // unknown session: close

        existing.LastSeen = DateTimeOffset.UtcNow;

        // Long-poll: wait briefly for something to send, then answer with a ping
        // so the client keeps the connection alive.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (existing.Outbox.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            if (http.RequestAborted.IsCancellationRequested) return Text("");
            await Task.Delay(100, http.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        var packets = new List<string>();
        while (packets.Count < 64 && existing.Outbox.TryDequeue(out string? packet))
            packets.Add(packet!);

        if (packets.Count == 0) return Text("2");
        return Text(string.Join(Separator, packets));
    }

    private async Task<IResult> ReceiveAsync(HttpContext http)
    {
        string? sid = http.Request.Query["sid"];
        if (string.IsNullOrEmpty(sid) || !_sessions.TryGetValue(sid, out var session)) return Text("1");

        string body = await new StreamReader(http.Request.Body).ReadToEndAsync();
        session.LastSeen = DateTimeOffset.UtcNow;

        foreach (string packet in body.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (packet == "40" && !session.NamespaceConnected)
            {
                if (!_isAuthenticated(http))
                {
                    // An anonymous namespace connect is refused
                    // (captured: 44{"message":"Connection rejected by server"}).
                    session.Outbox.Enqueue("44" + JsonSerializer.Serialize(
                        new { message = "Connection rejected by server" }));
                    continue;
                }

                session.NamespaceConnected = true;
                // Packet order copied from the captured exchange: the application
                // event precedes the namespace acknowledgement.
                session.Outbox.Enqueue("42" + JsonSerializer.Serialize(new object?[]
                {
                    "connected", new { status = "ok" },
                }));
                session.Outbox.Enqueue("40" + JsonSerializer.Serialize(new { sid = NewSid() }));
            }
            else if (packet == "2")
            {
                session.Outbox.Enqueue("3");            // ping -> pong
            }
        }

        return Text("OK");
    }

    private static IResult Text(string body) =>
        // Note: Results.Text(body, contentType, Encoding.UTF8) produced an empty
        // body here (200 with zero bytes). Results.Content with an explicit
        // content type writes the payload as expected.
        Results.Content(body, "text/plain; charset=UTF-8");

    private static string NewSid()
    {
        var bytes = new byte[15];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
