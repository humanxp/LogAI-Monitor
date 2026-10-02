// Alert acknowledgement and cleanup.
//
//   acknowledge one      -> true when the alert existed
//   acknowledge all      -> count of alerts flipped from unacknowledged
//   clear acknowledged   -> count of alerts removed (only acknowledged ones)
//
// "Acknowledged" is the operator's assertion that a human has seen the alert, so
// it is written as the string "true"/"false" exactly like the Python version -
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
        long count = 0;
        foreach (var id in ids)
        {
            if ((await store.Db.HashGetAsync(id.ToString(), "acknowledged")).ToString() == "true") continue;
            await store.Db.HashSetAsync(id.ToString(), "acknowledged", "true");
            count++;
        }
        return count;
    }

    /// <summary>Deletes acknowledged alerts, leaving unacknowledged ones alone.</summary>
    public static async Task<long> ClearAcknowledgedAsync(RedisStore store)
    {
        var ids = await store.Db.SortedSetRangeByRankAsync(Keys.AlertsTimeline, 0, -1);
        long count = 0;
        foreach (var id in ids)
        {
            if ((await store.Db.HashGetAsync(id.ToString(), "acknowledged")).ToString() != "true") continue;
            await store.Db.KeyDeleteAsync(id.ToString());
            await store.Db.SortedSetRemoveAsync(Keys.AlertsTimeline, id);
            count++;
        }
        return count;
    }
}
