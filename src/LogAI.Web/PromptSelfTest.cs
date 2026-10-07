// Checks the prompt template verbatim and the sanitiser against the input that
// actually broke parsing in production.

using LogAI.Core.Ai;

namespace LogAI.Web;

internal static class PromptSelfTest
{
    private static int _failures;

    public static int Run()
    {
        // Sanitiser: the production failure case.
        Check("quotes become apostrophes",
            PromptBuilder.Safe("disk \"sda1\" is full") == "disk 'sda1' is full",
            PromptBuilder.Safe("disk \"sda1\" is full"));
        Check("backslashes become slashes",
            PromptBuilder.Safe(@"C:\path\to\file") == "C:/path/to/file",
            PromptBuilder.Safe(@"C:\path\to\file"));
        Check("newlines and tabs become spaces",
            PromptBuilder.Safe("a\nb\tc\rd") == "a b c d", PromptBuilder.Safe("a\nb\tc\rd"));
        Check("null becomes empty", PromptBuilder.Safe(null) == "", "null");
        Check("truncated to the limit", PromptBuilder.Safe(new string('x', 300), 200).Length == 200);
        Check("whitespace trimmed", PromptBuilder.Safe("  padded  ") == "padded");

        var logs = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["severity"] = "error", ["hostname"] = "GL-AXT1800",
                ["program"] = "crond", ["message"] = "USER root pid 4643 cmd get_arp_scan_ret",
            },
            new Dictionary<string, string>
            {
                ["severity"] = "warning", ["source"] = "10.10.10.7",
                ["program"] = "kernel", ["message"] = "disk \"sda1\" is full",
            },
        };

        string summary = PromptBuilder.LogSummary(logs);
        string expectedSummary =
            "[GL-AXT1800] crond: USER root pid 4643 cmd get_arp_scan_ret\n" +
            "[10.10.10.7] kernel: disk 'sda1' is full";
        Check("log summary format", summary == expectedSummary, summary.Replace("\n", "\\n"));
        Check("hostname falls back to source", summary.Contains("[10.10.10.7]", StringComparison.Ordinal));
        Check("severity label is NOT emitted (device labels are unreliable; see LogSummary doc)",
            !summary.Contains("[ERROR]", StringComparison.Ordinal) && !summary.Contains("[CRITICAL]", StringComparison.Ordinal));

        string prompt = PromptBuilder.BatchPrompt(summary);
        // 结构是产品决策、不是风格，两个方向都实测过：
        //   · 指令在末尾、无示例      → healthy 15 / warning 10，缓存 0%
        //   · 指令在前、无示例        → warning 13 / healthy 2（把健康批次误判），缓存 6.9%
        //   · 指令在前 + few-shot 示例 → healthy 25/25、故障批次 critical 25/25、
        //     warning 批次 warning 15/15，缓存 22.8%
        // 示例把"设备标了 error 但其实是例行噪音 → healthy"锚住，于是既能吃到
        // 前缀缓存（静态块 966 token → 稳定命中 768）又不牺牲判定。改动这段前请重跑
        // 上述三组 A/B。
        Check("prompt starts with the opening line",
            prompt.StartsWith("You are a syslog security/health analyzer.", StringComparison.Ordinal),
            prompt[..Math.Min(80, prompt.Length)]);
        Check("static rules + examples come BEFORE the logs (cacheable prefix)",
            prompt.IndexOf("REPLY FORMAT", StringComparison.Ordinal) <
            prompt.IndexOf("NOW ANALYZE THIS BATCH.", StringComparison.Ordinal),
            "static block must precede the logs");
        Check("logs come last",
            prompt.EndsWith(expectedSummary, StringComparison.Ordinal),
            prompt[^60..]);
        Check("three few-shot examples present (they anchor the routine-chatter case)",
            prompt.Contains("Example 1 - routine chatter", StringComparison.Ordinal)
            && prompt.Contains("Example 2 - a service is down", StringComparison.Ordinal)
            && prompt.Contains("Example 3 - a real problem", StringComparison.Ordinal));
        Check("prompt embeds the summary verbatim", prompt.Contains(expectedSummary, StringComparison.Ordinal));
        Check("prompt carries every required key name",
            new[] { "overall_status", "issues_found", "critical_count", "recommendations", "affected_hosts", "alert_message" }
                .All(key => prompt.Contains('"' + key + '"', StringComparison.Ordinal)),
            "missing key");
        Check("no stray markdown fences in the template", !prompt.Contains("```", StringComparison.Ordinal));

        string corrective = PromptBuilder.CorrectivePrompt(prompt);
        Check("corrective prompt keeps the original", corrective.StartsWith(prompt, StringComparison.Ordinal));
        Check("corrective prompt adds the retry instruction",
            corrective.Contains("Your previous reply was NOT a single valid JSON object", StringComparison.Ordinal)
            && corrective.EndsWith("Every string must be proper JSON.", StringComparison.Ordinal));

        // ---- 级别优先与样本上限 ----
        // 设置页承诺"每批按级别排序后取 N 行送给模型"。这几条断言把该承诺钉住：
        // 曾经这里既不排序也不限行，整批日志会全部灌进提示词。
        var mixed = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["severity"] = "info",    ["hostname"] = "h1", ["program"] = "p", ["message"] = "info-1" },
            new Dictionary<string, string> { ["severity"] = "debug",   ["hostname"] = "h2", ["program"] = "p", ["message"] = "debug-1" },
            new Dictionary<string, string> { ["severity"] = "critical",["hostname"] = "h3", ["program"] = "p", ["message"] = "crit-1" },
            new Dictionary<string, string> { ["severity"] = "error",   ["hostname"] = "h4", ["program"] = "p", ["message"] = "err-1" },
            new Dictionary<string, string> { ["severity"] = "warning", ["hostname"] = "h5", ["program"] = "p", ["message"] = "warn-1" },
        };

        string ranked = PromptBuilder.LogSummary(mixed);
        var rankedLines = ranked.Split('\n');
        Check("等级由重到轻排序（看消息内容，级别标签已去掉）",
            rankedLines[0].EndsWith("p: crit-1", StringComparison.Ordinal) &&
            rankedLines[1].EndsWith("p: err-1", StringComparison.Ordinal) &&
            rankedLines[2].EndsWith("p: warn-1", StringComparison.Ordinal) &&
            rankedLines[3].EndsWith("p: info-1", StringComparison.Ordinal) &&
            rankedLines[4].EndsWith("p: debug-1", StringComparison.Ordinal),
            ranked.Replace("\n", " | "));

        string limited = PromptBuilder.LogSummary(mixed, 2);
        var limitedLines = limited.Split('\n');
        Check("限制条数时只保留最严重的 N 行",
            limitedLines[0].EndsWith("p: crit-1", StringComparison.Ordinal) &&
            limitedLines[1].EndsWith("p: err-1", StringComparison.Ordinal),
            limited.Replace("\n", " | "));
        Check("被截断时明确告知模型剩余条数",
            limited.Contains("3 more log(s) omitted", StringComparison.Ordinal), limited);

        // 同级必须保持原有先后（稳定排序），否则同一批次的提示词会抖动。
        var sameLevel = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["severity"] = "error", ["hostname"] = "a", ["program"] = "p", ["message"] = "first" },
            new Dictionary<string, string> { ["severity"] = "error", ["hostname"] = "b", ["program"] = "p", ["message"] = "second" },
        };
        Check("同级保持到达顺序",
            PromptBuilder.LogSummary(sameLevel).Replace("\n", "|").Contains("first|[b] p: second", StringComparison.Ordinal));

        Check("样本上限大于总数时不截断、不提示",
            !PromptBuilder.LogSummary(mixed, 99).Contains("omitted", StringComparison.Ordinal));
        Check("未知级别排在最后",
            PromptBuilder.LogSummary(new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["severity"] = "weird", ["program"] = "p", ["message"] = "x" },
                new Dictionary<string, string> { ["severity"] = "info", ["program"] = "p", ["message"] = "y" },
            }).EndsWith("p: x", StringComparison.Ordinal));

        // ---- "启用 AI 分析"总开关的判定 ----
        // 这个键此前后端从不读取，勾掉它完全无效。判定语义要有断言钉住，
        // 否则以后很容易又退化成"设置页是个摆设"。
        Check("缺省视为启用",
            LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string>()));
        Check("空串视为启用",
            LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "" }));
        Check("true 视为启用",
            LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "true" }));
        Check("false 视为停用",
            !LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "false" }));
        Check("JSON 编码的 false 也视为停用（存储里是带引号的）",
            !LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "\"false\"" }));
        Check("0/no/off 也视为停用",
            !LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "0" }) &&
            !LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "no" }) &&
            !LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "off" }));
        Check("TRUE 大写视为启用",
            LogAI.Core.Ai.AiClient.AiEnabledIn(new Dictionary<string, string> { ["ollama_enabled"] = "TRUE" }));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
