// 归档任务：把超过 archive_after_hours 的日志哈希搬到 SQLite，Redis 只留 ZSET 索引。
//
// 增量推进：Redis 里存一个水位 `logs:archive:watermark`（已归档到的最大 score），
// 每轮只处理 (水位, 截止) 这一小段"新变老"的日志——首轮追平历史存量时是多批，
// 之后每轮就是最近几分钟越过阈值的那一小撮。已归档/已删除的日志（哈希缺失）
// 作为幂等安全网跳过。
//
// 关键约定（与清理任务的分工）：
//   * 归档只删哈希、保留 timeline/source/host/severity 四个 ZSET 的 id，因此时间/
//     维度筛选与总数统计完全不变；
//   * 保留期到期时，清理任务把 id 从 ZSET 摘除（维度从 SQLite 读回），并删除
//     SQLite 记录——30 天保留仍然成立，只是中间 7~30 天这段从内存搬到了盘上。

using System.Globalization;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Scheduler;

public static class LogArchiveJob
{
    /// <summary>每批处理的条数：只影响往返次数与峰值内存，不影响结果（幂等）。</summary>
    public const int DefaultBatchSize = 5000;

    public sealed record Result(long Archived, double Watermark);

    public static async Task<Result> RunAsync(RedisStore store, LogArchive archive, int archiveHours,
                                              int batchSize = DefaultBatchSize,
                                              CancellationToken cancellationToken = default)
    {
        var db = store.Db;
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double cutoff = now - archiveHours * 3600.0;

        // 水位：score <= watermark 的日志已归档。score 用 epoch 秒，与时间线分数一致，
        // 不受"清理删除导致 rank 平移"的影响。
        double watermark = 0;
        var raw = await db.StringGetAsync(Keys.ArchiveWatermark);
        if (raw.HasValue && double.TryParse(raw.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double w))
            watermark = w;

        if (cutoff <= watermark) return new Result(0, watermark);

        long archived = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            // 取 (watermark, cutoff] 最旧的一页（含分数）。
            var page = await db.SortedSetRangeByScoreWithScoresAsync(
                Keys.Timeline, watermark, cutoff, Exclude.Start, Order.Ascending, 0, batchSize);
            if (page.Length == 0) break;

            var ids = new RedisValue[page.Length];
            for (int i = 0; i < page.Length; i++) ids[i] = page[i].Element;

            // 一批取回哈希（已归档/已删除的会返回空）。
            var hashes = await store.HashGetAllBatchAsync(ids);

            var toArchive = new List<ArchivedLog>(page.Length);
            for (int i = 0; i < page.Length; i++)
            {
                if (hashes[i].Length == 0) continue;          // 幂等安全网
                toArchive.Add(ArchivedLog.FromHash(hashes[i], page[i].Score));
            }
            if (toArchive.Count > 0)
                await archive.ArchiveAsync(toArchive, cancellationToken);

            // 删哈希 + 从未分析队列摘除（这么老的日志不会再回头分析）。ZSET 索引保留。
            var batch = db.CreateBatch();
            var pending = new List<Task>(page.Length + 1);
            for (int i = 0; i < page.Length; i++)
                pending.Add(batch.KeyDeleteAsync(page[i].Element.ToString()));
            pending.Add(batch.SortedSetRemoveAsync(Keys.Unanalyzed, ids));
            batch.Execute();
            await Task.WhenAll(pending);

            archived += toArchive.Count;
            // 关键：水位必须随每页推进，否则下次还取同一页，无限循环。
            watermark = page[^1].Score;
            if (page.Length < batchSize) { watermark = cutoff; break; }  // 本页未满 → 已追平
        }

        await db.StringSetAsync(Keys.ArchiveWatermark,
            watermark.ToString("0.000", CultureInfo.InvariantCulture));

        return new Result(archived, watermark);
    }
}
