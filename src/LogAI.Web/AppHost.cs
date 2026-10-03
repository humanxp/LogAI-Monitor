// Wires the background subsystems into the web application.
//
//   syslog receiver (UDP + TCP) -> LogWriter -> filters -> alerts -> Telegram
//   scheduler: analysis (settings interval), cleanup (retention), health, docker
//
// The ingest callback stores first and only then runs the alert pipeline, so an
// alert can never reference a log id that was not persisted.

using LogAI.Core.Ai;
using LogAI.Core.Docker;
using LogAI.Core.Filters;
using LogAI.Core.Notify;
using LogAI.Core.Scheduler;
using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Web;

using LogAI.Web.Api;

internal static class AppHost
{
    /// <summary>
    /// 单批分析日志数的硬上限。batch_size 由设置页提供，没有上限时一个
    /// "都填 9" 的设置就能让每轮分析抓取并水合海量日志直至 OOM。
    /// </summary>
    internal const int MaxAnalysisBatch = 5000;

    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var store = app.Services.GetRequiredService<RedisStore>();
        var settings = await store.GetSettingsAsync();

        int retentionHours = IntSetting(settings, "log_retention_hours", 720);
        int analysisMinutes = Math.Max(1, IntSetting(settings, "analysis_interval", 2));
        // batch_size 来自设置页，过去没有任何上限：写成 1000000000 就会让分析任务
        // 一次抓十亿条日志并全部水合成提示词（OOM）。UI 最多给 500，这里留 10 倍余量。
        int batchSize = Math.Min(IntSetting(settings, "batch_size", 500), MaxAnalysisBatch);
        int cooldownMinutes = IntSetting(settings, "alert_cooldown_minutes", 5);
        int warnThreshold = IntSetting(settings, "analysis_warn_threshold", 2000);
        bool alertOnCritical = BoolSetting(settings, "alert_on_critical", true);
        bool alertOnError = BoolSetting(settings, "alert_on_error", true);

        // 巡检看门狗（对齐 Python 的 health_watchdog）：积压 / AI 后端 / 调度卡死
        // 三种条件各自按冷却时间推送 Telegram，恢复正常时补一条"已恢复"，
        // 并有可选的巡检日报。这些设置键在 Python 侧一直在用，本实现此前
        // 完全没读——所以巡检再糟也只是一行日志，不会通知任何人。
        int healthWarn = IntSettingAllowZero(settings, "health_backlog_warn", warnThreshold);
        int healthWatchMinutes = Math.Max(1, IntSetting(settings, "health_watch_minutes", 5));
        int healthAlertCooldownMinutes = Math.Max(1, IntSetting(settings, "health_alert_cooldown_min", 30));
        bool healthDailySummary = BoolSetting(settings, "health_daily_summary", true);

        // Hard guard: database 0 is the LIVE production database. An instance
        // pointed at it for read-only comparison must not ingest, analyse, clean
        // up or poll docker - all of those write. This is enforced in code rather
        // than left to whoever starts the container remembering an env var.
        int database = int.TryParse(Environment.GetEnvironmentVariable("REDIS_DB"), out int configuredDb)
            ? configuredDb : 0;
        if (database == 0 && Environment.GetEnvironmentVariable("ALLOW_DB0_WRITES") != "1")
        {
            Console.WriteLine("[AppHost] background writers DISABLED: Redis database 0 is the live "
                + "production database. Set ALLOW_DB0_WRITES=1 only when this instance is meant "
                + "to replace the running one.");
            return;
        }

