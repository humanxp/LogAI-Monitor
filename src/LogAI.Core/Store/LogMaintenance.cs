// Bulk log deletion used by the write endpoints.
//
// Both operations must remove every trace of a log, not just the hash: the
// timeline, the unanalyzed queue and the three index zsets all hold ids, and an
// orphaned id is what makes the dashboard show entries that cannot be opened.
//
// The deletion order is: read the ids first (so the count is the number of logs
// that actually existed), then remove the indexes, then the hashes.

using StackExchange.Redis;

namespace LogAI.Core.Store;

public static class LogMaintenance
{
    /// <summary>Wipes every log plus the client records. Returns logs deleted.</summary>
    public static async Task<long> ClearAllAsync(RedisStore store, CancellationToken cancellationToken = default)
    {
        var db = store.Db;
        long count = await db.SortedSetLengthAsync(Keys.Timeline);

        var sources = await db.SetMembersAsync(Keys.SourcesIndex);
        var hosts = await db.SetMembersAsync(Keys.HostsIndex);
        var severities = await db.SetMembersAsync(Keys.SeveritiesIndex);

        await db.KeyDeleteAsync(Keys.Timeline);
        await db.KeyDeleteAsync(Keys.Unanalyzed);
        await db.KeyDeleteAsync(Keys.SourcesIndex);
        await db.KeyDeleteAsync(Keys.HostsIndex);
        await db.KeyDeleteAsync(Keys.SeveritiesIndex);
        await db.KeyDeleteAsync(Keys.ClientsIndex);

        foreach (var source in sources) await db.KeyDeleteAsync(Keys.LogSource(source.ToString()));
        foreach (var host in hosts) await db.KeyDeleteAsync(Keys.LogHost(host.ToString()));
        foreach (var severity in severities) await db.KeyDeleteAsync(Keys.LogSeverity(severity.ToString()));

        Console.WriteLine("[Logs] cleared all " + count + " log(s)");
        return count;
    }

    /// <summary>Removes every log whose source equals the given value.</summary>
    public static async Task<long> DeleteBySourceAsync(RedisStore store, string source,
                                                       CancellationToken cancellationToken = default)
    {
        var db = store.Db;
        var ids = await db.SortedSetRangeByRankAsync(Keys.LogSource(source), 0, -1);
        if (ids.Length == 0)
        {
            // The source index may be missing the entry; fall back to scanning the
            // timeline so a host can still be removed from the UI.
            var all = await db.SortedSetRangeByRankAsync(Keys.Timeline, 0, -1);
            var matching = new List<RedisValue>();
            foreach (var id in all)
                if ((await db.HashGetAsync(id.ToString(), "source")).ToString() == source)
                    matching.Add(id);
            ids = matching.ToArray();
        }

        long removed = 0;
        foreach (var idValue in ids)
        {
            string id = idValue.ToString();
            var hash = await db.HashGetAllAsync(id);
            var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

            await db.KeyDeleteAsync(id);
            await db.SortedSetRemoveAsync(Keys.Timeline, id);
            await db.SortedSetRemoveAsync(Keys.Unanalyzed, id);
            if (fields.TryGetValue("hostname", out string? hostname))
                await db.SortedSetRemoveAsync(Keys.LogHost(hostname), id);
            if (fields.TryGetValue("severity", out string? severity))
                await db.SortedSetRemoveAsync(Keys.LogSeverity(severity), id);
            removed++;
        }

        // The source itself disappears once its last log is gone.
        await db.KeyDeleteAsync(Keys.LogSource(source));
        await db.SetRemoveAsync(Keys.SourcesIndex, source);
        Console.WriteLine("[Logs] deleted " + removed + " log(s) from source " + source);
        return removed;
    }

    /// <summary>Clears the whole Connected-Clients list.</summary>
    public static async Task<long> DeleteAllClientsAsync(RedisStore store,
                                                         CancellationToken cancellationToken = default)
    {
        long count = await store.Db.SortedSetLengthAsync(Keys.ClientsIndex);
        await store.Db.KeyDeleteAsync(Keys.ClientsIndex);
        return count;
    }

    /// <summary>
    /// Forgets a connected-client record. A client owns FOUR keys (see
    /// ClientTracker): removing only the index entry leaves the hash, the
    /// protocol set and the recent-window zset behind, so the record reappears
    /// with stale counters once the host sends again.
    /// </summary>
    public static async Task<bool> DeleteClientAsync(RedisStore store, string source)
    {
        string key = "syslog:client:" + source;
        bool existed = await store.Db.KeyExistsAsync(key);
        await store.Db.KeyDeleteAsync(key);
        await store.Db.KeyDeleteAsync(key + ":protocols");
        await store.Db.KeyDeleteAsync(key + ":recent");
        await store.Db.SortedSetRemoveAsync(Keys.ClientsIndex, source);
        return existed;
    }
}
