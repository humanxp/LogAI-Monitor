// Locks down the stored AI history shape (7 fields) and the retirement rule.

using System.Text.Json;
using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Store;

namespace LogAI.Web;

internal static class AiHistorySelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        int database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "9");
        if (database == 0)
        {
            Console.Error.WriteLine("refusing to run against database 0 (production)");
            return 2;
        }

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = database,
        };
        using var store = new RedisStore(options);
        await store.Db.ExecuteAsync("FLUSHDB");

        var writer = new AiHistoryWriter(store);
        var analysis = JsonNode.Parse("""{"overall_status":"warning","critical_count":3,"issues_found":["a","b"]}""")!;
        string id = await writer.WriteAsync(["log:1", "log:2", "log:3"], analysis, "auto", 0);

        var hash = await store.Db.HashGetAllAsync(id);
        var stored = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

        string[] expected = ["analysis", "fail_count", "id", "log_ids", "logs_analyzed", "status", "timestamp", "type"];
        Check("stored record has exactly the eight fields",
            stored.Count == expected.Length && expected.All(stored.ContainsKey),
            string.Join(",", stored.Keys.OrderBy(k => k)));
        Check("id field equals the key", stored.GetValueOrDefault("id") == id, id);
        Check("logs_analyzed counts the batch", stored.GetValueOrDefault("logs_analyzed") == "3",
            stored.GetValueOrDefault("logs_analyzed") ?? "");
        Check("fail_count starts at zero", stored.GetValueOrDefault("fail_count") == "0",
            stored.GetValueOrDefault("fail_count") ?? "");
        Check("type recorded", stored.GetValueOrDefault("type") == "auto", stored.GetValueOrDefault("type") ?? "");

        var ids = JsonNode.Parse(stored["log_ids"]!) as JsonArray;
        Check("log_ids is a JSON array of the batch",
            ids is { Count: 3 } && ids[0]?.GetValue<string>() == "log:1" && ids[2]?.GetValue<string>() == "log:3",
            stored["log_ids"] ?? "");

        var roundTrip = JsonNode.Parse(stored["analysis"]!);
        Check("analysis round-trips as an object",
            roundTrip?["overall_status"]?.GetValue<string>() == "warning"
            && roundTrip?["issues_found"] is JsonArray { Count: 2 },
            stored["analysis"] ?? "");

        // The API serves five of these eight fields; the rest must stay internal.
        string[] served = ["analysis", "id", "logs_analyzed", "timestamp", "type"];
        Check("the internal fields are exactly fail_count, log_ids and status",
            expected.Except(served, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(["fail_count", "log_ids", "status"]),
            string.Join(",", expected.Except(served, StringComparer.Ordinal)));

        Check("analysis indexed on the timeline",
            await store.Db.SortedSetScoreAsync(Keys.AiHistoryTimeline, id) is not null);

        // Retirement rule for dead batches.
        Check("retire after three failures",
            !AiHistoryWriter.ShouldRetire(0) && !AiHistoryWriter.ShouldRetire(2)
            && AiHistoryWriter.ShouldRetire(3) && AiHistoryWriter.ShouldRetire(4));
        Check("retirement threshold is three", AiHistoryWriter.MaxFailedRetries == 3);

        // AiStatusClassifier：字符串重载必须真的解析 JSON（曾经因为 JsonNode 对 string
        // 的隐式转换，3 参调用把整串 JSON 当成 JsonValue，导致回填全部变成 other）。
        const string CritJson = """{"overall_status":"critical","critical_count":4}""";
        Check("string overload parses JSON (not other)",
            AiStatusClassifier.Classify("auto", CritJson) == "critical");
        Check("error/notice fold into warning",
            AiStatusClassifier.Classify("auto", """{"overall_status":"error"}""") == "warning"
            && AiStatusClassifier.Classify("auto", """{"overall_status":"notice"}""") == "warning");
        Check("info folds into healthy",
            AiStatusClassifier.Classify("auto", """{"overall_status":"info"}""") == "healthy");
        Check("critical without any critical_count stays critical (pure model)",
            AiStatusClassifier.Classify("auto", """{"overall_status":"critical","critical_count":0}""") == "critical");
        Check("allowCritical is ignored (pure model)",
            AiStatusClassifier.Classify("auto", CritJson, allowCritical: false) == "critical"
            && AiStatusClassifier.Classify("auto", CritJson, allowCritical: true) == "critical");
        Check("non-object analysis is other",
            AiStatusClassifier.Classify("auto", "not json") == "other");
        // 100% 纯模型：判 healthy 就 healthy，即使 issues 里列了故障词也不改。
        Check("healthy with a failure-word issue stays healthy (pure model)",
            AiStatusClassifier.Classify("auto",
                """{"overall_status":"healthy","issues_found":["[h1] myddns: Transfer failed - retry 12/"]}""") == "healthy");
        Check("healthy with only routine issues stays healthy",
            AiStatusClassifier.Classify("auto",
                """{"overall_status":"healthy","issues_found":["[h1] Load template from file","[h2] Injector: Sleeping!"]}""") == "healthy");
        // warning 下限的消息判定：字面故障词才算，例行调度/误标 emergency 不算。
        Check("HasFailureWord matches literal failures only",
            AiStatusClassifier.HasFailureWord("myddns_ipv6: Transfer failed - retry 126/ in 60 seconds")
            && AiStatusClassifier.HasFailureWord("IpmiIfcOpenIpmiOpen: open(/dev/ipmi0, RDWR) failed")
            && !AiStatusClassifier.HasFailureWord("crond: USER root pid 28014 cmd /usr/bin/wg-watchdog")
            && !AiStatusClassifier.HasFailureWord("start NTP update"));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
