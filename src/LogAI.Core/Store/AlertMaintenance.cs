// Alert acknowledgement and cleanup.
//
//   acknowledge one      -> true when the alert existed
//   acknowledge all      -> count of alerts flipped from unacknowledged
//   clear acknowledged   -> count of alerts removed (only acknowledged ones)
//
// "Acknowledged" is the operator's assertion that a human has seen the alert, so
// it is written as the string "true"/"false" -
// /api/alerts passes the field through verbatim.

using StackExchange.Redis;

namespace LogAI.Core.Store;

public static class AlertMaintenance
{
    public static async Task<bool> AcknowledgeAsync(RedisStore store, string alertId)
    {
        if (!await store.Db.KeyExistsAsync(alertId)) return false;
        await store.Db.HashSetAsync(alertId, "acknowledged", "true");
        return true;
    }

    public static async Task<long> AcknowledgeAllAsync(RedisStore store)
    {
        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
        // 读阶段一批取回 acknowledged(原来逐条 HGET),要翻转的再一批写回:
        // 告警一多,逐条往返就是这两条端点的主要开销。
        var statuses = await ReadAcknowledgedBatchAsync(store, ids);
        var batch = store.Db.CreateBatch();
        var writes = new List<Task>();
        long count = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (statuses[i]) continue;
            writes.Add(batch.HashSetAsync(ids[i].ToString(), "acknowledged", "true"));
            count++;
        }
        if (writes.Count > 0)
        {
            batch.Execute();
            await Task.WhenAll(writes);
        }
        if (count > 0)
            Console.WriteLine("[Alerts] cleared " + count + " acknowledged alert(s)");
        return count;
    }

    /// <summary>Deletes acknowledged alerts, leaving unacknowledged ones alone.</summary>
    public static async Task<long> ClearAcknowledgedAsync(RedisStore store)
    {
        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
        // 同上:读阶段一批;删阶段把 DEL + ZREM 成对并进同一个批。
        var statuses = await ReadAcknowledgedBatchAsync(store, ids);
        var batch = store.Db.CreateBatch();
        var writes = new List<Task>();
        long count = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!statuses[i]) continue;
            writes.Add(batch.KeyDeleteAsync(ids[i].ToString()));
            writes.Add(batch.SortedSetRemoveAsync(Keys.AlertsTimeline, ids[i].ToString()));
            count++;
        }
        if (writes.Count > 0)
        {
            batch.Execute();
            await Task.WhenAll(writes);
        }
        return count;
    }

    /// <summary>每个告警的 acknowledged 状态,500 条一批取回。</summary>
    private static async Task<bool[]> ReadAcknowledgedBatchAsync(RedisStore store, RedisValue[] ids)
    {
        var flags = new bool[ids.Length];
        const int Chunk = 500;
        for (int offset = 0; offset < ids.Length; offset += Chunk)
        {
            int size = Math.Min(Chunk, ids.Length - offset);
            var batch = store.Db.CreateBatch();
            var reads = new Task<RedisValue>[size];
            for (int i = 0; i < size; i++)
                reads[i] = batch.HashGetAsync(ids[offset + i].ToString(), "acknowledged");
            batch.Execute();
            var loaded = await Task.WhenAll(reads);
            for (int i = 0; i < size; i++)
                flags[offset + i] = loaded[i].ToString() == "true";
        }
        return flags;
    }
}
