// Writes alert records and owns the Telegram suppression window.
//
// Record shape and key layout are the project's storage contract, verified
// against a real alert read back from Redis:
//   alert:<ms>  acknowledged filter_id filter_name hostname id log_id message
//               severity source timestamp
//   ZADD alerts:timeline <epoch seconds> alert:<ms>
//   EXPIRE alert:<ms> 30 days
//   SET notif_cooldown:<host>:<filter> 1 EX <minutes*60> NX

using LogAI.Core.Store;
using LogAI.Core.Syslog;
using StackExchange.Redis;

namespace LogAI.Core.Filters;

public sealed class AlertWriter(RedisStore store, int retentionDays = 30)
{
    private const int MessageLimit = 500;
    private long _lastMilliseconds;

    public async Task<string> WriteAsync(FilterRule rule, SyslogEntry entry, string logId,
                                         CancellationToken cancellationToken = default)
    {
        string id = $"alert:{NextMilliseconds()}";
        string message = entry.Message.Length <= MessageLimit
            ? entry.Message
            : entry.Message[..MessageLimit];

        var db = store.Db;
        await db.HashSetAsync(id,
        [
            new HashEntry("acknowledged", "false"),
            new HashEntry("filter_id", rule.Id),
            new HashEntry("filter_name", rule.Name),
            new HashEntry("hostname", entry.Hostname),
            new HashEntry("id", id),
            new HashEntry("log_id", logId),
            new HashEntry("message", message),
            new HashEntry("severity", entry.Severity),
            new HashEntry("source", entry.Source),
            new HashEntry("timestamp", SyslogParser.NowTimestamp()),
        ]);

        double score = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        await db.SortedSetAddAsync(Keys.AlertsTimeline, id, score);
        if (retentionDays > 0)
            await db.KeyExpireAsync(id, TimeSpan.FromDays(retentionDays));

        return id;
    }

    /// <summary>
    /// True when this caller won the suppression window. SET NX EX makes the
    /// decision atomic, so concurrent workers cannot both notify.
    /// </summary>
    public async Task<bool> AcquireCooldownAsync(string hostname, string filterId, int minutes)
    {
        if (minutes <= 0) return true;
        var result = await store.Db.StringSetAsync(
            Keys.NotifyCooldown(hostname, filterId), "1", TimeSpan.FromMinutes(minutes), When.NotExists);
        return result;
    }

    private long NextMilliseconds()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            long previous = Interlocked.Read(ref _lastMilliseconds);
            long next = now > previous ? now : previous + 1;
            if (Interlocked.CompareExchange(ref _lastMilliseconds, next, previous) == previous) return next;
        }
    }
}
