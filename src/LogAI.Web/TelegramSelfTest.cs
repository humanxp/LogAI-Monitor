// Compares the composed message with the template written out literally,
// so a change in spacing or markup cannot slip through unnoticed.

using LogAI.Core.Notify;

namespace LogAI.Web;

internal static class TelegramSelfTest
{
    private static int _failures;

    public static int Run()
    {
        // Expected text for severity=error, source=10.10.10.7, hostname=GL-AXT1800.
        string expected =
            "\n" +
            "🟠 <b>LogAI Monitor Alert</b>\n" +
            "\n" +
            "<b>Severity:</b> ERROR\n" +
            "<b>Host:</b> GL-AXT1800\n" +
            "<b>Source:</b> 10.10.10.7\n" +
            "<b>Message:</b>\n" +
            "<code>buffer I/O error on dev sda1</code>\n";

        string actual = TelegramNotifier.BuildAlertText(
            "error", "10.10.10.7", "buffer I/O error on dev sda1", "GL-AXT1800");
        Check("alert text matches the template", actual == expected, Show(actual));

        // Hostname falls back to the source when absent.
        string noHost = TelegramNotifier.BuildAlertText("info", "192.168.50.37", "hello");
        Check("host falls back to source", noHost.Contains("<b>Host:</b> 192.168.50.37", StringComparison.Ordinal));
        Check("severity is upper-cased", noHost.Contains("<b>Severity:</b> INFO", StringComparison.Ordinal));

        // HTML escaping, matching html.escape(quote=True).
        string escaped = TelegramNotifier.BuildAlertText("warning", "h1", "a<b>&c\"d'e");
        Check("html escape applied inside <code>",
            escaped.Contains("<code>a&lt;b&gt;&amp;c&quot;d&#x27;e</code>", StringComparison.Ordinal), Show(escaped));

        // Truncation caps.
        string longMessage = new string('m', 600);
        Check("message capped at 500",
            TelegramNotifier.BuildAlertText("error", "s", longMessage).Contains(new string('m', 500), StringComparison.Ordinal)
            && !TelegramNotifier.BuildAlertText("error", "s", longMessage).Contains(new string('m', 501), StringComparison.Ordinal));
        Check("analysis capped at 300",
            TelegramNotifier.BuildAlertText("error", "s", "m", "h", new string('a', 400))
                .EndsWith(new string('a', 300), StringComparison.Ordinal));

        // Optional analysis section.
        string withAnalysis = TelegramNotifier.BuildAlertText("critical", "s", "m", "h", "check the disk");
        Check("analysis section appended with a blank line",
            withAnalysis.Contains("\n<b>AI Analysis:</b>\ncheck the disk", StringComparison.Ordinal), Show(withAnalysis));
        Check("no analysis section when absent",
            !TelegramNotifier.BuildAlertText("critical", "s", "m", "h").Contains("AI Analysis", StringComparison.Ordinal));

        // Emoji table.
        Check("emoji mapping",
            TelegramNotifier.Emoji("critical") == "🔴" && TelegramNotifier.Emoji("error") == "🟠"
            && TelegramNotifier.Emoji("warning") == "🟡" && TelegramNotifier.Emoji("notice") == "🔵"
            && TelegramNotifier.Emoji("info") == "⚪" && TelegramNotifier.Emoji("debug") == "⚫"
            && TelegramNotifier.Emoji("unknown") == "⚪");

        // Summary template.
        string summary = TelegramNotifier.BuildSummaryText(
            System.Text.Json.Nodes.JsonNode.Parse("""{"logs_last_day":212527,"logs_last_hour":5154,"unacknowledged_alerts":3,"sources":["a","b"]}"""));
        Check("summary text matches the template",
            summary == "\n📊 <b>LogAI Monitor Summary</b>\n\n<b>Logs (24h):</b> 212527\n<b>Logs (1h):</b> 5154\n<b>Active Alerts:</b> 3\n<b>Sources:</b> 2\n",
            Show(summary));

        // gated status 应覆盖模型的 overall_status，保证 Telegram 与 Analysis History 口径一致。
        string gated = TelegramNotifier.BuildSummaryText(
            System.Text.Json.Nodes.JsonNode.Parse("""{"logs_last_day":1,"logs_last_hour":1,"unacknowledged_alerts":0,"sources":[]}"""),
            """{"overall_status":"critical","issues_found":[],"critical_count":0,"recommendations":[],"affected_hosts":[],"alert_message":""}""",
            "warning");
        Check("gated status overrides raw overall_status", gated.Contains("WARNING") && !gated.Contains("CRITICAL"), Show(gated));

        // Delivery failures must not throw (invalid token is the cheap case).
        var notifier = new TelegramNotifier();
        bool emptyToken = notifier.SendAsync("", "1", "x").GetAwaiter().GetResult();
        Check("empty token returns false without throwing", emptyToken == false);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static string Show(string text) =>
        text.Replace("\n", "\\n").Replace("\r", "");

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}\n     got: {detail}"); }
    }
}
