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
            "[ERROR] [GL-AXT1800] crond: USER root pid 4643 cmd get_arp_scan_ret\n" +
            "[WARNING] [10.10.10.7] kernel: disk 'sda1' is full";
        Check("log summary format", summary == expectedSummary, summary.Replace("\n", "\\n"));
        Check("hostname falls back to source", summary.Contains("[10.10.10.7]", StringComparison.Ordinal));
        Check("severity label is emitted (python-legacy log_summary format)",
            summary.Contains("[ERROR]", StringComparison.Ordinal) && summary.Contains("[WARNING]", StringComparison.Ordinal));

        string prompt = PromptBuilder.BatchPrompt(summary);
        // 与 python-legacy 版一致：简单提示 + 日志 + 6 个 JSON 键，无 few-shot、无缓存前缀。
        Check("prompt starts with the opening line",
            prompt.StartsWith("You are a syslog security/health analyzer.", StringComparison.Ordinal),
            prompt[..Math.Min(80, prompt.Length)]);
        Check("prompt embeds the summary verbatim", prompt.Contains(expectedSummary, StringComparison.Ordinal));
        Check("prompt carries every required key name",
            new[] { "overall_status", "issues_found", "critical_count", "recommendations", "affected_hosts", "alert_message" }
                .All(key => prompt.Contains('"' + key + '"', StringComparison.Ordinal)),
            "missing key");
        Check("no stray markdown fences in the template", !prompt.Contains("```", StringComparison.Ordinal));
        Check("no few-shot examples", !prompt.Contains("Example 1", StringComparison.Ordinal));

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
            PromptBuilder.LogSummary(sameLevel).StartsWith("[ERROR] [a] p: first", StringComparison.Ordinal)
            && PromptBuilder.LogSummary(sameLevel).EndsWith("[b] p: second", StringComparison.Ordinal));

        // 不去重（python-legacy 版行为）：逐条输出原始行。
        Check("lines are NOT deduped (python-legacy)",
            PromptBuilder.LogSummary(new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["severity"] = "error", ["hostname"] = "h", ["program"] = "crond", ["message"] = "USER root pid 100 cmd wg-watchdog" },
                new Dictionary<string, string> { ["severity"] = "error", ["hostname"] = "h", ["program"] = "crond", ["message"] = "USER root pid 200 cmd wg-watchdog" },
            }).Split('\n').Length == 2, "pid 不同也应各占一行");

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

        // ---- 提示词模式 → 模板（「建议使用模型匹配优化」的后端一半）----
        // 前端 syncPromptModeToModel() 负责把模型名映射成模式，后端 BatchPromptFor 负责把模式
        // 映射成模板。这里把后端这一半钉死，尤其是"未知值必须回退到通用缓存版"——
        // 老库里可能存着 ai_prompt_mode 的任意历史值，升级后提示词不能悄悄变样。
        string q36 = PromptBuilder.BatchPromptFor("qwen36", summary);
        Check("qwen36 取到自己的模板（含输出精简段与问题/噪声边界，不含安全段）",
            q36.Contains("BREVITY (this monitor runs every 2 minutes", StringComparison.Ordinal)
            && q36.Contains("PROBLEM vs NOISE - WHAT COUNTS AS AN ISSUE", StringComparison.Ordinal)
            && !q36.Contains("SECURITY EVIDENCE IS LITERAL", StringComparison.Ordinal));
        Check("qwen36 模板与通用缓存版不同（说明它确实被优化过）",
            q36 != PromptBuilder.BatchPromptCached(summary));

        string q35 = PromptBuilder.BatchPromptFor("qwen35", summary);
        Check("qwen35 与 qwen25 用同一份短模板（长模板实测对 9B 是负收益，已删）",
            q35 == PromptBuilder.BatchPromptQwen25(summary));
        Check("qwen35 短模板仍带两条安全判据",
            q35.Contains("A successful remote login (\"Accepted password\"", StringComparison.Ordinal)
            && q35.Contains("from an antivirus is confirmed malware", StringComparison.Ordinal));

        string gemma = PromptBuilder.BatchPromptFor("gemma", summary);
        Check("gemma 短模板带两条安全判据",
            gemma.Contains("A successful remote login (\"Accepted password\"", StringComparison.Ordinal)
            && gemma.Contains("from an antivirus is confirmed malware", StringComparison.Ordinal));
        Check("gemma 比 qwen25 多一条爆破强调（其余部分与短模板一致）",
            gemma.Contains("an ACTIVE brute-force attack", StringComparison.Ordinal)
            && !PromptBuilder.BatchPromptQwen25(summary).Contains("an ACTIVE brute-force attack", StringComparison.Ordinal));

        string q25 = PromptBuilder.BatchPromptFor("qwen25", summary);
        Check("qwen25 取到自己的模板（只有最短的安全判据，没有长段/示例）",
            q25.Contains("A successful remote login (\"Accepted password\"", StringComparison.Ordinal)
            && q25.Contains("from an antivirus is confirmed malware", StringComparison.Ordinal)
            && !q25.Contains("PROBLEM vs NOISE", StringComparison.Ordinal)
            && !q25.Contains("Example 5", StringComparison.Ordinal));

        Check("default 与空串取简单版（Llama3.2 行为不变）",
            PromptBuilder.BatchPromptFor("default", summary) == PromptBuilder.BatchPrompt(summary)
            && PromptBuilder.BatchPromptFor("", summary) == PromptBuilder.BatchPrompt(summary));

        Check("未知模式回退到通用缓存版（老配置升级不变样）",
            PromptBuilder.BatchPromptFor("qwen3.5-legacy", summary) == PromptBuilder.BatchPromptCached(summary));

        Check("模式名大小写与引号都容错",
            PromptBuilder.BatchPromptFor("\"QWEN25\"", summary) == PromptBuilder.BatchPromptQwen25(summary));

        Check("每个模式都必须带上 6 个键",
            new[] { q36, q35, q25, gemma }.All(p =>
                new[] { "overall_status", "issues_found", "critical_count", "recommendations", "affected_hosts", "alert_message" }
                    .All(key => p.Contains('"' + key + '"', StringComparison.Ordinal))));

        Check("每个模式都必须嵌入日志正文",
            new[] { q36, q35, q25, gemma }.All(p => p.Contains(expectedSummary, StringComparison.Ordinal)));

        Check("qwen25/qwen35 同源、gemma 多一条、qwen36 与 default 各自独立（共 4 份）",
            q35 == q25
            && new HashSet<string> { q36, q25, gemma, PromptBuilder.BatchPromptFor("default", summary) }.Count == 4);

        // 兼容层：ai_cache_optimized=true（旧键）→ qwen35；缺省 → default
        Check("旧键 ai_cache_optimized=true 映射到 qwen35",
            LogAI.Core.Ai.AiClient.AiPromptModeIn(new Dictionary<string, string> { ["ai_cache_optimized"] = "true" }) == "qwen35");
        Check("两个键都缺省时映射到 default",
            LogAI.Core.Ai.AiClient.AiPromptModeIn(new Dictionary<string, string>()) == "default");
        Check("ai_prompt_mode 优先于旧键",
            LogAI.Core.Ai.AiClient.AiPromptModeIn(new Dictionary<string, string>
            { ["ai_prompt_mode"] = "qwen25", ["ai_cache_optimized"] = "true" }) == "qwen25");

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
