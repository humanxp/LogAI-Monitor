// Retention cleanup.
//
// Two distinct problems, both handled here because fixing only the first is
// what lets Redis grow without bound:
//
//   1. logs older than the retention window are removed;
//   2. DEAD ids are purged — a log hash disappears when its TTL fires, but the
//      timeline, the unanalyzed queue and the three index zsets keep pointing
//      at it forever. Expiry does not cascade.
//
// Index membership is read from the hash before deleting it, because afterwards
// there is no way to know which index keys the entry belonged to.
//
// Both phases are deliberately BOUNDED. An earlier version asked Redis for
// every expired id in one call and checked every queued id for existence:
// at 30 days x ~2 KB/log that is millions of entries handed over in a single
// reply, and each deletion issued six sequential round trips. The cost only
// appears on the run that crosses the retention boundary, which is exactly when
// a multi-million-entry backlog has accumulated — so it would have shown up as
// a job that looks hung rather than as steady load. Paging with a batch
// pipeline keeps memory flat and turns N*6 round trips into (N/pageSize)*2.

namespace LogAI.Core.Scheduler;

using LogAI.Core.Store;
using StackExchange.Redis;

public static class CleanupJob
{
    /// <summary>
    /// 每轮向 Redis 索取/提交的条目数。只影响往返次数与峰值内存，
    /// 不影响最终结果——分页是幂等的，每页处理完即落盘。
    /// </summary>
    public const int DefaultPageSize = 1000;

    public sealed record Result(int Removed, int DeadPurged, int AiHistoryRemoved, int AlertsRemoved, int Batches = 1);

    /// <summary>Epoch seconds before which a log is considered expired.</summary>
    public static double Cutoff(double nowSeconds, int retentionHours) =>
        nowSeconds - retentionHours * 3600.0;

    public static async Task<Result> RunAsync(RedisStore store, int retentionHours,
                                              int pageSize = DefaultPageSize,
                                              LogArchive? archive = null,
                                              CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0) pageSize = DefaultPageSize;
        var db = store.Db;
        double cutoff = Cutoff(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, retentionHours);

        int removed = await RemoveExpiredAsync(db, archive, cutoff, pageSize, cancellationToken);
        // 分析历史/告警只有一条时间线，没有维度索引/待分析队列，清理更简单。
        int aihRemoved = await RemoveExpiredSimpleAsync(db, archive, Keys.AiHistoryTimeline, cutoff, pageSize, cancellationToken);
        int alrRemoved = await RemoveExpiredSimpleAsync(db, archive, Keys.AlertsTimeline, cutoff, pageSize, cancellationToken);
        int deadPurged = await PurgeDeadQueuedAsync(db, pageSize, cancellationToken);

        await db.StringSetAsync(Keys.CleanupLastRun, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00"));
        await db.StringSetAsync(Keys.CleanupLastRemoved, removed.ToString());
        return new Result(removed, deadPurged, aihRemoved, alrRemoved);
    }

    /// <summary>
    /// 按分数取出到期的最旧一页并删除。
    ///
    /// 每页都从 rank 0 重新取，因此不需要游标：删除会把它自己从时间线摘掉，
    /// 下一轮 rank 0 就是新的最旧一条。空页即结束——这也让重复运行天然幂等。
    /// </summary>
    private static async Task<int> RemoveExpiredAsync(IDatabase db, LogArchive? archive, double cutoff,
                                                       int pageSize, CancellationToken cancellationToken)
    {
        int removed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await db.SortedSetRangeByScoreAsync(Keys.Timeline, 0, cutoff,
                                                           Exclude.None, Order.Ascending, 0, pageSize);
            if (page.Length == 0) break;

            // 先读哈希，得知它属于哪些索引；这一步之后哈希就要删掉了。
            // 并发读取：pageSize 次往返压成一批，这是本函数最贵的部分。
            var hashes = new Task<HashEntry[]>[page.Length];
            for (int i = 0; i < page.Length; i++)
                hashes[i] = db.HashGetAllAsync(page[i].ToString());
            var loaded = await Task.WhenAll(hashes);

            // 已归档日志的哈希早被搬到 SQLite（Redis 里缺失），维度要从冷存储读回，
            // 否则 source/host/severity 三个 ZSET 会残留死 id。
            if (archive is not null)
            {
                var missing = new List<string>();
                for (int i = 0; i < page.Length; i++)
                    if (loaded[i].Length == 0) missing.Add(page[i].ToString());
                if (missing.Count > 0)
                {
                    var archived = await archive.GetFieldsBatchAsync(missing, cancellationToken);
                    int k = 0;
                    for (int i = 0; i < page.Length; i++)
                        if (loaded[i].Length == 0) loaded[i] = archived[k++];
                }
            }

            var batch = db.CreateBatch();
            var pending = new List<Task>(page.Length * 8);
            var affectedSources = new HashSet<string>(StringComparer.Ordinal);
            var affectedHosts = new HashSet<string>(StringComparer.Ordinal);
            var affectedSeverities = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < page.Length; i++)
            {
                string id = page[i].ToString();
                var fields = loaded[i].ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(),
                                                    StringComparer.Ordinal);

                pending.Add(batch.KeyDeleteAsync(id));
                pending.Add(batch.SortedSetRemoveAsync(Keys.Timeline, id));
                pending.Add(batch.SortedSetRemoveAsync(Keys.Unanalyzed, id));
                if (fields.TryGetValue("source", out string? source))
                {
                    pending.Add(batch.SortedSetRemoveAsync(Keys.LogSource(source), id));
                    affectedSources.Add(source);
                }
                if (fields.TryGetValue("hostname", out string? hostname))
                {
                    pending.Add(batch.SortedSetRemoveAsync(Keys.LogHost(hostname), id));
                    affectedHosts.Add(hostname);
                }
                if (fields.TryGetValue("severity", out string? severity))
                {
                    pending.Add(batch.SortedSetRemoveAsync(Keys.LogSeverity(severity), id));
                    affectedSeverities.Add(severity);
                }

