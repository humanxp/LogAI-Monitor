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

        // 三个维度索引的删除并成一个批:原来逐键串行,来源/主机一多就逐个等。
        {
            var batch = db.CreateBatch();
            var deletes = new List<Task>(sources.Length + hosts.Length + severities.Length);
            foreach (var source in sources) deletes.Add(batch.KeyDeleteAsync(Keys.LogSource(source.ToString())));
            foreach (var host in hosts) deletes.Add(batch.KeyDeleteAsync(Keys.LogHost(host.ToString())));
            foreach (var severity in severities) deletes.Add(batch.KeyDeleteAsync(Keys.LogSeverity(severity.ToString())));
            batch.Execute();
            await Task.WhenAll(deletes);
        }

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
            // 扫描也分批管道化:原来对整条时间线逐条 HGET(300 万条就是 300 万次
            // 串行往返,一次回退扫描能占住端点几分钟);500 条一批后同样的扫描
            // 只是几百次往返。
            var all = await db.SortedSetRangeByRankAsync(Keys.Timeline, 0, -1);
            var matching = new List<RedisValue>();
            const int ScanChunk = 500;
            for (int offset = 0; offset < all.Length; offset += ScanChunk)
            {
                int size = Math.Min(ScanChunk, all.Length - offset);
                var batch = db.CreateBatch();
                var reads = new Task<RedisValue>[size];
                for (int i = 0; i < size; i++)
                    reads[i] = batch.HashGetAsync(all[offset + i].ToString(), "source");
                batch.Execute();
                var loaded = await Task.WhenAll(reads);
                for (int i = 0; i < size; i++)
                    if (loaded[i].ToString() == source)
                        matching.Add(all[offset + i]);
            }
            ids = matching.ToArray();
        }

        long removed = 0;
        // 删除同样分批:先一批取回哈希(hostname/severity 决定要清哪些维度索引),
        // 再把 DEL + 各维度的 ZREM 并进同一个批;同一维度的多个 id 合并成
        // 一次 ZREM(原来每个 id 五次串行往返)。
        const int DeleteChunk = 500;
        for (int offset = 0; offset < ids.Length; offset += DeleteChunk)
        {
            int size = Math.Min(DeleteChunk, ids.Length - offset);
            var chunkIds = new RedisValue[size];
            for (int i = 0; i < size; i++) chunkIds[i] = ids[offset + i];
            var hashes = await store.HashGetAllBatchAsync(chunkIds);

            var batch = db.CreateBatch();
            var pending = new List<Task>();
            var timelineIds = new List<RedisValue>(size);
            var unanalyzedIds = new List<RedisValue>(size);
            var byHost = new Dictionary<string, List<RedisValue>>(StringComparer.Ordinal);
            var bySeverity = new Dictionary<string, List<RedisValue>>(StringComparer.Ordinal);
            for (int i = 0; i < size; i++)
            {
                string id = chunkIds[i].ToString();
                var fields = hashes[i].ToDictionary(
                    h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
                pending.Add(batch.KeyDeleteAsync(id));
                timelineIds.Add(id);
                unanalyzedIds.Add(id);
                if (fields.TryGetValue("hostname", out string? hostname))
                {
                    if (!byHost.TryGetValue(hostname, out var hostGroup)) { hostGroup = []; byHost[hostname] = hostGroup; }
                    hostGroup.Add(id);
                }
                if (fields.TryGetValue("severity", out string? severity))
                {
                    if (!bySeverity.TryGetValue(severity, out var sevGroup)) { sevGroup = []; bySeverity[severity] = sevGroup; }
                    sevGroup.Add(id);
                }
                removed++;
            }
            pending.Add(batch.SortedSetRemoveAsync(Keys.Timeline, timelineIds.ToArray()));
            pending.Add(batch.SortedSetRemoveAsync(Keys.Unanalyzed, unanalyzedIds.ToArray()));
            foreach (var pair in byHost)
                pending.Add(batch.SortedSetRemoveAsync(Keys.LogHost(pair.Key), pair.Value.ToArray()));
            foreach (var pair in bySeverity)
                pending.Add(batch.SortedSetRemoveAsync(Keys.LogSeverity(pair.Key), pair.Value.ToArray()));
            batch.Execute();
            await Task.WhenAll(pending);
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
