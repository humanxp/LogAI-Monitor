// Self-test for the model-reply JSON extractor, using the failure shapes this
// deployment actually produced.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;

namespace LogAI.Web;

internal static class JsonSelfTest
{
    private static int _failures;

    public static int Run()
    {
        // 1) clean object
        Check("plain object", JsonExtractor.Extract("""{"overall_status":"warning","critical_count":3}"""),
            n => n["overall_status"]?.GetValue<string>() == "warning" && n["critical_count"]?.GetValue<int>() == 3);

        // 2) fenced, with prose around it
        Check("fenced with prose",
            JsonExtractor.Extract("Sure! Here is the analysis:\n```json\n{\"overall_status\":\"critical\",\"critical_count\":8}\n```\nLet me know."),
            n => n["overall_status"]?.GetValue<string>() == "critical");

        // 3) truncated mid-token, the 2048-token case
        string truncated = """{"overall_status":"warning","critical_count":8,"issues_found":["host down","disk almost full"],"recommendations":["check the disk","restart the serv""";
        Check("truncated reply repaired", JsonExtractor.Extract(truncated),
            n => n["overall_status"]?.GetValue<string>() == "warning"
                 && n["issues_found"] is JsonArray { Count: 2 }
                 && n["recommendations"] is JsonArray);

        // 4) the invalid array-pair form that broke whole batches
        string pairs = """{"overall_status":"critical","issues_found":["GL-AXT1800":"crond running twice","XiaoBao":"BTRFS csum failed"],"critical_count":2}""";
        Check("array pairs normalised", JsonExtractor.Extract(pairs),
            n => n["issues_found"] is JsonArray { Count: 2 } list
                 && list[0] is JsonObject first
                 && first["GL-AXT1800"]?.GetValue<string>() == "crond running twice");

        // 5) nothing usable
        Check("garbage returns null", JsonExtractor.Extract("I cannot analyse this."), n => n is null);

        // 6) 去重开关：默认开时重复的 issues/建议折叠 + critical_count 封顶；关时保留原样。
        string dup = """{"overall_status":"warning","issues_found":["a","a","a"],"recommendations":["r","r"],"critical_count":5}""";
        Check("dedup on collapses duplicates and caps critical_count", JsonExtractor.Extract(dup),
            n => n["issues_found"] is JsonArray { Count: 1 }
                 && n["recommendations"] is JsonArray { Count: 1 }
                 && n["critical_count"]?.GetValue<int>() == 1);
        Check("dedup off keeps the raw output", JsonExtractor.Extract(dup, dedup: false),
            n => n["issues_found"] is JsonArray { Count: 3 } && n["critical_count"]?.GetValue<int>() == 5);

        // 7) 有 issues 但 recommendations 空：HasRequiredFields 判不自洽（触发重试让模型补）。
        //    建议一律由模型生成，程序不再塞模板建议，所以这里只校验"判不自洽"。
        string noRec = """{"overall_status":"warning","issues_found":["a"],"recommendations":[],"critical_count":0}""";
        Check("issues without recommendations is incomplete",
            System.Text.Json.Nodes.JsonNode.Parse(noRec),
            n => !JsonExtractor.HasRequiredFields(n));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, JsonNode? node, Func<JsonNode?, bool> assertion)
    {
        bool ok;
        string detail = "";
        try
        {
            ok = assertion(node);
            if (!ok) detail = node?.ToJsonString()?[..Math.Min(120, node.ToJsonString().Length)] ?? "null";
        }
        catch (Exception ex)
        {
            ok = false;
            detail = ex.GetType().Name + ": " + ex.Message;
        }

        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}   {detail}"); }
    }
}
