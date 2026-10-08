// 模型分析正确性对比（--analysis-bench）。
//
// 为什么做成应用内的自测开关，而不是外面写个脚本直接打模型接口：只有走同一条
// PromptBuilder → AiClient → JsonExtractor → AiStatusClassifier 路径，比出来的
// 差异才归因于模型，而不是归因于"我另抄了一份提示词"。也因此它和调度器用的是
// 同一份模板、同一个 JSON 契约、同一套同义词归一。
//
// 判据是带标准答案的语料：每条用例写明期望档位、必须点到的关键词、以及不许
// 无中生有的词。四个分数：
//   exact    档位完全一致
//   tolerant 档位相差不超过一档（other 不算）
//   recall   期望 critical 的用例里真判成 critical —— 漏报是安全侧最贵的错
//   clean    期望 healthy 的用例里没有误报（误报会淹没真警）
//
// 不需要 Redis：只需要 AI 端点（AI_BASE_URL / AI_API_KEY）。
//
//   dotnet LogAI.Web.dll --analysis-bench
//   dotnet LogAI.Web.dll --analysis-bench --mode qwen35      # 两个模型都强制同一套提示词
//   dotnet LogAI.Web.dll --analysis-bench --models Qwen3.6-35B-A3B-MLX-4bit --repeat 2

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LogAI.Core.Ai;

namespace LogAI.Web;

internal static class AnalysisBench
{
    internal sealed record Sample(string Severity, string Host, string Program, string Message);

    internal sealed record BenchCase(string Id, string Expected, string[] MustMention,
                                     string[] MustNotMention, Sample[] Logs);

    private static Sample I(string host, string prog, string msg) => new("info", host, prog, msg);
    private static Sample W(string host, string prog, string msg) => new("warning", host, prog, msg);
    private static Sample E(string host, string prog, string msg) => new("error", host, prog, msg);
    private static Sample C(string host, string prog, string msg) => new("critical", host, prog, msg);

    /// <summary>默认对比的两个模型（设置页下拉框里的名字，需与 /v1/models 完全一致）。</summary>
    private static readonly string[] DefaultModels =
        ["Qwen2.5-Coder-7B-Instruct-4bit", "Qwen3.5-9B-MLX-4bit"];

