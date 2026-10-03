// Per-client state for the Connected Clients view and /api/syslog/diagnostics.
//
// Four keys per client:
//   syslog:client:<ip>            hash  first_seen last_seen message_count
//                                       hostname error_count last_error last_error_time
//   syslog:client:<ip>:protocols  set   UDP / TCP
//   syslog:client:<ip>:recent     zset  one member per message in the last 60s
//   syslog:clients:index          zset  member = ip, score = last seen
//
// "recent" is a sliding window rather than a per-minute counter: every message
// adds its own timestamp and prunes anything older than 60 seconds in the same
// pipeline, so messages_per_minute is just ZCARD(recent) and there is no reset
// boundary to get wrong.
//
// Errors refresh the index score too (both branches do it)
// so an actively failing host still counts as recently active.

using System.Globalization;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Syslog;

public sealed class ClientTracker(RedisStore store)
{
    private const int WindowSeconds = 60;

    public async Task TrackAsync(string sourceIp, string protocol, string hostname,
                                 bool error = false, string? errorMessage = null,
                                 CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sourceIp)) return;

        // Microsecond precision, as an epoch float. The member name
        // must be unique per message: at millisecond precision a burst collapses
        // into one member and messages_per_minute under-reports exactly when a
        // client is flooding.
        // Epoch seconds with 100ns resolution. Both parts matter:
        //   * epoch based, so the score stays comparable with the values the
        //     external readers of the same index depend on it;
        //   * sub-millisecond, so a burst yields one window member per message.
        // UtcNow.Ticks alone counts from year 1 and produced 6.39e10.
        double now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks)
                     / (double)TimeSpan.TicksPerSecond;
        // Stored as an EPOCH FLOAT string: the
        // diagnostics endpoint parses these with float(v), and anything else
        // would fall back to 0.0 on an ISO string, marking every client stale.
        string timestamp = now.ToString(CultureInfo.InvariantCulture);
        string key = "syslog:client:" + sourceIp;
        var db = store.Db;

        if (!await db.KeyExistsAsync(key))
        {
            await db.HashSetAsync(key,
            [
                new HashEntry("first_seen", timestamp),
                new HashEntry("message_count", 0),
                new HashEntry("error_count", 0),
            ]);
        }

        await db.HashSetAsync(key,
        [
            new HashEntry("last_seen", timestamp),
            new HashEntry("hostname", hostname),
        ]);

        if (protocol.Length > 0) await db.SetAddAsync(key + ":protocols", protocol);

        if (error)
        {
            // A failed message is not counted towards the rate, but it still
            // refreshes the activity index below.
            await db.HashIncrementAsync(key, "error_count");
            await db.HashSetAsync(key, [new HashEntry("last_error", errorMessage ?? ""),
                                       new HashEntry("last_error_time", timestamp)]);
        }
        else
        {
            await db.HashIncrementAsync(key, "message_count");
            string recent = key + ":recent";
            await db.SortedSetAddAsync(recent, now.ToString(CultureInfo.InvariantCulture), now);
            await db.SortedSetRemoveRangeByScoreAsync(recent, 0, now - WindowSeconds);
        }

        await db.SortedSetAddAsync(Keys.ClientsIndex, sourceIp, now);
    }

    /// <summary>True when a record existed; removes all four keys.</summary>
    public async Task<bool> DeleteAsync(string sourceIp, CancellationToken cancellationToken = default)
    {
        string key = "syslog:client:" + sourceIp;
        var db = store.Db;
        bool existed = await db.KeyExistsAsync(key);

        await db.KeyDeleteAsync(key);
        await db.KeyDeleteAsync(key + ":protocols");
        await db.KeyDeleteAsync(key + ":recent");
        await db.SortedSetRemoveAsync(Keys.ClientsIndex, sourceIp);

        return existed;
    }

    /// <summary>Messages seen in the last 60 seconds.</summary>
    public async Task<long> MessagesPerMinuteAsync(string sourceIp, CancellationToken cancellationToken = default) =>
        await store.Db.SortedSetLengthAsync("syslog:client:" + sourceIp + ":recent");
}
