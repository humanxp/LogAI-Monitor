// AI token 用量累计。
//
// 后端（vLLM 的 OpenAI 兼容端点）每次响应都带 usage 对象，例如：
//   {"prompt_tokens":38,"completion_tokens":1,"total_tokens":39,
//    "prompt_tokens_details":{"cached_tokens":0}, ...}
// 这里把它累加进 Redis，供仪表盘 Service Status 显示。刻意不读后端自己的
// Prometheus /metrics：本部署该端点 404，而且我们想要的正是"本应用消耗了多少"，
// 而不是这台机器上所有客户端的合计。
//
// 两个键：
//   ai:usage            累计总量（不设 TTL）
//   ai:usage:<date>     当日量（60 天 TTL，用来显示"今天/近 7 天用了多少"）
//
// 后端没给 usage（例如 Ollama 原生协议）时静默跳过，不写零值噪音。

using System.Text.Json;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Core.Ai;

public static class AiUsage
{
    /// <summary>累计用量的哈希键。</summary>
    public const string Key = "ai:usage";

    /// <summary>当日用量的哈希键前缀（后面接 yyyy-MM-dd）。</summary>
    public const string DailyPrefix = "ai:usage:";

    /// <summary>日键保留天数。</summary>
    public const int DailyRetentionDays = 60;

    public static string DailyKey(DateTimeOffset when) =>
        DailyPrefix + when.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>用量快照。</summary>
    public readonly record struct Snapshot(
        long Total, long Today, long Week, long Prompt, long Completion,
        long Cached, long Calls, long UpdatedAtUnix);

    /// <summary>把一次调用的 token 用量累加进 Redis。</summary>
    public static async Task RecordAsync(RedisStore store, JsonElement usage, CancellationToken ct = default)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;

        long prompt = Long(usage, "prompt_tokens");
        long completion = Long(usage, "completion_tokens");
        long total = Long(usage, "total_tokens");
        if (total == 0) total = prompt + completion;
        if (total == 0) return;                      // 后端没报用量，不写零值

        // 缓存命中（vLLM/SGLang 放在 prompt_tokens_details.cached_tokens）
        long cached = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
            cached = Long(details, "cached_tokens");

        var now = DateTimeOffset.UtcNow;
        string daily = DailyKey(now);
        var batch = store.Db.CreateBatch();
        var pending = new List<Task>(16);
        foreach (string key in new[] { Key, daily })
        {
            pending.Add(batch.HashIncrementAsync(key, "calls", 1));
            pending.Add(batch.HashIncrementAsync(key, "prompt_tokens", prompt));
            pending.Add(batch.HashIncrementAsync(key, "completion_tokens", completion));
            pending.Add(batch.HashIncrementAsync(key, "total_tokens", total));
            if (cached > 0) pending.Add(batch.HashIncrementAsync(key, "cached_tokens", cached));
            pending.Add(batch.HashSetAsync(key, "updated_at", now.ToUnixTimeSeconds()));
        }
        pending.Add(batch.KeyExpireAsync(daily, TimeSpan.FromDays(DailyRetentionDays)));
        batch.Execute();
        await Task.WhenAll(pending);
    }

    /// <summary>读用量快照。任何一项缺失都算 0——统计不该因为键还没建出来就报错。</summary>
    public static async Task<Snapshot> ReadAsync(RedisStore store, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var fields = new RedisValue[] { "total_tokens", "prompt_tokens", "completion_tokens", "cached_tokens", "calls", "updated_at" };
        var total = await store.Db.HashGetAsync(Key, fields);
        var today = await store.Db.HashGetAsync(DailyKey(now), ["total_tokens"]);

        long week = Num(today, 0);
        for (int i = 1; i < 7; i++)
            week += Num(await store.Db.HashGetAsync(DailyKey(now.AddDays(-i)), ["total_tokens"]), 0);

        return new Snapshot(
            Total: Num(total, 0),
            Today: Num(today, 0),
            Week: week,
            Prompt: Num(total, 1),
            Completion: Num(total, 2),
            Cached: Num(total, 3),
            Calls: Num(total, 4),
            UpdatedAtUnix: Num(total, 5));
    }

    /// <summary>清零累计与所有日键（仪表盘上的"复位清零"）。返回删掉的键数。</summary>
    public static async Task<long> ResetAsync(RedisStore store, CancellationToken ct = default)
    {
        var keys = new List<RedisKey> { Key };
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < DailyRetentionDays; i++) keys.Add(DailyKey(now.AddDays(-i)));
        return await store.Db.KeyDeleteAsync(keys.ToArray());
    }

    private static long Num(RedisValue[] values, int index) =>
        index < values.Length && long.TryParse(values[index].ToString(), out long n) ? n : 0;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long n) ? n : 0;
}
