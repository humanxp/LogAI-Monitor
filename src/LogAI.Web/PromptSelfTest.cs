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

        string prompt = PromptBuilder.BatchPrompt(summary);
        Check("prompt starts with the exact opening line",
            prompt.StartsWith("You are a syslog security/health analyzer. Assess the logs below.\n\nLOGS:\n", StringComparison.Ordinal),
            prompt[..Math.Min(80, prompt.Length)]);
        Check("prompt embeds the summary verbatim", prompt.Contains(expectedSummary, StringComparison.Ordinal));
        Check("prompt ends with the exact closing sentence",
            prompt.EndsWith("Base everything ONLY on the logs given. Keep the JSON compact. The reply MUST be one single complete valid JSON object - no markdown, no text before or after it, no truncation.", StringComparison.Ordinal),
            prompt[^60..]);
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
        Check("等级由重到轻排序",
            rankedLines[0].StartsWith("[CRITICAL]", StringComparison.Ordinal) &&
            rankedLines[1].StartsWith("[ERROR]", StringComparison.Ordinal) &&
            rankedLines[2].StartsWith("[WARNING]", StringComparison.Ordinal) &&
            rankedLines[3].StartsWith("[INFO]", StringComparison.Ordinal) &&
            rankedLines[4].StartsWith("[DEBUG]", StringComparison.Ordinal),
            ranked.Replace("\n", " | "));

        string limited = PromptBuilder.LogSummary(mixed, 2);
        var limitedLines = limited.Split('\n');
        Check("限制条数时只保留最严重的 N 行",
            limitedLines[0].StartsWith("[CRITICAL]", StringComparison.Ordinal) &&
            limitedLines[1].StartsWith("[ERROR]", StringComparison.Ordinal),
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
            PromptBuilder.LogSummary(sameLevel).Replace("\n", "|").Contains("first|[ERROR] [b]", StringComparison.Ordinal));

        Check("样本上限大于总数时不截断、不提示",
            !PromptBuilder.LogSummary(mixed, 99).Contains("omitted", StringComparison.Ordinal));
        Check("未知级别排在最后",
            PromptBuilder.LogSummary(new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["severity"] = "weird", ["program"] = "p", ["message"] = "x" },
                new Dictionary<string, string> { ["severity"] = "info", ["program"] = "p", ["message"] = "y" },
            }).StartsWith("[INFO]", StringComparison.Ordinal));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
