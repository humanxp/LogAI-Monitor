// Telegram 发送量统计。
//
// 每次 TelegramNotifier.SendAsync 之后记一笔，供仪表盘 Service Status 显示
// "到底发出去了多少条"。与 AiUsage 同构：累计键 + 当日键（本地日期，60 天 TTL），
// 因为容器 TZ=Asia/Shanghai，用 UTC 日期会让"今日"在凌晨显示成昨天。

using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Notify;

public static class TelegramUsage
{
    /// <summary>累计计数的哈希键。</summary>
    public const string Key = "telegram:usage";

    public const string DailyPrefix = "telegram:usage:";
    public const int DailyRetentionDays = 60;

    /// <summary>
    /// 当日键名（本地日期）。与 AiUsage 同样的理由：日志时间戳、界面时间都已统一到
    /// 本地时区，统计口径没道理还按 UTC 翻篇。
    /// </summary>
    public static string DailyKey(DateTimeOffset when) =>
        DailyPrefix + when.ToLocalTime().ToString("yyyy-MM-dd");

    public readonly record struct Snapshot(
        long Sent, long SentToday, long Week, long Failed, long UpdatedAtUnix);

    /// <summary>记一次发送结果（成功/失败都记，失败数才是排查时真正想看的）。</summary>
    public static async Task RecordAsync(RedisStore store, bool ok, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        string daily = DailyKey(now);
        var batch = store.Db.CreateBatch();
        var pending = new List<Task>(7);
        foreach (string key in new[] { Key, daily })
        {
            if (ok) pending.Add(batch.HashIncrementAsync(key, "sent", 1));
            else pending.Add(batch.HashIncrementAsync(key, "failed", 1));
            pending.Add(batch.HashSetAsync(key, "updated_at", now.ToUnixTimeSeconds()));
        }
        pending.Add(batch.KeyExpireAsync(daily, TimeSpan.FromDays(DailyRetentionDays)));
        batch.Execute();
        await Task.WhenAll(pending);
    }

    /// <summary>读快照。缺项一律算 0。</summary>
    public static async Task<Snapshot> ReadAsync(RedisStore store, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var fields = new RedisValue[] { "sent", "failed", "updated_at" };
        var total = await store.Db.HashGetAsync(Key, fields);
        var today = await store.Db.HashGetAsync(DailyKey(now), ["sent"]);

        long week = Num(today, 0);
        for (int i = 1; i < 7; i++)
            week += Num(await store.Db.HashGetAsync(DailyKey(now.AddDays(-i)), ["sent"]), 0);

        return new Snapshot(
            Sent: Num(total, 0),
            SentToday: Num(today, 0),
            Week: week,
            Failed: Num(total, 1),
            UpdatedAtUnix: Num(total, 2));
    }

    /// <summary>清零（与 token 用量一致的复位语义）。返回删掉的键数。</summary>
    public static async Task<long> ResetAsync(RedisStore store, CancellationToken ct = default)
    {
        var keys = new List<RedisKey> { Key };
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < DailyRetentionDays; i++) keys.Add(DailyKey(now.AddDays(-i)));
        return await store.Db.KeyDeleteAsync(keys.ToArray());
    }

    private static long Num(RedisValue[] values, int index) =>
        index < values.Length && long.TryParse(values[index].ToString(), out long n) ? n : 0;
}
