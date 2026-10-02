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

namespace LogAI.Core.Scheduler;

using LogAI.Core.Store;
using StackExchange.Redis;

public static class CleanupJob
{
    public sealed record Result(int Removed, int DeadPurged);

    /// <summary>Epoch seconds before which a log is considered expired.</summary>
    public static double Cutoff(double nowSeconds, int retentionHours) =>
        nowSeconds - retentionHours * 3600.0;

    public static async Task<Result> RunAsync(RedisStore store, int retentionHours, int chunkSize = 2000,
                                              CancellationToken cancellationToken = default)
    {
        var db = store.Db;
        double cutoff = Cutoff(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, retentionHours);

        // ---- 1) expired logs -------------------------------------------------
        int removed = 0;
        var expired = await db.SortedSetRangeByScoreAsync(Keys.Timeline, 0, cutoff);
        foreach (var group in Chunk(expired, chunkSize))
        {
            foreach (var member in group)
            {
                string id = member.ToString();
                var hash = await db.HashGetAllAsync(id);
                var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

                await db.KeyDeleteAsync(id);
                await db.SortedSetRemoveAsync(Keys.Timeline, id);
                await db.SortedSetRemoveAsync(Keys.Unanalyzed, id);

                if (fields.TryGetValue("source", out string? source))
                    await db.SortedSetRemoveAsync(Keys.LogSource(source), id);
                if (fields.TryGetValue("hostname", out string? hostname))
                    await db.SortedSetRemoveAsync(Keys.LogHost(hostname), id);
                if (fields.TryGetValue("severity", out string? severity))
                    await db.SortedSetRemoveAsync(Keys.LogSeverity(severity), id);

                removed++;
            }
        }

        // ---- 2) dead ids in the queue whose hash already expired -------------
        int deadPurged = 0;
        var queued = await db.SortedSetRangeByRankAsync(Keys.Unanalyzed, 0, -1);
        foreach (var group in Chunk(queued, chunkSize))
        {
            var batch = db.CreateBatch();
            var exists = group.Select(member => batch.KeyExistsAsync(member.ToString())).ToArray();
            batch.Execute();
            var present = await Task.WhenAll(exists);

            var missing = new List<RedisValue>();
            for (int i = 0; i < group.Length; i++)
                if (!present[i]) missing.Add(group[i]);

            if (missing.Count == 0) continue;
            await db.SortedSetRemoveAsync(Keys.Unanalyzed, missing.ToArray());
            await db.SortedSetRemoveAsync(Keys.Timeline, missing.ToArray());
            deadPurged += missing.Count;
        }

        await db.StringSetAsync(Keys.CleanupLastRun, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00"));
        await db.StringSetAsync(Keys.CleanupLastRemoved, removed.ToString());
        return new Result(removed, deadPurged);
    }

    private static IEnumerable<RedisValue[]> Chunk(RedisValue[] items, int size)
    {
        for (int i = 0; i < items.Length; i += size)
            yield return items.Skip(i).Take(size).ToArray();
    }
}
