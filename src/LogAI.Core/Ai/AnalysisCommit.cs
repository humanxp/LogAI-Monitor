// Applies an analysis result to the logs it covered.
//
// A real analysed log record carries two extra fields, verified by reading one
// back from Redis:
//     analyzed = "true"
//     analysis = {"overall_status": ..., "issues_found": [...], ...}
// so the copy is stored on every log of the batch, not only in ai_history.
// The batch ids also have to leave logs:unanalyzed, otherwise the same logs are
// analysed again on the next tick and the backlog never shrinks.

using System.Text.Json.Nodes;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Ai;

public static class AnalysisCommit
{
    /// <summary>Marks the batch analysed and unqueues it. Returns the number updated.</summary>
    public static async Task<int> ApplyAsync(RedisStore store, IReadOnlyList<string> logIds, JsonNode analysis,
                                             CancellationToken cancellationToken = default)
    {
        if (logIds.Count == 0) return 0;
        string json = analysis.ToJsonString();
        var db = store.Db;

        // Unqueue first: if this succeeds and the field updates fail, the logs
        // are merely re-analysed later. The opposite order would lose them.
        var queued = logIds.Select(id => (RedisValue)id).ToArray();
        await db.SortedSetRemoveAsync(Keys.Unanalyzed, queued);

        int updated = 0;
        foreach (string[] chunk in Chunk(logIds, 200))
        {
            var batch = db.CreateBatch();
            var tasks = chunk.Select(id => batch.HashSetAsync(id,
            [
                new HashEntry("analyzed", "true"),
                new HashEntry("analysis", json),
            ])).ToArray();
            batch.Execute();
            await Task.WhenAll(tasks);
            updated += chunk.Length;
        }
        return updated;
    }

    /// <summary>
    /// Retires a batch that failed too many times: it leaves the queue without a
    /// result. Without this the same poison batch is retried forever — the
    /// an earlier deployment's guard raised on every attempt, the
    /// queue grew to 41k entries and the health check reported ok:false.
    /// </summary>
    public static async Task RetireAsync(RedisStore store, IReadOnlyList<string> logIds)
    {
        if (logIds.Count == 0) return;
        var queued = logIds.Select(id => (RedisValue)id).ToArray();
        await store.Db.SortedSetRemoveAsync(Keys.Unanalyzed, queued);
    }

    private static IEnumerable<string[]> Chunk(IReadOnlyList<string> items, int size)
    {
        for (int i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToArray();
    }
}
