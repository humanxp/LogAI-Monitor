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

        string[] expected = ["analysis", "fail_count", "id", "log_ids", "logs_analyzed", "timestamp", "type"];
        Check("stored record has exactly the seven fields",
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

        // The API serves five of these seven fields; the extra two must stay internal.
        string[] served = ["analysis", "id", "logs_analyzed", "timestamp", "type"];
        Check("the two internal fields are exactly fail_count and log_ids",
            expected.Except(served, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(["fail_count", "log_ids"]),
            string.Join(",", expected.Except(served, StringComparer.Ordinal)));

        Check("analysis indexed on the timeline",
            await store.Db.SortedSetScoreAsync(Keys.AiHistoryTimeline, id) is not null);

        // Retirement rule for dead batches.
        Check("retire after three failures",
            !AiHistoryWriter.ShouldRetire(0) && !AiHistoryWriter.ShouldRetire(2)
            && AiHistoryWriter.ShouldRetire(3) && AiHistoryWriter.ShouldRetire(4));
        Check("retirement threshold is three", AiHistoryWriter.MaxFailedRetries == 3);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