    /// <summary>模型名 → 提示词模式：与设置页 wwwroot/js/app.js 的 syncPromptModeToModel() 同一套规则。</summary>
    internal static string ModeForModel(string model)
    {
        string m = model.ToLowerInvariant();
        if (m.Contains("qwen2.5") || m.Contains("coder")) return "qwen25";
        if (m.Contains("gemma")) return "gemma";
        if (m.Contains("qwen3.6") || m.Contains("35b")) return "qwen36";
        if (m.Contains("qwen3.5") || m.Contains("9b")) return "qwen35";
        return "default";
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string mode = ArgValue(args, "--mode") ?? "auto";
        string[] models = (ArgValue(args, "--models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (models.Length == 0) models = DefaultModels;
        int repeat = int.TryParse(ArgValue(args, "--repeat"), out int r) && r > 0 ? r : 1;

        string baseUrl = Environment.GetEnvironmentVariable("AI_BASE_URL")
                      ?? Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "";
        if (baseUrl.Length == 0)
        {
            Console.Error.WriteLine("AI_BASE_URL (or OLLAMA_HOST) is required");
            return 2;
        }
        string provider = Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "openai";
        string apiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "";

        var cases = BuiltinCases();
        // --cases a,b 只跑名字里含 a 或 b 的用例：稳定复测个别有争议的用例时，
        // 不必把 15 条语料 × N 次全部重跑。
        string filter = ArgValue(args, "--cases") ?? "";
        if (filter.Length > 0)
        {
            var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            cases = cases.Where(c => wanted.Any(w => c.Id.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        Console.WriteLine($"analysis-bench: {cases.Length} case(s) x {models.Length} model(s) x {repeat} run(s)");
        Console.WriteLine($"endpoint: {AiClient.NormalizeBaseUrl(baseUrl, provider)}  provider={provider}  thinking=off");
        Console.WriteLine();

        var summaries = new List<string>();
        foreach (string model in models)
        {
            string promptMode = mode == "auto" ? ModeForModel(model) : mode;
            var client = new AiClient
            {
                Provider = provider,
                BaseUrl = AiClient.NormalizeBaseUrl(baseUrl, provider),
                Model = model,
                ApiKey = apiKey,
                EnableThinking = false,   // 与生产设置一致（ai_thinking_enabled=false），也让每轮更快
            };
            var result = await RunModelAsync(client, model, promptMode, cases, repeat);
            PrintResult(result);
            summaries.Add(SummaryLine(result));
            Console.WriteLine();
        }

        Console.WriteLine("== comparison ==");
        foreach (string line in summaries) Console.WriteLine("  " + line);
        return 0;
    }

    internal sealed record BenchCaseResult(string Id, string Expected, string[] Verdicts,
                                           string LastIssues, bool Fabricated)
    {
        public int ExactCount => Verdicts.Count(v => v == Expected);
    }

    internal sealed record BenchResult(
        string Model, string PromptMode, int Attempts, int Exact, int Tolerant,
        int CritRecall, int CritTotal, int HealthyClean, int HealthyTotal,
        int IssueHit, int IssueTotal, int Fabricated, int ParseFail, double AvgSeconds,
        BenchCaseResult[] Cases);

    /// <summary>
    /// 跑一个模型 × 一组用例，返回结构化结果（不打印）。CLI --analysis-bench 与
    /// /api/ai/bench 共用；onProgress(done, total) 在每例完成后回调，供异步任务轮询。
    /// </summary>
    internal static async Task<BenchResult> RunModelAsync(AiClient client, string model, string promptMode,
                                                          BenchCase[] cases, int repeat,
                                                          Func<int, int, Task>? onProgress = null)
    {
        int attempts = 0, exact = 0, tolerant = 0, critRecall = 0, critTotal = 0;
        int healthyClean = 0, healthyTotal = 0, issueHit = 0, issueTotal = 0;
        int fabricated = 0, parseFail = 0;
        double seconds = 0;
        var caseResults = new List<BenchCaseResult>();

        foreach (var testCase in cases)
        {
            var logs = testCase.Logs
                .Select(s => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["severity"] = s.Severity, ["hostname"] = s.Host,
                    ["program"] = s.Program, ["message"] = s.Message,
                })
                .ToList();
            string prompt = PromptBuilder.BatchPromptFor(promptMode, PromptBuilder.LogSummary(logs));

            var verdicts = new List<string>();
            string lastIssues = "";
            bool caseBad = false;

            for (int attempt = 0; attempt < repeat; attempt++)
            {
                attempts++;
                critTotal += testCase.Expected == "critical" ? 1 : 0;
                healthyTotal += testCase.Expected == "healthy" ? 1 : 0;
                issueTotal += testCase.MustMention.Length > 0 ? 1 : 0;

                string got = "other", issues = "";
                var sw = Stopwatch.StartNew();
                try
                {
                    string reply = await client.CompleteAsync(prompt, cancellationToken: default,
                                                              enableThinking: false);
                    seconds += sw.Elapsed.TotalSeconds;

                    var analysis = JsonExtractor.Extract(reply) as JsonObject;
                    if (analysis is null || !JsonExtractor.HasRequiredFields(analysis))
                    {
                        parseFail++;
                        got = "parse-fail";
                        issues = "";
                    }
                    else
                    {
                        got = AiStatusClassifier.Classify("batch", analysis);
                        issues = (analysis["issues_found"] as JsonArray) is { } arr
                            ? string.Join(" | ", arr.Select(x => x?.ToString() ?? ""))
                            : "";
                        string haystack = (issues + " " + (analysis["alert_message"]?.ToString() ?? "") + " "
                                           + (analysis["recommendations"] as JsonArray is { } rec
                                                ? string.Join(" | ", rec.Select(x => x?.ToString() ?? "")) : ""))
                                          .ToLowerInvariant();
                        bool hit = testCase.MustMention.Length == 0 ||
                                   testCase.MustMention.Any(k => haystack.Contains(k, StringComparison.Ordinal));
                        bool bad = testCase.MustNotMention.Any(k => haystack.Contains(k, StringComparison.Ordinal));
                        issueHit += testCase.MustMention.Length > 0 && hit ? 1 : 0;
                        fabricated += bad ? 1 : 0;
                        caseBad |= bad;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    seconds += sw.Elapsed.TotalSeconds;
                    parseFail++;
                    got = "call-fail";
                }

                verdicts.Add(got);
                lastIssues = issues;
                exact += got == testCase.Expected ? 1 : 0;
                bool ok = got is not ("other" or "parse-fail" or "call-fail")
                          && Math.Abs(Level(got) - Level(testCase.Expected)) <= 1;
                tolerant += ok ? 1 : 0;
                critRecall += testCase.Expected == "critical" && got == "critical" ? 1 : 0;
                healthyClean += testCase.Expected == "healthy" && got == "healthy" ? 1 : 0;
            }

            caseResults.Add(new BenchCaseResult(testCase.Id, testCase.Expected, verdicts.ToArray(), lastIssues, caseBad));
            if (onProgress is not null) await onProgress(caseResults.Count, cases.Length);
        }

        double avg = attempts > 0 ? seconds / attempts : 0;
        return new BenchResult(model, promptMode, attempts, exact, tolerant,
            critRecall, critTotal, healthyClean, healthyTotal, issueHit, issueTotal,
            fabricated, parseFail, avg, caseResults.ToArray());
    }

    private static string SummaryLine(BenchResult r) =>
        $"{r.Model} [{r.PromptMode}]  exact {r.Exact}/{r.Attempts}  tolerant {r.Tolerant}/{r.Attempts}  "
        + $"critical-recall {r.CritRecall}/{r.CritTotal}  healthy-clean {r.HealthyClean}/{r.HealthyTotal}  "
        + $"issue-hit {r.IssueHit}/{r.IssueTotal}  fabricated {r.Fabricated}  parse-fail {r.ParseFail}  avg {r.AvgSeconds:0.0}s";

    private static void PrintResult(BenchResult r)
    {
        Console.WriteLine($"=== {r.Model}   prompt-mode={r.PromptMode} ===");
        foreach (var c in r.Cases)
        {
            int repeat = c.Verdicts.Length;
            int exactHere = c.ExactCount;
            string mark = exactHere == repeat ? "ok  " : exactHere == 0 ? "MISS" : "MIXD";
            string shown = repeat == 1 ? c.Verdicts[0] : string.Join("/", c.Verdicts);
            Console.WriteLine($"  [{mark}] {c.Id,-26} exp={c.Expected,-8} got={shown,-24}"
                              + (c.Fabricated ? " FABRICATED" : ""));
            if (exactHere != repeat)
                Console.WriteLine($"         issues(last): {Truncate(c.LastIssues, 200)}");
        }
        Console.WriteLine($"  -> {SummaryLine(r)}");
    }

    private static int Level(string status) => status switch
    {
        "critical" => 2,
        "warning" => 1,
        "healthy" => 0,
        _ => -1,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ------------------------------------------------------------------ 语料
    // 期望档位的依据尽量取"字面事实"，不取主观判断：
    //   入侵/数据损坏/阵列掉盘/恶意文件 → critical
    //   磁盘将满/内存打死进程/证书到期/SYN 洪泛/SMART 待映射扇区/无效用户探测/单个进程崩溃 → warning
    //   例行 cron、正常启停、单次客户端断开 → healthy
    internal static BenchCase[] BuiltinCases()
    {
        var needle = new List<Sample>();
        for (int i = 0; i < 40; i++)
            needle.Add(I("nas01", "crond", $"USER root pid {4000 + i} cmd /lib/functions/mptun.sh"));
        needle.Insert(20, I("nas01", "clamav", "/mnt/share/invoice.pdf: Eicar-Test-Signature FOUND"));

        return
        [
            new("routine-cron", "healthy", [],
                ["critical", "attack", "malware", "compromise", "corrupt", "intrusion"],
            [
                I("GL-AXT1800", "crond", "USER root pid 4643 cmd /lib/functions/mptun.sh"),
                I("GL-AXT1800", "crond", "USER root pid 4701 cmd /usr/bin/wg-watchdog"),
                I("GL-AXT1800", "crond", "USER root pid 4822 cmd /lib/functions/mptun.sh"),
                I("GL-AXT1800", "ntpd", "adjusting local clock by 0.003412s"),
                I("GL-AXT1800", "hostapd", "wlan0: STA 11:22:33:44:55:66 IEEE 802.11: associated"),
            ]),

            new("service-restart", "healthy", [],
                ["critical", "attack", "malware", "compromise", "outage"],
            [
                I("web01", "systemd", "Stopping nginx.service - A high performance web server..."),
                I("web01", "systemd", "Started nginx.service - A high performance web server."),
                I("web01", "systemd", "Reloading nginx configuration"),
                I("web01", "nginx", "signal process started"),
            ]),

            new("btrfs-corruption", "critical", ["btrfs", "corrupt", "checksum", "csum"], [],
            [
                C("XiaoBao", "kernel", "BTRFS error (device mmcblk0p2): bdev /dev/mmcblk0p2 errs: wr 0, rd 0, flush 0, corrupt 6518, gen 0"),
                E("XiaoBao", "kernel", "BTRFS warning (device mmcblk0p2): csum failed root 5 ino 19596 off 737280"),
                E("XiaoBao", "kernel", "BTRFS error (device mmcblk0p2): read error at logical 123456789"),
            ]),

            new("ssh-bruteforce", "critical", ["brute", "failed", "203.0.113.5", "authentication"], [],
            [
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 51001 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 51002 ssh2"),
                W("edge01", "sshd", "Failed password for admin from 203.0.113.5 port 51003 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 51004 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 51005 ssh2"),
                W("edge01", "sshd", "Failed password for oracle from 203.0.113.5 port 51006 ssh2"),
                W("edge01", "sshd", "error: maximum authentication attempts exceeded for root from 203.0.113.5 port 51007 ssh2 [preauth]"),
                W("edge01", "sshd", "PAM 4 more authentication failures; logname= uid=0 euid=0 tty=ssh ruser= rhost=203.0.113.5"),
            ]),

            new("ssh-accepted-after-failures", "critical", ["accepted", "success", "203.0.113.5", "login"], [],
            [
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 52001 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 52002 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 52003 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 52004 ssh2"),
                W("edge01", "sshd", "Failed password for root from 203.0.113.5 port 52005 ssh2"),
                W("edge01", "sshd", "Accepted password for root from 203.0.113.5 port 52006 ssh2"),
                I("edge01", "sshd", "pam_unix(sshd:session): session opened for user root by (uid=0)"),
            ]),

            new("malware-found", "critical", ["trojan", "malware", "clamav", "virus"], [],
            [
                E("fs01", "clamav", "/srv/upload/setup.exe: Win.Trojan.Agent-1234567 FOUND"),
                I("fs01", "clamav", "Scanning /srv/upload ..."),
            ]),

            new("raid-disk-failure", "critical", ["raid", "disk", "sdb1", "degraded", "fail"], [],
            [
                C("db01", "kernel", "md/raid1:md0: Disk failure on sdb1, disabling device."),
                C("db01", "kernel", "md/raid1:md0: Operation continuing on 1 devices."),
                W("db01", "smartd", "Device: /dev/sdb1, FAILED SMART self-check. Back up Data now!"),
            ]),

            new("needle-in-haystack", "critical", ["eicar", "malware", "trojan", "virus", "clamav"], [],
                needle.ToArray()),

            new("disk-full", "warning", ["space", "disk", "full", "ext4"], [],
            [
                E("web01", "kernel", "EXT4-fs warning (device sda1): ext4_end_bio:344: I/O error 10 writing to inode 1234"),
                E("web01", "nginx", "write() failed (28: No space left on device) while writing to /var/log/nginx/access.log"),
                W("web01", "systemd", "var-log.mount: Mount point is not empty, files may be hidden"),
            ]),

            new("oom-kill", "warning", ["memory", "oom", "killed", "swap"], [],
            [
                C("app01", "kernel", "Out of memory: Killed process 28411 (java) total-vm:8123456kB, anon-rss:7456123kB"),
                W("app01", "kernel", "app01 invoked oom-killer: gfp_mask=0x1100cca(GFP_HIGHUSER_MOVABLE), order=0"),
                E("app01", "systemd", "app.service: Main process exited, code=killed, status=9/KILL"),
            ]),

            new("cert-expiry", "warning", ["cert", "expir", "tls", "ssl"], [],
            [
                W("web01", "nginx", "certificate '/etc/ssl/certs/web01.crt' will expire in 7 days"),
                I("web01", "systemd", "Started nginx.service - A high performance web server."),
            ]),

            new("syn-flood", "warning", ["syn", "flood", "tcp", "connection"], [],
            [
                W("edge01", "kernel", "TCP: request_sock_TCP: Possible SYN flooding on port 443. Sending cookies.  Check SNMP counters."),
                W("edge01", "kernel", "TCP: request_sock_TCP: Possible SYN flooding on port 443. Sending cookies."),
            ]),

            new("invalid-user-probes", "warning", ["invalid", "ssh", "user", "prob"], [],
            [
                W("edge01", "sshd", "Invalid user admin from 198.51.100.7 port 33101 ssh2"),
                W("edge01", "sshd", "Invalid user test from 198.51.100.7 port 33102 ssh2"),
                W("edge01", "sshd", "Invalid user postgres from 198.51.100.7 port 33103 ssh2"),
                W("edge01", "sshd", "Invalid user oracle from 198.51.100.7 port 33104 ssh2"),
            ]),

            new("smart-pending-sectors", "warning", ["smart", "sector", "sda", "disk"], [],
            [
                W("db01", "smartd", "Device: /dev/sda [SAT], 4 Currently unreadable (pending) sectors"),
                I("db01", "smartd", "Device: /dev/sda [SAT], SMART Usage Attribute: 194 Temperature_Celsius changed from 38 to 39"),
            ]),

            new("kernel-segfault", "warning", ["segfault", "crash", "nginx"], [],
            [
                E("web01", "kernel", "nginx[2199]: segfault at 0 ip 000055d1c2b3f4a1 sp 00007ffd1a2b3c40 error 4 in nginx[55d1c2a00000+24000]"),
                I("web01", "systemd", "nginx.service: Scheduled restart job, restart counter is at 1."),
            ]),

            // ---- 扩充用例（2026-10-08）：补 healthy 负例 + 更多 critical/warning ----

            new("dhcp-renew", "healthy", [],
                ["critical", "attack", "malware", "down", "fail"],
            [
                I("edge01", "dhclient", "DHCPREQUEST for 192.168.50.10 on eth0 to 255.255.255.255 port 67"),
                I("edge01", "dhclient", "DHCPACK from 192.168.50.1 (xid=0x1a2b3c4d)"),
                I("edge01", "dhclient", "bound to 192.168.50.10 -- renewal in 43100 seconds"),
            ]),

            new("ntp-sync", "healthy", [],
                ["critical", "attack", "malware", "down", "fail"],
            [
                I("nas01", "ntp", "start NTP update"),
                I("nas01", "ntpd", "adjusting local clock by 0.001234s"),
                I("nas01", "ntpd", "synchronized to 192.168.50.1, stratum 2"),
            ]),

            new("backup-success", "healthy", [],
                ["critical", "attack", "malware", "fail", "error"],
            [
                I("nas01", "rsync", "Backup completed successfully: sent 4.2G bytes  received 0 bytes  3.1M bytes/sec"),
                I("nas01", "systemd", "Finished backup.service - Nightly backup."),
            ]),

            new("kernel-panic", "critical", ["panic", "kernel", "crash", "reboot"], [],
            [
                C("db01", "kernel", "Kernel panic - not syncing: Fatal exception in interrupt"),
                C("db01", "kernel", "Oops: 0000 [#1] SMP PTI"),
                C("db01", "systemd", "systemd-shutdown[1]: Sending SIGKILL to remaining processes"),
            ]),

            new("filesystem-readonly", "critical", ["read-only", "readonly", "remount", "data loss", "ext4"], [],
            [
                C("nas01", "kernel", "EXT4-fs error (device sda1): ext4_journal_check_start: Detected aborted journal"),
                C("nas01", "kernel", "EXT4-fs (sda1): Remounting filesystem read-only"),
                E("nas01", "kernel", "Buffer I/O error on device sda1, logical block 123456"),
            ]),

            new("database-down", "critical", ["mysql", "down", "refused", "crash", "3306"], [],
            [
                C("db01", "systemd", "mysqld.service: Main process exited, code=exited, status=1/FAILURE"),
                E("db01", "mysqld", "Can't connect to local MySQL server through socket /var/run/mysqld/mysqld.sock"),
                E("web01", "nagios", "CRITICAL - db01:3306 - connection refused"),
            ]),

            new("rootkit-detected", "critical", ["rootkit", "rkhunter", "malware", "trojan"], [],
            [
                C("web01", "rkhunter", "Warning: Possible rootkit: Xzibit Rootkit detected"),
                C("web01", "rkhunter", "Warning: Hidden file found: /etc/.hidden_backdoor"),
            ]),

            new("service-restart-loop", "warning", ["restart", "loop", "failed", "service"], [],
            [
                E("web01", "systemd", "nginx.service: Main process exited, code=exited, status=1/FAILURE"),
                E("web01", "systemd", "nginx.service: Unit entered failed state."),
                I("web01", "systemd", "nginx.service: Scheduled restart job, restart counter is at 4."),
                E("web01", "nginx", "bind() to 0.0.0.0:443 failed (98: Address already in use)"),
            ]),

            new("backup-failed", "warning", ["backup", "fail", "refused", "error"], [],
            [
                E("nas01", "rsync", "rsync: connection unexpectedly closed (0 bytes received so far)"),
                E("nas01", "systemd", "backup.service: Main process exited, code=exited, status=1/FAILURE"),
                E("nas01", "systemd", "Failed to start backup.service - Nightly backup."),
            ]),

            new("memory-pressure", "warning", ["memory", "swap", "pressure", "high"], [],
            [
                W("app01", "systemd", "Memory usage: 94% (of 32G), swap usage: 62%"),
                W("app01", "kernel", "pswpin: 12000 pages swapped in per second"),
            ]),

            new("fan-failure", "warning", ["fan", "thermal", "cooling", "throttl"], [],
            [
                W("db01", "ipmi", "Fan 2: fan failure detected (0 RPM)"),
                W("db01", "ipmi", "CPU temperature 82C - thermal throttling active"),
            ]),

            new("upstream-connection-refused", "warning", ["refused", "upstream", "connect", "fail", "timed out"], [],
            [
                E("web02", "nginx", "connect() failed (111: Connection refused) while connecting to upstream, client: 1.2.3.4"),
                E("web02", "nginx", "upstream timed out (110: Connection timed out) while connecting to upstream"),
            ]),
        ];
    }
}