        // First-run bootstrap, mirroring the Python ensure_admin_exists(): a fresh
        // deployment has no user at all, so without this nobody could ever sign in.
        // Idempotent: it only acts when no account with role=admin exists.
        bool hasAdmin = false;
        foreach (var value in await store.Db.SetMembersAsync("users:all"))
        {
            var fields = await store.Db.HashGetAllAsync(value.ToString());
            foreach (var f in fields)
                if (f.Name == "role" && f.Value == "admin") { hasAdmin = true; break; }
            if (hasAdmin) break;
        }
        if (!hasAdmin)
        {
            string adminId = "user:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            await store.Db.HashSetAsync(adminId,
            [
                new StackExchange.Redis.HashEntry("username", "admin"),
                new StackExchange.Redis.HashEntry("password_hash",
                    LogAI.Core.Auth.WerkzeugPasswordGenerator.Generate("admin")),
                new StackExchange.Redis.HashEntry("email", ""),
                new StackExchange.Redis.HashEntry("role", "admin"),
                new StackExchange.Redis.HashEntry("id", adminId),
                new StackExchange.Redis.HashEntry("created_at", DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-ddTHH:mm:ss.ffffff+00:00",
                    System.Globalization.CultureInfo.InvariantCulture)),
            ]);
            await store.Db.SetAddAsync("users:all", adminId);
            await store.Db.StringSetAsync("users:username:admin", adminId);
            Console.WriteLine("[Redis] Created default admin user (username: admin, password: admin)");
        }

        var writer = new LogWriter(store, retentionHours);
        var tracker = new ClientTracker(store);
        var alerts = new AlertWriter(store);
        var notifier = new TelegramNotifier();

        // ---- syslog ingest ------------------------------------------------
        int udpPort = IntEnv("SYSLOG_UDP_PORT", 514);
        int tcpPort = IntEnv("SYSLOG_TCP_PORT", 515);
        var receiver = new SyslogReceiver(new ReceiverOptions { UdpPort = udpPort, TcpPort = tcpPort });

        async Task OnStored(SyslogEntry entry, string protocol)
        {
            string logId = await writer.StoreAsync(entry, cancellationToken);

            // Live updates: the protocol layer was already verified, but nothing
            // ever published an application event, so the dashboard would load
            // correctly and then quietly go stale.
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            LogAI.Web.Realtime.EngineIoServer.Current?.Broadcast("new_log",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["facility"] = entry.Facility,
                    ["hostname"] = entry.Hostname,
                    ["id"] = logId,
                    ["message"] = entry.Message,
                    ["program"] = entry.Program,
                    ["severity"] = entry.Severity,
                    ["source"] = entry.Source,
                    ["timestamp"] = entry.Timestamp,
                });

            // Connected-Clients bookkeeping: the index zset, the per-client hash,
            // the protocol set and the 60s rate window.
            await tracker.TrackAsync(entry.Source, protocol, entry.Hostname ?? entry.Source,
                                     cancellationToken: cancellationToken);

            // 过滤器在每条日志上重新读取（规则很少，代价可接受），但这意味着
            // "一条都没匹配" 与 "根本没加载到规则" 在日志里无法区分。默认只在
            // 有匹配时打点；需要排查时设 FILTER_TRACE=1，则每一条日志都记录
            // 本轮的规则数与命中数，用来区分"没进循环""进了但没匹配""匹配了"。
            bool filterTrace = Environment.GetEnvironmentVariable("FILTER_TRACE") == "1";
            var rules = await LoadFiltersAsync(store);
            int matched = 0;
            foreach (var rule in rules)
            {
                if (!FilterMatcher.Matches(rule, entry.Source, entry.Severity, entry.Message)) continue;
                matched++;

                string alertId = await alerts.WriteAsync(rule, entry, logId, cancellationToken);
                LogAI.Web.Api.StatsApi.PushIfNeeded(store);
            LogAI.Web.Realtime.EngineIoServer.Current?.Broadcast("new_alert",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["filter_id"] = rule.Id,
                        ["filter_name"] = rule.Name,
                        ["hostname"] = entry.Hostname,
                        ["id"] = alertId,
                        ["log_id"] = logId,
                        ["message"] = entry.Message.Length <= 500 ? entry.Message : entry.Message[..500],
                        ["severity"] = entry.Severity,
                        ["source"] = entry.Source,
                    });

                // Every outcome is logged with its reason. The previous shape only had
                // silent `continue`s, so "blocked by the level gate", "suppressed by
                // cooldown" and "Telegram not configured" were indistinguishable - which
                // is why alert pushes could quietly stop with nothing in the log.
                string gate = "filter=" + rule.Id + " notify=" + rule.NotifyTelegram
                    + " any_severity=" + rule.NotifyAnySeverity + " severity=" + entry.Severity
                    + " host=" + entry.Hostname;
                if (!FilterMatcher.TelegramAllowed(rule, entry.Severity, alertOnCritical, alertOnError))
                {
                    Console.WriteLine("[Telegram] alert blocked by level gate (" + gate
                        + " alert_on_critical=" + alertOnCritical + " alert_on_error=" + alertOnError + ")");
                    continue;
                }
                if (!await TelegramState.EnsureAsync(store, cancellationToken))
                {
                    Console.WriteLine("[Telegram] alert not sent, Telegram not configured (" + gate + ")");
                    continue;
                }
                // 冷却必须在"确认能发"之后再占用：原顺序是 先占冷却 再检查配置，
                // 于是 Telegram 未配置时也会把冷却窗口消耗掉，等配置补齐后
                // 该主机+规则的告警会在冷却期内被判为 "suppressed" 而静默丢弃。
                if (!await alerts.AcquireCooldownAsync(entry.Hostname, rule.Id, cooldownMinutes))
                {
                    Console.WriteLine("[Telegram] alert suppressed by cooldown " + cooldownMinutes
                        + "m (" + gate + ")");
                    continue;
                }
                bool alertDelivered = await notifier.SendAsync(TelegramState.BotToken, TelegramState.ChatId,
                    TelegramNotifier.BuildAlertText(entry.Severity, entry.Source, entry.Message, entry.Hostname),
                    cancellationToken);
                Console.WriteLine("[Telegram] alert " + (alertDelivered ? "sent" : "FAILED")
                    + " (" + gate + ")");
            }

            if (filterTrace)
                Console.WriteLine("[Filters] loaded=" + rules.Count + " matched=" + matched
                    + " severity=" + entry.Severity + " source=" + entry.Source);
        }

        // Publish the receiver facts the diagnostics endpoint reports.
        LogAI.Web.Api.ReceiverState.UdpPort = udpPort;
        LogAI.Web.Api.ReceiverState.TcpPort = tcpPort;
        LogAI.Web.Api.ReceiverState.StartedAt = DateTimeOffset.UtcNow;
        LogAI.Web.Api.ReceiverState.Receiver = receiver;

        _ = receiver.RunAsync(OnStored, cancellationToken);

        // ---- scheduler ----------------------------------------------------
        string provider = TextSetting(settings, "ai_provider", "openai");
        string aiHost = TextSetting(settings, "ollama_host",
            Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "");
        var client = new AiClient
        {
            Provider = provider,
            BaseUrl = AiClient.NormalizeBaseUrl(aiHost, provider),
            Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "",
            ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
        };

        var runner = new AnalysisRunner(store, client, new AiHistoryWriter(store), batchSize);
        var scheduler = new JobScheduler(TimeSpan.FromSeconds(10));

        // The health check reports the age of the last analysis RUN, not of the
        // last stored analysis: the Python version updates that timestamp even
        // when a run analyses nothing, so an idle queue is not mistaken for a
        // stalled analyser.
        DateTimeOffset? lastAnalysis = null;
        // MinValue so the first health run reports immediately, then once a minute.
        DateTimeOffset lastReceiverReport = DateTimeOffset.MinValue;
        bool aiAvailable = false;
        DateTimeOffset? aiChecked = null;

        scheduler.Add("analysis", () => TimeSpan.FromMinutes(analysisMinutes),
            async ct =>
            {
                try
                {
                    var outcome = await runner.RunOnceAsync(ct);
                    // "这一次没东西可分析"与"这次根本没跑/跑完没记录"必须能区分：
                    // AI History 里的空洞（曾出现 13:48 → 18:07 无任何记录）
                    // 只有配上这一行才解释得清。
                    if (outcome.Status == "empty")
                        Console.WriteLine("[Analysis] nothing to analyze");
                    if (outcome.Status == "analyzed")
                    {
                        // 成功也要留痕：此前只有推给前端的事件，服务端日志里
                        // "分析在跑"与"分析卡死"完全一样——巡检心跳里的 age 因此
                        // 难以解读（曾把"跑过但没记录"误读成"从未运行"）。
                        Console.WriteLine("[Analysis] analyzed " + outcome.Count + " log(s)"
                            + (string.IsNullOrEmpty(outcome.HistoryId) ? "" : " history=" + outcome.HistoryId));
                        LogAI.Web.Api.StatsApi.PushIfNeeded(store);
                        // The dashboard reads data.logs_analyzed and renders data.analysis
                        // (Python emitted exactly those two keys). Sending count/history_id
                        // alone made the toast read "Analyzed undefined logs" and left an
                        // automatic analysis invisible on the page.
                        string? analysisRaw = null;
                        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["logs_analyzed"] = outcome.Count,
                        };
                        if (!string.IsNullOrEmpty(outcome.HistoryId))
                        {
                            var hash = await store.Db.HashGetAllAsync(outcome.HistoryId);
                            foreach (var field in hash)
                            {
                                if (field.Name != "analysis") continue;
                                string raw = field.Value.ToString();
                                if (raw.Length == 0) break;
                                analysisRaw = raw;
                                try
                                {
                                    payload["analysis"] = System.Text.Json.JsonSerializer
                                        .Deserialize<Dictionary<string, object?>>(raw);
                                }
                                catch (System.Text.Json.JsonException)
                                {
                                    // A malformed record must not stop the scheduler.
                                }
                                break;
                            }
                        }
                        LogAI.Web.Realtime.EngineIoServer.Current?.Broadcast("analysis_complete", payload);

                        // Python sent the summary to Telegram whenever an analysis came
                        // back critical ("Send Telegram summary if critical issues found");
                        // that path was missing here. It has no throttle upstream, so an
                        // independent cooldown keeps a critical-every-few-minutes stream
                        // from flooding the chat. 0 disables the throttle.
                        if (analysisRaw is not null)
                        {
                            try
                            {
                                var analysisNode = System.Text.Json.Nodes.JsonNode.Parse(analysisRaw);
                                if (analysisNode?["overall_status"]?.ToString() == "critical"
                                    && await LogAI.Core.Notify.TelegramState.EnsureAsync(store, ct))
                                {
                                    int summaryCooldown = IntSetting(
                                        await store.GetSettingsAsync(), "analysis_summary_cooldown_min", 30);
                                    bool maySend = summaryCooldown <= 0
                                        || await store.Db.StringSetAsync("notify:cooldown:analysis_summary",
                                               "1", TimeSpan.FromMinutes(summaryCooldown),
                                               StackExchange.Redis.When.NotExists);
                                    if (!maySend)
                                    {
                                        Console.WriteLine("[Telegram] critical summary skipped (cooldown "
                                            + summaryCooldown + "m)");
                                    }
                                    else
                                    {
                                        var statsNode = System.Text.Json.Nodes.JsonNode.Parse(
                                            ReadApi.SerializeLikeFlask(
                                                await LogAI.Web.Api.StatsApi.BuildPayloadAsync(store)));
                                        string text = LogAI.Core.Notify.TelegramNotifier
                                            .BuildSummaryText(statsNode, analysisRaw);
                                        bool sent = await notifier.SendAsync(
                                            LogAI.Core.Notify.TelegramState.BotToken,
                                            LogAI.Core.Notify.TelegramState.ChatId, text, ct);
                                        if (sent) Console.WriteLine("[Telegram] critical analysis summary sent");
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // A notification failure must never break the scheduler.
                                Console.Error.WriteLine("[Telegram] critical summary failed: " + ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 失败写进 Redis（最近 100 条，带原因），而不是只写 stdout：
                    // stdout 随容器重建消失，事后无法回答"这段时间为什么没有分析记录"。
                    await RecordAnalysisFailureAsync(store, ex, ct);
                    throw;   // 交给调度器，保留它那行 job failed 与堆栈
                }
                finally { lastAnalysis = DateTimeOffset.UtcNow; }
            },
            firstDelay: TimeSpan.FromSeconds(20));

        // 巡检周期由 health_watch_minutes 决定（Python 侧会热重排；这里在启动时
        // 读取一次，改完设置需要重启容器才生效）。
        scheduler.Add("health", () => TimeSpan.FromMinutes(healthWatchMinutes), async ct =>
        {
            if (aiChecked is null || (DateTimeOffset.UtcNow - aiChecked.Value).TotalSeconds > 60)
            {
                aiAvailable = await client.IsAvailableAsync(ct);
                aiChecked = DateTimeOffset.UtcNow;
            }

            long backlog = await store.Db.SortedSetLengthAsync(Keys.Unanalyzed);
            long total = await store.Db.SortedSetLengthAsync(Keys.Timeline);
            int age = lastAnalysis is { } run ? (int)(DateTimeOffset.UtcNow - run).TotalSeconds : -1;

            HealthState.AiAvailable = aiAvailable;
            HealthState.AiModel = client.Model;
            HealthState.LastAnalysisAgeSeconds = age;
            HealthState.WarnThreshold = healthWarn;
            HealthState.AnalysisMinutes = analysisMinutes;

            var inputs = new HealthInputs(backlog, total, aiAvailable, age, healthWarn, analysisMinutes);
            bool ok = HealthCheck.IsOk(inputs);

            if (!ok)
                Console.WriteLine("[Health] not ok: backlog=" + backlog + " total=" + total
                    + " ai=" + aiAvailable + " age=" + age + " warn=" + healthWarn);

            await RunHealthWatchdogAsync(store, notifier, backlog, total, aiAvailable, age,
                healthWarn, healthAlertCooldownMinutes, healthDailySummary, analysisMinutes,
                client.Model, client.BaseUrl, ct);

            // Positive heartbeat. The health job used to log only when something was
            // wrong, so "everything is fine" and "this job died" looked identical in
            // the log. One throttled line a minute carries the health verdict plus the
            // receiver counters, which makes that distinction obvious at a glance.
            // The counters are deliberately NOT added to the diagnostics payload: the
            // Python endpoint does not expose them and that payload stays byte identical.
            if (DateTimeOffset.UtcNow - lastReceiverReport > TimeSpan.FromMinutes(1))
            {
                lastReceiverReport = DateTimeOffset.UtcNow;
                string heartbeat = "[Health] " + (ok ? "ok" : "NOT OK")
                    + " backlog=" + backlog + " total=" + total
                    + " ai=" + aiAvailable + " age=" + age
                    + " warn=" + healthWarn;
                if (LogAI.Web.Realtime.EngineIoServer.Current is { } eio)
                    heartbeat += " | realtime sessions=" + eio.SessionCount
                        + " connected=" + eio.ConnectedClients
                        + " queued=" + eio.QueuedPackets;
                if (LogAI.Web.Api.ReceiverState.Receiver is { } rcv)
                    heartbeat += " | [Receiver] udp=" + rcv.UdpReceived + " tcp=" + rcv.TcpReceived
                        + " stored=" + rcv.Stored + " dropped=" + rcv.Dropped
                        + " udp_bound=" + rcv.UdpBound + " tcp_bound=" + rcv.TcpBound;
                Console.WriteLine(heartbeat);
            }
        }, firstDelay: TimeSpan.FromSeconds(15));

        scheduler.Add("cleanup", () => TimeSpan.FromHours(1),
            async ct =>
            {
                var cleanup = await LogAI.Core.Scheduler.CleanupJob.RunAsync(
                    store, retentionHours, cancellationToken: ct);
                // Log the outcome even when nothing was removed: without a positive
                // line, "the job ran and found nothing" and "the job never ran" look
                // identical in the log, which is exactly how a data deletion can go
                // unexplained.
                Console.WriteLine("[Cleanup] removed " + cleanup.Removed + " expired logs, purged "
                    + cleanup.DeadPurged + " dead ids");

            },
            firstDelay: TimeSpan.FromMinutes(1));

        if (Environment.GetEnvironmentVariable("DOCKER_COLLECTION") != "off")
        {
            try
            {
                var docker = new DockerApi();
                var collector = new DockerCollector(docker, writer, () => ReadExclusions(settings));
                scheduler.Add("docker", () => TimeSpan.FromSeconds(30),
                    ct => collector.PollOnceAsync(ct), firstDelay: TimeSpan.FromSeconds(45));
            }
            catch (Exception)
            {
                // No docker socket: the rest of the application still runs.
            }
        }

        _ = scheduler.RunAsync(cancellationToken);

        Console.WriteLine("[AppHost] syslog udp=" + udpPort + " tcp=" + tcpPort
            + " | analysis every " + analysisMinutes + "m | retention " + retentionHours + "h");
    }

    /// <summary>
    /// 记录一次分析失败：一行 stdout（方便实时看）+ 一条 Redis 记录（方便事后查）。
    ///
    /// 为什么需要持久化：AI History 里 2026-10-03 13:48→18:07 有 4 小时 18 分的
    /// 空洞，而 stdout 已随容器重建消失，事后没有任何地方能说明那段时间发生了什么。
    /// 这类"历史里的空洞"必须由不随日志轮转消失的痕迹来解释。
    /// 列表用 LPUSH + LTRIM 限长，不会无限增长。
    /// </summary>
    private static async Task RecordAnalysisFailureAsync(RedisStore store, Exception ex, CancellationToken ct)
    {
        string reason = ex.GetType().Name + ": " + ex.Message;
        Console.WriteLine("[Analysis] FAILED " + reason);
        try
        {
            string entry = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK",
                System.Globalization.CultureInfo.InvariantCulture) + " " + reason;
            await store.Db.ListLeftPushAsync(Keys.AnalysisFailures, entry);
            await store.Db.ListTrimAsync(Keys.AnalysisFailures, 0, 99);
            // 失败记录保留 7 天：足够回溯"那天下午为什么没分析"，又不会永久堆积。
            await store.Db.KeyExpireAsync(Keys.AnalysisFailures, TimeSpan.FromDays(7));
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            // 记录失败本身不能把分析任务再弄挂一次
            Console.Error.WriteLine("[Analysis] 无法记录失败原因: " + logEx.Message);
        }
    }

    /// <summary>
    /// 巡检看门狗：把"积压 / AI 后端 / 调度卡死"三类异常推给 Telegram。
    ///
    /// 对齐 Python 的 health_watchdog，三点行为要一致：
    ///   1. **按条件各自冷却**（不是整体冷却）：AI 宕机不会因为刚发过积压告警而被吞掉；
    ///   2. **恢复通知**：条件消失时补一条"已恢复"，并清掉该条件的记录，
    ///      否则下次复发要等到冷却结束才通知；
    ///   3. **日报**：距上次日报 ≥24h 时发一条概览（时间戳落在 Redis，
    ///      容器重启不会重复发；此前那段代码在每个巡检周期都写这个键，
    ///      导致"距上次 ≥24h"永远不成立，日报永远不会发）。
    ///
    /// 冷却状态放在进程内（Python 同样是模块级字典），因此重启会重置冷却——
    /// 对一个巡检看门狗来说是安全的（宁可重启后多提醒一次，也不要静默）。
    /// </summary>
    private static async Task RunHealthWatchdogAsync(
        RedisStore store, TelegramNotifier notifier, long backlog, long total,
        bool aiAvailable, int ageSeconds, int warnThreshold, int cooldownMinutes,
        bool dailySummary, int analysisMinutes, string aiModel, string aiEndpoint,
        CancellationToken ct)
    {
        var conditions = new List<(string Key, string Message)>();
        if (backlog > warnThreshold)
            conditions.Add(("backlog", "⚠️ 未分析日志积压 <b>" + backlog + "</b> 条（阈值 " + warnThreshold + "），AI 分析跟不上"));
        if (!aiAvailable)
            conditions.Add(("ai", "⚠️ <b>AI 后端不可达</b>（模型 " + aiModel + " @ " + aiEndpoint + "）"));
        // Python: age > max(interval * 2.5, 300) 视为调度卡死
        long stallAfter = Math.Max((long)analysisMinutes * 150, 300);
        // 只有分析任务至少跑过一次（lastAnalysis 有值）才判定"调度卡死"：
        // 缓存年龄 age 在从未跑过时是 -1（Python 同样用 age >= 0 挡住这种误报）。
        if (ageSeconds >= 0 && ageSeconds > stallAfter)
            conditions.Add(("sched", "⚠️ 自动分析已 " + (ageSeconds / 60) + " 分钟未运行（疑似调度器卡死）"));

        // Telegram 凭据由 TelegramState 惰性加载（设置页优先，其次环境变量）。
        // 过滤器告警那条路径每次都会先 EnsureAsync，而巡检任务可能早于任何一次
        // 告警运行——以前这里直接查 Enabled，于是恒为 false，巡检永远只打一行
        // "(telegram 未配置)"，即使 token 早已配好。
        await TelegramState.EnsureAsync(store, ct);

        bool CanSend() => TelegramState.Enabled;
        async Task<bool> SendAsync(string text, string what)
        {
            if (!CanSend())
            {
                Console.WriteLine("[Health] " + what + " 未发送：Telegram 未配置");
                return false;
            }
            bool sent;
            try { sent = await notifier.SendAsync(TelegramState.BotToken, TelegramState.ChatId, text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine("[Health] " + what + " 发送异常: " + ex.Message);
                return false;
            }
            // 每个结果都留痕：没有这一行，"发送失败"和"看门狗根本没跑"在日志里
            // 长得一样——这正是之前几轮排查反复踩的坑。
            Console.WriteLine("[Health] " + what + (sent ? " 已发送" : " FAILED"));
            return sent;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var active = conditions.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, message) in conditions)
        {
            if (now - _healthLastAlert.GetValueOrDefault(key) < cooldownMinutes * 60)
            {
                Console.WriteLine("[Health] 条件 '" + key + "' 在 " + cooldownMinutes + " 分钟冷却内，跳过");
                continue;
            }
            if (await SendAsync("🚨 <b>LogAI Monitor 巡检告警</b>\n" + message, "巡检告警(" + key + ")"))
                _healthLastAlert[key] = now;
        }

        // 恢复通知：此前在告警、现在不在条件里的，逐个清掉并发一条
        foreach (string key in _healthLastAlert.Keys.ToList())
        {
            if (active.Contains(key)) continue;
            if (key == "backlog" && backlog > warnThreshold) continue;
            string what = key switch { "backlog" => "积压已回落到正常范围", "ai" => "AI 后端已恢复", _ => "调度已恢复" };
            await SendAsync("✅ <b>LogAI Monitor</b>\n" + what, "恢复通知(" + key + ")");
            _healthLastAlert.Remove(key);
        }

        if (!dailySummary) return;
        double lastSummary = 0;
        var raw = await store.Db.StringGetAsync(Keys.HealthLastSummary);
        if (raw.HasValue
            && double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            lastSummary = parsed;
        if (now - lastSummary < 24 * 3600) return;

        string aiIcon = aiAvailable ? "✅" : "⚠️";
        string ageText = ageSeconds >= 0 ? (ageSeconds / 60) + " 分钟前" : "尚未运行";
        if (await SendAsync("📊 <b>LogAI Monitor 巡检日报</b>\n"
                + "日志总量：<b>" + total + "</b>\n"
                + "未分析积压：" + backlog + "\n"
                + "AI 后端：" + aiIcon + " " + aiModel + "\n"
                + "最近自动分析：" + ageText, "巡检日报"))
        {
            await store.Db.StringSetAsync(Keys.HealthLastSummary,
                now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static readonly Dictionary<string, double> _healthLastAlert = new(StringComparer.Ordinal);

    private static async Task<List<FilterRule>> LoadFiltersAsync(RedisStore store)
    {
        var ids = await store.Db.SetMembersAsync(Keys.Filters);
        var rules = new List<FilterRule>(ids.Length);
        foreach (var id in ids)
        {
            var rule = await FilterLoader.LoadAsync(store, id.ToString());
            if (rule is not null && rule.Enabled) rules.Add(rule);
        }
        return rules;
    }

    private static IReadOnlyList<string> ReadExclusions(Dictionary<string, object?> settings)
    {
        if (settings.TryGetValue("docker_excluded_containers", out object? value) && value is List<object?> list)
            return list.Select(RedisStore.ToText).ToList();
        return [];
    }

    private static int IntSetting(Dictionary<string, object?> settings, string key, int fallback) =>
        int.TryParse(RedisStore.ToText(settings.GetValueOrDefault(key)), out int value) && value > 0 ? value : fallback;

    /// <summary>
    /// 与 IntSetting 相同，但允许 0。IntSetting 用 "> 0" 过滤，把合法的 0 也当成
    /// 无效值回退默认——health_backlog_warn 允许 0（Python 侧 max(0, …)，
    /// 含义是"任何积压都算超标"），用 IntSetting 读会让 0 变成 2000，设置形同虚设。
    /// </summary>
    private static int IntSettingAllowZero(Dictionary<string, object?> settings, string key, int fallback) =>
        int.TryParse(RedisStore.ToText(settings.GetValueOrDefault(key)), out int value) ? value : fallback;

    private static bool BoolSetting(Dictionary<string, object?> settings, string key, bool fallback)
    {
        string text = RedisStore.ToText(settings.GetValueOrDefault(key));
        return text.Length == 0 ? fallback : text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string TextSetting(Dictionary<string, object?> settings, string key, string fallback)
    {
        string text = RedisStore.ToText(settings.GetValueOrDefault(key));
        return text.Length > 0 ? text : fallback;
    }

    private static int IntEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
}
