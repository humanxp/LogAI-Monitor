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
        for (int i = 0; i < hashes.Length && items.Count < limit; i++)
        {
            if (hashes[i].Length == 0) continue;                 // hash expired, index entry stale

            var fields = hashes[i].ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
            if (string.Equals(fields.GetValueOrDefault("analyzed"), "true", StringComparison.OrdinalIgnoreCase))
                continue;                                        // already analysed

            items.Add(new Item(ids[i].ToString(), fields));
        }
        return items;
    }
}