                removed++;
            }

            batch.Execute();
            await Task.WhenAll(pending);

            // 保留期到期：一并删除 SQLite 归档记录（非归档 id 是 no-op）。
            if (archive is not null)
                await archive.DeleteBatchAsync(page.Select(p => p.ToString()).ToArray(), cancellationToken);

            // 维度 ZSET 若已清空，把名字从注册表摘掉——否则停止上报的来源/主机/级别
            // 会在下拉列表里永远留下幻影条目（之前"多出来的主机"就是它）。
            await PurgeEmptyRegistriesAsync(db, affectedSources, affectedHosts, affectedSeverities, cancellationToken);
        }

        return removed;
    }

    /// <summary>
    /// 清理只有一条时间线的数据（分析历史/告警）：到期后摘除时间线 id、删 Redis 哈希、
    /// 并删 SQLite 归档记录。无维度索引/待分析队列，比日志清理简单。
    /// </summary>
    private static async Task<int> RemoveExpiredSimpleAsync(IDatabase db, LogArchive? archive,
        string timelineKey, double cutoff, int pageSize, CancellationToken cancellationToken)
    {
        int removed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await db.SortedSetRangeByScoreAsync(timelineKey, 0, cutoff,
                                                           Exclude.None, Order.Ascending, 0, pageSize);
            if (page.Length == 0) break;

            var batch = db.CreateBatch();
            var pending = new List<Task>(page.Length * 2);
            for (int i = 0; i < page.Length; i++)
            {
                string id = page[i].ToString();
                pending.Add(batch.KeyDeleteAsync(id));
                pending.Add(batch.SortedSetRemoveAsync(timelineKey, id));
            }
            batch.Execute();
            await Task.WhenAll(pending);

            // 保留期到期：一并删除 SQLite 归档记录（未归档 id 是 no-op）。
            if (archive is not null)
                await archive.DeleteHashesBatchAsync(page.Select(p => p.ToString()).ToArray(), cancellationToken);

            removed += page.Length;
        }
        return removed;
    }

    /// <summary>
    /// 清掉"仍在队列里、但哈希已过期"的条目。
    ///
    /// 按 rank 分页扫描，每页只做一次存在性检查的管道。这些 id 可能早已不在
    /// 时间线上（TTL 只作用于哈希），所以 ZREM 时间线是顺带操作、不是判断依据。
    /// </summary>
    private static async Task<int> PurgeDeadQueuedAsync(IDatabase db, int pageSize,
                                                        CancellationToken cancellationToken)
    {
        int dead = 0;
        long rank = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await db.SortedSetRangeByRankAsync(Keys.Unanalyzed, rank, rank + pageSize - 1);
            if (page.Length == 0) break;

            var existsBatch = db.CreateBatch();
            var checks = page.Select(member => existsBatch.KeyExistsAsync(member.ToString())).ToArray();
            existsBatch.Execute();
            var present = await Task.WhenAll(checks);

            var missing = new List<RedisValue>();
            for (int i = 0; i < page.Length; i++)
                if (!present[i]) missing.Add(page[i]);

            if (missing.Count > 0)
            {
                await db.SortedSetRemoveAsync(Keys.Unanalyzed, missing.ToArray());
                await db.SortedSetRemoveAsync(Keys.Timeline, missing.ToArray());
                dead += missing.Count;
            }

            // 整页存活时它们仍留在队列里：跳过本页继续往后。
            // 有删除时队列变短、rank 处已是新条目，不能前进。
            if (missing.Count == 0) rank += page.Length;
        }

        return dead;
    }

    /// <summary>
    /// 维度 ZSET 清空后，把名字从注册表（logs:index:sources/hosts/severities）摘掉。
    /// 否则停止上报的来源/主机/级别会永远留在下拉列表里，就是"多出来的主机"。
    /// </summary>
    private static async Task PurgeEmptyRegistriesAsync(IDatabase db,
        HashSet<string> sources, HashSet<string> hosts, HashSet<string> severities,
        CancellationToken cancellationToken)
    {
        var batch = db.CreateBatch();
        var lengthTasks = new List<Task<long>>(sources.Count + hosts.Count + severities.Count);
        foreach (var source in sources)
            lengthTasks.Add(batch.SortedSetLengthAsync(Keys.LogSource(source)));
        foreach (var host in hosts)
            lengthTasks.Add(batch.SortedSetLengthAsync(Keys.LogHost(host)));
        foreach (var severity in severities)
            lengthTasks.Add(batch.SortedSetLengthAsync(Keys.LogSeverity(severity)));
        batch.Execute();
        var lengths = await Task.WhenAll(lengthTasks);

        int k = 0;
        var emptySources = new List<RedisValue>();
        foreach (var source in sources) if (lengths[k++] == 0) emptySources.Add(source);
        var emptyHosts = new List<RedisValue>();
        foreach (var host in hosts) if (lengths[k++] == 0) emptyHosts.Add(host);
        var emptySeverities = new List<RedisValue>();
        foreach (var severity in severities) if (lengths[k++] == 0) emptySeverities.Add(severity);

        if (emptySources.Count > 0) await db.SetRemoveAsync(Keys.SourcesIndex, emptySources.ToArray());
        if (emptyHosts.Count > 0) await db.SetRemoveAsync(Keys.HostsIndex, emptyHosts.ToArray());
        if (emptySeverities.Count > 0) await db.SetRemoveAsync(Keys.SeveritiesIndex, emptySeverities.ToArray());
    }
}
