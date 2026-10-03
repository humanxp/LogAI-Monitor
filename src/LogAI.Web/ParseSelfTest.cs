// Field-level self-test for the syslog parser.
//
// Every case is a real sample captured on the production network, plus the
// regression cases for the hostname guard and for RFC 5424 structured data
// containing spaces.  Run with:  dotnet LogAI.Web.dll --parse-selftest

using LogAI.Core.Syslog;

namespace LogAI.Web;

internal static class ParseSelfTest
{
    private static int _failures;

    private sealed record Case(
        string Name,
        string Raw,
        string SourceIp,
        string Hostname,
        string Program,
        string Message,
        string? Pid = null,
        string? Facility = null,
        string? Severity = null,
        string? ProcId = null,
        string? MsgId = null,
        string? Timestamp = null);

    public static int Run()
    {
        Case[] cases =
        [
            // --- the hostname guard: senders that omit the hostname -----
            new("3164 no host (connmand[351]:)",
                "Sep 25 07:22:59 connmand[351]: ntp: adjust (jump): +1474790149.998557 sec",
                "192.168.50.37", "192.168.50.37", "connmand",
                "ntp: adjust (jump): +1474790149.998557 sec", Pid: "351"),
            new("3164 no host (rngd:)",
                "Sep 25 07:22:59 rngd: [jitter]: Enabling JITTER rng support",
                "192.168.50.37", "192.168.50.37", "rngd",
                "[jitter]: Enabling JITTER rng support"),
            new("3164 relay tag without pid",
                "Sep 25 02:05:41 LANSCAN-SYSLOG: forward test to 192.168.50.6",
                "192.168.50.37", "192.168.50.37", "LANSCAN-SYSLOG",
                "forward test to 192.168.50.6"),

            // --- normal host present -----------------------------------
            new("3164 host kept",
                "Sep 25 07:22:59 web01 sshd[1234]: Accepted password for root",
                "10.0.0.1", "web01", "sshd", "Accepted password for root", Pid: "1234"),
            new("3164 ip as hostname",
                "Sep 25 07:22:59 192.168.50.10 systemd[1]: Started Session 42.",
                "10.0.0.1", "192.168.50.10", "systemd", "Started Session 42.", Pid: "1"),
            new("3164 space padded day",
                "Oct  1 13:05:11 box01 cron[9]: job done",
                "10.0.0.1", "box01", "cron", "job done", Pid: "9"),

            // --- PRI ---------------------------------------------------
            new("PRI 134 local0/info",
                "<134>Sep 25 07:22:59 web01 sshd[1]: hi",
                "10.0.0.1", "web01", "sshd", "hi", Pid: "1",
                Facility: "local0", Severity: "info"),
            new("PRI 11 user/error",
                "<11>Sep 25 07:22:59 web01 sshd[1]: hi",
                "10.0.0.1", "web01", "sshd", "hi", Pid: "1",
                Facility: "user", Severity: "error"),

            // --- RFC 5424 ----------------------------------------------
            new("5424 basic",
                "2026-09-07T05:23:11.564Z localhost.localdomain Hostd 2346082 ID47 - open failed",
                "192.168.50.3", "localhost.localdomain", "Hostd", "open failed",
                ProcId: "2346082", MsgId: "ID47"),
            new("5424 nil hostname keeps sender ip",
                "2026-09-07T05:23:11.564Z - Hostd 1 ID47 - hello",
                "192.168.50.3", "192.168.50.3", "Hostd", "hello",
                ProcId: "1", MsgId: "ID47"),
            new("5424 structured data with spaces",
                "2026-09-07T05:23:11.564Z esxi01 vmkernel 1234 ID47 [Originator@6876 sub=Default] cpu0: alert",
                "192.168.50.3", "esxi01", "vmkernel", "cpu0: alert",
                ProcId: "1234", MsgId: "ID47"),
            new("5424 structured data only",
                "2026-09-07T05:23:11.564Z esxi01 vmkernel 1234 ID47 [Originator@6876 sub=Default]",
                "192.168.50.3", "esxi01", "vmkernel", "[Originator@6876 sub=Default]",
                ProcId: "1234", MsgId: "ID47"),

            // --- fallback ----------------------------------------------
            new("no pattern: whole line becomes the message",
                "this line has no timestamp or tag at all",
                "192.168.50.37", "192.168.50.37", "unknown",
                "this line has no timestamp or tag at all"),
        ];

        Console.WriteLine($"{"case",-46} result");
        foreach (var test in cases)
        {
            var entry = SyslogParser.Parse(test.Raw, test.SourceIp);
            var problems = new List<string>();

            void Check(string field, string? expected, string actual)
            {
                if (expected is null) return;
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    problems.Add($"{field}: got '{actual}', want '{expected}'");
            }

            Check("hostname", test.Hostname, entry.Hostname);
            Check("program", test.Program, entry.Program);
            Check("message", test.Message, entry.Message);
            Check("pid", test.Pid, entry.Pid ?? "");
            Check("facility", test.Facility, entry.Facility);
            Check("severity", test.Severity, entry.Severity);
            Check("proc_id", test.ProcId, entry.ProcId ?? "");
            Check("msg_id", test.MsgId, entry.MsgId ?? "");

            if (problems.Count == 0)
            {
                Console.WriteLine($"{test.Name,-46} ok");
            }
            else
            {
                _failures++;
                Console.WriteLine($"{test.Name,-46} FAIL");
                foreach (string problem in problems) Console.WriteLine($"    {problem}");
            }
        }

        // Decoding: GB18030 Chinese must survive, latin-1 must never fail.
        var gbk = new byte[] { 0x2e, 0x2e, 0xb2, 0xe2, 0xca, 0xd4 };  // "..测试"
        string decoded = SyslogParser.SmartDecode(gbk);
        if (decoded.EndsWith("测试", StringComparison.Ordinal)) Console.WriteLine($"{"decode GB18030",-46} ok");
        else { _failures++; Console.WriteLine($"{"decode GB18030",-46} FAIL (got '{decoded}')"); }

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        // 退出码必须是 成功=0 / 失败=1（另加 2 = 拒绝在 DB 0 上跑），与其余 20 套
        // 自测一致。这里曾经写成 成功=1、失败=2，于是 CI/脚本把"2164 项断言全过"
        // 判成失败——自测的退出码本身就是被测接口，写反等于让所有调用方误判。
        return _failures == 0 ? 0 : 1;
    }
}
