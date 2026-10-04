// End-to-end analysis cycle against the configured model, on database 9 only.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class RunnerSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0) { Console.Error.WriteLine("refusing database 0"); return 2; }

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        };
        using var store = new RedisStore(options);
        await store.Db.ExecuteAsync("FLUSHDB");

        // Three logs with a real problem in them.
        (string Id, string Host, string Severity, string Message)[] samples =
        [
            ("log:001", "XiaoBao", "error", "BTRFS error (device mmcblk0p2): bdev /dev/mmcblk0p2 errs: wr 0, rd 0, flush 0, corrupt 6518, gen 0"),
            ("log:002", "XiaoBao", "warning", "BTRFS warning (device mmcblk0p2): csum failed root 5 ino 19596 off 737280"),
            ("log:003", "GL-AXT1800", "info", "crond[4861]: USER root pid 4643 cmd /lib/functions/mptun.sh"),
        ];
        foreach (var sample in samples)
        {
            await store.Db.HashSetAsync(sample.Id,
            [
                new HashEntry("message", sample.Message),
                new HashEntry("hostname", sample.Host),
                new HashEntry("severity", sample.Severity),
                new HashEntry("program", "kernel"),
                new HashEntry("analyzed", "false"),
            ]);
            await store.Db.SortedSetAddAsync(Keys.Unanalyzed, sample.Id, 1);
            await store.Db.SortedSetAddAsync(Keys.Timeline, sample.Id, 1);
        }

        // Settings live in database 0 while the test data lives in the isolated
        // one, so the configuration needs its own connection.
        using var configStore = new RedisStore(new RedisOptions
        {
            Host = options.Host, Port = options.Port, Database = 0,
        });
        var settings = await configStore.GetSettingsAsync();
        string baseUrl = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
        string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
        if (provider.Length == 0) provider = "openai";

        var client = new AiClient
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "Llama-3.2-3B-Instruct-4bit",
            ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
        };

        var runner = new AnalysisRunner(store, client, new AiHistoryWriter(store), fallbackBatchSize: 50);
        var started = DateTime.UtcNow;
        // AI 不可达时 CompleteAsync 会抛异常。自测必须把它变成"一条失败断言 + 结论行"，
        // 而不是让进程带着未捕获异常崩溃：崩溃时没有 verdict，调用方只能看到 exit=134，
        // 分不清"功能坏了"和"环境没配好"（本次就是这么被绊了一下）。
        AnalysisRunner.Outcome outcome;
        try
        {
            outcome = await runner.RunOnceAsync();
        }
        catch (Exception ex)
        {
            Check("cycle analysed the batch", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine($"\nFAILED ({_failures} failures)");
            return 1;
        }
        Console.WriteLine($"cycle: {outcome.Status}, count={outcome.Count}, {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
        if (outcome.Error is not null) Console.WriteLine($"error: {outcome.Error}");

        Check("cycle analysed the batch", outcome.Status == "analyzed" && outcome.Count == 3, outcome.Status);
        Check("queue drained", await store.Db.SortedSetLengthAsync(Keys.Unanalyzed) == 0,
            (await store.Db.SortedSetLengthAsync(Keys.Unanalyzed)).ToString());
        Check("a history record was written",
            await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline) == 1,
            (await store.Db.SortedSetLengthAsync(Keys.AiHistoryTimeline)).ToString());

        if (outcome.HistoryId is not null)
        {
            var record = await store.Db.HashGetAllAsync(outcome.HistoryId);
            var fields = record.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
            var ids = JsonNode.Parse(fields.GetValueOrDefault("log_ids") ?? "[]") as JsonArray;
            Check("history covers the batch", ids is { Count: 3 }, fields.GetValueOrDefault("log_ids") ?? "");
            Check("history carries a real analysis",
                JsonNode.Parse(fields.GetValueOrDefault("analysis") ?? "null")?["overall_status"] is not null,
                fields.GetValueOrDefault("analysis")?[..Math.Min(90, fields.GetValueOrDefault("analysis")!.Length)] ?? "");
            Console.WriteLine($"analysis: {fields.GetValueOrDefault("analysis")?[..Math.Min(150, fields.GetValueOrDefault("analysis")!.Length)]}");
        }

        int marked = 0;
        foreach (var sample in samples)
            if ((await store.Db.HashGetAsync(sample.Id, "analyzed")) == "true"
                && (await store.Db.HashGetAsync(sample.Id, "analysis")).HasValue) marked++;
        Check("every log marked analysed with a copy", marked == 3, marked.ToString());

        // Second cycle over an empty queue must be a no-op.
        var again = await runner.RunOnceAsync();
        Check("empty queue is a no-op", again.Status == "empty", again.Status);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
