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

        // 6) 不再去重：保留模型原始输出（重复的 issues/建议原样保留，critical_count 不改）。
        string dup = """{"overall_status":"warning","issues_found":["a","a","a"],"recommendations":["r","r"],"critical_count":5}""";
        Check("raw output kept (no dedup)", JsonExtractor.Extract(dup),
            n => n["issues_found"] is JsonArray { Count: 3 }
                 && n["recommendations"] is JsonArray { Count: 2 }
                 && n["critical_count"]?.GetValue<int>() == 5);

        // 7) 有 issues 但 recommendations 空：HasRequiredFields 判不自洽（触发重试让模型补）。
        //    100% 纯模型：程序不再塞模板建议，所以这里只校验"判不自洽"。
        string noRec = """{"overall_status":"warning","issues_found":["a"],"recommendations":[],"critical_count":0}""";
        Check("issues without recommendations is incomplete",
            System.Text.Json.Nodes.JsonNode.Parse(noRec),
            n => !JsonExtractor.HasRequiredFields(n));

        // 8) EnsureHostPrefix：只在文本里显式出现主机时才补前缀；已有可信前缀保留；不猜主机。
        var hostObj = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """{"overall_status":"warning","issues_found":["磁盘满","[db01] 服务宕机"],"recommendations":["清理磁盘"],"critical_count":0}""")!;
        JsonExtractor.EnsureHostPrefix(hostObj, new List<string> { "db01" });
        Check("host prefix only added when text mentions it (no guessing)",
            hostObj,
            n => n["issues_found"]?[0]?.ToString() == "磁盘满"
                 && n["issues_found"]?[1]?.ToString() == "[db01] 服务宕机"
                 && n["recommendations"]?[0]?.ToString() == "清理磁盘");

        // 9) 显式主机：建议文本里写了"on 192.168.50.15"，前缀必须是它（不猜主机）。
        var hostObj2 = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """{"overall_status":"critical","affected_hosts":["192.168.50.3","192.168.50.15"],"issues_found":["[192.168.50.3] 192.168.50.3: Injector: Sleeping!"],"recommendations":["Check for duplicate VM registrations on 192.168.50.15"],"critical_count":1}""")!;
        JsonExtractor.EnsureHostPrefix(hostObj2, new List<string> { "192.168.50.3" });
        Check("explicit host in recommendation text wins",
            hostObj2,
            n => n["recommendations"]?[0]?.ToString() == "[192.168.50.15] Check for duplicate VM registrations on 192.168.50.15");

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
