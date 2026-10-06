// Self-test for filter matching, using the expectations verified earlier
// against a running receiver (three UDP samples produced three
// alerts, with exactly one Telegram cooldown key).

using LogAI.Core.Filters;

namespace LogAI.Web;

internal static class FilterSelfTest
{
    private static int _failures;

    public static int Run()
    {
        // The production filter that protects the disk/VMware rules.
        var disk = new FilterRule
        {
            Id = "filter:disk",
            Name = "磁盘/文件系统异常",
            NotifyTelegram = true,
            Severity = [],
            MessageRegex = "(buffer i/o error|blk_update_request|failed:?\\s*h:0x|all paths down)",
        };

        Check("regex hit (buffer i/o error)",
            FilterMatcher.Matches(disk, "10.0.0.1", "info", "Buffer I/O error on dev sda1, logical block 4096"));
        Check("regex hit (case-insensitive)",
            FilterMatcher.Matches(disk, "10.0.0.1", "error", "BLK_UPDATE_REQUEST: I/O error, dev sda"));
        Check("regex miss", !FilterMatcher.Matches(disk, "10.0.0.1", "info", "everything is fine"));

        var warnOnly = new FilterRule { Severity = ["warning", "error"], MessageContains = "out of memory" };
        Check("severity + substring both required",
            FilterMatcher.Matches(warnOnly, "10.0.0.1", "error", "Out of memory: killed process"));
        Check("severity miss blocks", !FilterMatcher.Matches(warnOnly, "10.0.0.1", "info", "Out of memory: killed"));
        Check("substring miss blocks", !FilterMatcher.Matches(warnOnly, "10.0.0.1", "error", "nothing here"));

        var bySource = new FilterRule { SourceContains = "192.168.50." };
        Check("source substring", FilterMatcher.Matches(bySource, "192.168.50.7", "info", "x"));
        Check("source substring miss", !FilterMatcher.Matches(bySource, "10.10.10.7", "info", "x"));

        Check("disabled rule never matches",
            !FilterMatcher.Matches(new FilterRule { Enabled = false, MessageRegex = "." }, "a", "info", "x"));

        Check("invalid regex matches nothing, does not throw",
            !FilterMatcher.Matches(new FilterRule { MessageRegex = "([unclosed" }, "a", "info", "x"));

        // Telegram gate：门限是"最低推送级别"的 syslog 编码（3=error 默认）。
        var rule = new FilterRule { NotifyTelegram = true };
        const int ErrorGate = 3, CriticalGate = 2, WarningGate = 4;
        Check("gate blocks info at error level", !FilterMatcher.TelegramAllowed(rule, "info", ErrorGate));
        Check("gate blocks warning at error level", !FilterMatcher.TelegramAllowed(rule, "warning", ErrorGate));
        Check("gate allows error", FilterMatcher.TelegramAllowed(rule, "error", ErrorGate));
        Check("gate allows critical", FilterMatcher.TelegramAllowed(rule, "critical", ErrorGate));
        Check("critical-only gate blocks error", !FilterMatcher.TelegramAllowed(rule, "error", CriticalGate));
        Check("warning gate allows warning", FilterMatcher.TelegramAllowed(rule, "warning", WarningGate));
        Check("warning gate blocks info", !FilterMatcher.TelegramAllowed(rule, "info", WarningGate));
        Check("unknown severity never pushes", !FilterMatcher.TelegramAllowed(rule, "bogus", WarningGate));
        Check("min severity name maps to code",
            FilterMatcher.MinSeverityCode("critical") == 2 && FilterMatcher.MinSeverityCode("warning") == 4
            && FilterMatcher.MinSeverityCode("garbage") == 3);
        Check("per-rule bypass ignores the gate",
            FilterMatcher.TelegramAllowed(new FilterRule { NotifyTelegram = true, NotifyAnySeverity = true },
                                          "info", CriticalGate));
        Check("notify_telegram=false never pushes", !FilterMatcher.TelegramAllowed(new FilterRule(), "critical", ErrorGate));

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}"); }
    }
}
