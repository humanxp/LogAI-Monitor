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
    public static async Task<bool> AcknowledgeAsync(RedisStore store, LogArchive archive, string alertId)
    {
        if (await store.Db.KeyExistsAsync(alertId))
        {
            await store.Db.HashSetAsync(alertId, "acknowledged", "true");
            return true;
        }
        // 已归档的告警：改 SQLite 里那份哈希，绝不能新建一个只有 acknowledged 的
        // Redis 哈希——读路径只在"Redis 哈希完全不存在"时才回退冷库
        // （见 ReadApi.HydrateHashesAsync），造出残缺哈希会让整条告警渲染成空行。
        if (await archive.HashExistsAsync(alertId))
        {
            await archive.UpdateHashFieldAsync(alertId, "acknowledged", "true");
            return true;
        }
        return false;
    }

    public static async Task<long> AcknowledgeAllAsync(RedisStore store, LogArchive archive)
    {
        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
        var states = await ReadAckStatesAsync(store, archive, ids);

        var batch = store.Db.CreateBatch();
        var writes = new List<Task>();
        var archivedToFlip = new List<string>();
        long count = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (states[i].Acknowledged) continue;
            string id = ids[i].ToString();
            if (states[i].InRedis) writes.Add(batch.HashSetAsync(id, "acknowledged", "true"));
            else archivedToFlip.Add(id);
            count++;
        }
        if (writes.Count > 0)
        {
            batch.Execute();
            await Task.WhenAll(writes);
        }
        // 归档条目逐条改 SQLite（哈希以整份 JSON 存放，无法批量改单个字段）
        foreach (string id in archivedToFlip)
            await archive.UpdateHashFieldAsync(id, "acknowledged", "true");

        if (count > 0)
            Console.WriteLine("[Alerts] acknowledged " + count + " alert(s), "
                + archivedToFlip.Count + " of them archived");
        return count;
    }

    /// <summary>Deletes acknowledged alerts, leaving unacknowledged ones alone.</summary>
    public static async Task<long> ClearAcknowledgedAsync(RedisStore store, LogArchive archive)
    {
        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
        var states = await ReadAckStatesAsync(store, archive, ids);

        var batch = store.Db.CreateBatch();
        var writes = new List<Task>();
        var archivedToDelete = new List<string>();
        long count = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!states[i].Acknowledged) continue;
            string id = ids[i].ToString();
            if (states[i].InRedis) writes.Add(batch.KeyDeleteAsync(id));
            else archivedToDelete.Add(id);
            writes.Add(batch.SortedSetRemoveAsync(Keys.AlertsTimeline, id));
            count++;
        }
        if (writes.Count > 0)
        {
            batch.Execute();
            await Task.WhenAll(writes);
        }
        // 冷库里的同一条也要删，否则时间线摘掉了、SQLite 里还留着（占磁盘且按 id 仍可回读）
        if (archivedToDelete.Count > 0)
            await archive.DeleteHashesBatchAsync(archivedToDelete);

        if (count > 0)
            Console.WriteLine("[Alerts] cleared " + count + " acknowledged alert(s), "
                + archivedToDelete.Count + " of them archived");
        return count;
    }

    /// <summary>
    /// 每条告警的 (是否已确认, 是否在 Redis)。归档的告警 Redis 里没有哈希，必须
    /// 落到冷库去读——否则它们的已确认状态永远读成 false，"全部确认"漏掉它们、
    /// "清除已确认"也删不掉它们。
    /// </summary>
    private static async Task<(bool Acknowledged, bool InRedis)[]> ReadAckStatesAsync(
        RedisStore store, LogArchive archive, RedisValue[] ids)
    {
        var states = new (bool Acknowledged, bool InRedis)[ids.Length];
        var archivedKeys = new List<string>();
        var archivedIndex = new List<int>();
        const int Chunk = 500;
        for (int offset = 0; offset < ids.Length; offset += Chunk)
        {
            int size = Math.Min(Chunk, ids.Length - offset);
            var batch = store.Db.CreateBatch();
            var reads = new Task<HashEntry[]>[size];
            for (int i = 0; i < size; i++)
                reads[i] = batch.HashGetAllAsync(ids[offset + i].ToString());
            batch.Execute();
            var loaded = await Task.WhenAll(reads);
            for (int i = 0; i < size; i++)
            {
                if (loaded[i].Length == 0)          // Redis 里没有 → 可能已归档
                {
                    archivedKeys.Add(ids[offset + i].ToString());
                    archivedIndex.Add(offset + i);
                    continue;
                }
                bool ack = false;
                foreach (var entry in loaded[i])
                    if (string.Equals(entry.Name.ToString(), "acknowledged", StringComparison.Ordinal))
                    { ack = entry.Value.ToString() == "true"; break; }
                states[offset + i] = (ack, true);
            }
        }

        if (archivedKeys.Count > 0)
        {
            var archivedAck = await archive.GetHashFieldBatchAsync(archivedKeys, "acknowledged");
            for (int i = 0; i < archivedKeys.Count; i++)
            {
                bool ack = archivedAck.TryGetValue(archivedKeys[i], out string? value) && value == "true";
                states[archivedIndex[i]] = (ack, false);
            }
        }
        return states;
    }
}
