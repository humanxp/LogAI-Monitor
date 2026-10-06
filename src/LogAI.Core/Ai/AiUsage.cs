// AI token 用量累计。
//
// 后端（vLLM 的 OpenAI 兼容端点）每次响应都带 usage 对象：
//   {"prompt_tokens":38,"completion_tokens":1,"total_tokens":39, ...}
// 这里把它累加进 Redis，供仪表盘 Service Status 显示。刻意不读后端自己的
// Prometheus /metrics：本部署该端点 404，而且我们想要的正是"本应用消耗了多少"，
// 而不是这台机器上所有客户端的合计。
//
// 两个键：
//   ai:usage            累计总量（不设 TTL）
//   ai:usage:<date>     当日量（60 天 TTL，用来显示"今天用了多少"）
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

    public static string DailyKey(DateTimeOffset when) =>
        "ai:usage:" + when.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>把一次调用的 token 用量累加进 Redis。</summary>
    public static async Task RecordAsync(RedisStore store, JsonElement usage, CancellationToken ct = default)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;

        long prompt = Long(usage, "prompt_tokens");
        long completion = Long(usage, "completion_tokens");
        long total = Long(usage, "total_tokens");
        if (total == 0) total = prompt + completion;
        if (total == 0) return;                      // 后端没报用量，不写零值

        var now = DateTimeOffset.UtcNow;
        string daily = DailyKey(now);
        var batch = store.Db.CreateBatch();
        var pending = new List<Task>(12);
        foreach (string key in new[] { Key, daily })
        {
            pending.Add(batch.HashIncrementAsync(key, "calls", 1));
            pending.Add(batch.HashIncrementAsync(key, "prompt_tokens", prompt));
            pending.Add(batch.HashIncrementAsync(key, "completion_tokens", completion));
            pending.Add(batch.HashIncrementAsync(key, "total_tokens", total));
            pending.Add(batch.HashSetAsync(key, "updated_at", now.ToUnixTimeSeconds()));
        }
        pending.Add(batch.KeyExpireAsync(daily, TimeSpan.FromDays(60)));
        batch.Execute();
        await Task.WhenAll(pending);
    }

    /// <summary>
    /// 读累计与当日用量。返回 (total, today, prompt, completion, calls)。
    /// 任何一项缺失都算 0——统计不该因为键还没建出来就报错。
    /// </summary>
    public static async Task<(long Total, long Today, long Prompt, long Completion, long Calls)> ReadAsync(
        RedisStore store, CancellationToken ct = default)
    {
        string daily = DailyKey(DateTimeOffset.UtcNow);
        var values = await store.Db.HashGetAsync(Key, ["total_tokens", "prompt_tokens", "completion_tokens", "calls"]);
        var todayValues = await store.Db.HashGetAsync(daily, ["total_tokens"]);
        return (Num(values, 0), Num(todayValues, 0), Num(values, 1), Num(values, 2), Num(values, 3));
    }

    private static long Num(RedisValue[] values, int index) =>
        index < values.Length && long.TryParse(values[index].ToString(), out long n) ? n : 0;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long n) ? n : 0;
}
