// Selects the next batch of logs to analyse.
//
// Semantics ported from redis_client.get_unanalyzed_logs:
//   * read logs:unanalyzed oldest-first (ZRANGE 0 limit-1);
//   * fetch the hashes in one pipeline;
//   * skip ids whose hash already expired (the index outlives the TTL) and ids
//     that were marked analyzed behind the index's back;
//   * do NOT top the batch back up to the requested size — the caller sees
//     fewer entries, which is the intended behaviour.
//
// Worth stating because it is easy to "improve" into a different behaviour: if
// expired ids were counted as batch members, a healthy-looking 500-log batch
// could analyse only a handful and the backlog would never drain.

using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Ai;

public static class UnanalyzedBatch
{
    public sealed record Item(string Id, Dictionary<string, string> Fields);

    public static async Task<List<Item>> FetchAsync(RedisStore store, int limit,
                                                    CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];

        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.Unanalyzed, 0, limit - 1);
        if (ids.Length == 0) return [];

        var batch = store.Db.CreateBatch();
        var pending = ids.Select(id => batch.HashGetAllAsync(id.ToString())).ToArray();
        batch.Execute();
        var hashes = await Task.WhenAll(pending);

        var items = new List<Item>(hashes.Length);
        var stale = new List<RedisValue>();
        for (int i = 0; i < hashes.Length && items.Count < limit; i++)
        {
            if (hashes[i].Length == 0) { stale.Add(ids[i]); continue; }   // hash expired, index entry stale

            var fields = hashes[i].ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
            if (string.Equals(fields.GetValueOrDefault("analyzed"), "true", StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(ids[i]);                                        // already analysed
                continue;
            }

            items.Add(new Item(ids[i].ToString(), fields));
        }

        // 自愈：把「已分析」或「哈希已消失」的条目从队列里摘掉。
        // 队列按分数（到达时间）排序、只取最前面 limit 条，所以队头一旦堆满这类条目，
        // 后面新到的日志永远排不进来 —— 分析器会一直报 nothing to analyze，
        // 而积压（ZCARD）却持续增长，健康巡检因此报 not ok。
        if (stale.Count > 0)
            await store.Db.SortedSetRemoveAsync(Keys.Unanalyzed, stale.ToArray());

        return items;
    }
}
