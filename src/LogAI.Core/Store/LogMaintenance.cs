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
    public static async Task<long> ClearAllAsync(RedisStore store, LogArchive? archive = null,
                                                 CancellationToken cancellationToken = default)
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
        // log:<id> 的哈希也必须删。原来只删上面的 ZSET 索引，哈希留了下来：
        // 线上实测一次清空后攒了 13 万个"没有任何索引引用"的孤儿哈希，要等 90 天
        // TTL 才自己消失，占了 Redis 约四成内存。它们不在任何索引里，只有 SCAN 扫得到。
        long hashes = await store.DeleteKeysByPatternAsync("log:*");
        Console.WriteLine("[Logs] also deleted " + hashes + " log hash(es) from Redis");
        // 冷归档也要清：否则 SQLite 里还留着老日志（界面看不见，但占磁盘，
        // 而且按 id 回读时还能命中）。
        if (archive is not null)
        {
            long archived = await archive.ClearLogsAsync(cancellationToken);
            Console.WriteLine("[Logs] also cleared " + archived + " archived row(s) from SQLite");
        }
        return count;
    }

    /// <summary>
    /// 清理"孤儿日志哈希"：Redis 里存在、但不在 <c>logs:timeline</c> 里的 <c>log:*</c> 键。
    ///
    /// 正常流程不会产生它们——归档是"删哈希、留索引"，清理是"哈希与索引一起删"。
    /// 只有旧版「清空所有数据」会留下（它只 KeyDelete 了 ZSET，没删哈希），线上实测
    /// 一次清空后攒了约 13 万个，占 Redis 四成内存、要等 90 天 TTL 才自己消失。
    /// 删它们不动任何索引，纯粹回收内存。
    ///
    /// 安全网：只删 id 时间戳至少 1 小时前的键。扫描期间新写入的日志可能还没进
    /// 上面的时间线快照，用 age 兜底，绝不误删活日志。
    /// </summary>
    public static async Task<long> PurgeOrphanLogHashesAsync(RedisStore store,
                                                             CancellationToken cancellationToken = default)
    {
        var db = store.Db;
        // 时间线的成员本身就是 "log:<id>"，直接当白名单用。
        var keep = new HashSet<string>(StringComparer.Ordinal);
        const int pageSize = 5000;
        long rank = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await db.SortedSetRangeByRankAsync(Keys.Timeline, rank, rank + pageSize - 1);
            if (page.Length == 0) break;
            foreach (var member in page) keep.Add(member.ToString());
            rank += page.Length;
        }

        long cutoffUs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() * 1000L;
        return await store.DeleteKeysByPatternAsync("log:*", key =>
        {
            if (keep.Contains(key)) return false;
            // key 形如 "log:<微秒时间戳>"；解析不出时间戳的保守留下。
            return long.TryParse(key.AsSpan(4), out long us) && us < cutoffUs;
        }, pageSize: 2000);
    }

    /// <summary>Removes every log whose source equals the given value.</summary>
    public static async Task<long> DeleteBySourceAsync(RedisStore store, string source,
                                                       LogArchive? archive = null,
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
        // 冷归档里同一来源的老日志也要删（否则 SQLite 里仍有残留）。
        if (archive is not null)
        {
            long archived = await archive.DeleteLogsBySourceAsync(source, cancellationToken);
            if (archived > 0)
                Console.WriteLine("[Logs] also deleted " + archived + " archived row(s) from SQLite");
        }
        Console.WriteLine("[Logs] deleted " + removed + " log(s) from source " + source);
        return removed;
    }

    /// <summary>
    /// Clears the whole Connected-Clients list. A client owns several keys (hash +
    /// protocols + recent-window zset + index entry); deleting only the index leaves
    /// the hash behind with a stale message_count, so the old "Messages" count
    /// resurrects once the host sends again. SCAN 扫全部 syslog:client:*，孤儿也一并清。
    /// </summary>
    public static async Task<long> DeleteAllClientsAsync(RedisStore store,
                                                         CancellationToken cancellationToken = default)
    {
        long count = await store.DeleteKeysByPatternAsync("syslog:client:*");
        await store.Db.KeyDeleteAsync(Keys.ClientsIndex);
        return count;
    }

    /// <summary>
    /// 清空分析历史 + 告警 + AI Token 用量 + Telegram 推送计数（Redis 与 SQLite 冷归档
    /// 一并删），配 "Clear All Data" 的"一键回到初始状态"。只删这些数据键，不动设置、
    /// 过滤器与账号。
    /// </summary>
    public static async Task ClearAnalysisAndAlertsAsync(RedisStore store,
                                                         LogArchive? archive = null,
                                                         CancellationToken cancellationToken = default)
    {
        await store.DeleteKeysByPatternAsync("ai_history:*");
        await store.DeleteKeysByPatternAsync("alert:*");
        await store.DeleteKeysByPatternAsync("alerts:*");
        // AI Token 用量 + Telegram 推送计数也一并初始化（累计键 + 当日键）。
        await store.DeleteKeysByPatternAsync("ai:usage*");
        await store.DeleteKeysByPatternAsync("telegram:usage*");
        if (archive is not null)
        {
            await archive.DeleteHashesByPrefixAsync("ai_history:", cancellationToken);
            await archive.DeleteHashesByPrefixAsync("alert:", cancellationToken);
        }
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
