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

    /// <summary>单批分析量的默认值（设置页与环境变量都没有给值时使用）。</summary>
    internal const int DefaultAnalysisBatch = 500;

    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var store = app.Services.GetRequiredService<RedisStore>();
        var archive = app.Services.GetRequiredService<LogArchive>();
        var settings = await store.GetSettingsAsync();

        // 保留期的唯一来源是设置页的 log_retention_hours（默认 720h=30 天）。
        // 注意：环境变量 LOG_RETENTION_HOURS 对主采集路径不生效——它只在
        // Program.cs 里被 HTTP ingest 旁路的 LogWriter 读取。两个来源曾同时
        // 存在且取值不同（settings=720 vs 部署脚本 -e LOG_RETENTION_HOURS=12），
        // 造成"部署写着 12 小时、实际留 30 天"的困惑；部署脚本里的死配置已移除。
        int retentionHours = IntSetting(settings, "log_retention_hours", 720);
        // 冷热分层：超过该小时数的日志哈希搬到 SQLite，Redis 只留 ZSET 索引。
        // 默认 168h=7 天；0 表示关闭归档。与保留期同理，每次使用前重读设置。
        int archiveAfterHours = IntSettingAllowZero(settings, "archive_after_hours", 168);
        int analysisMinutes = Math.Max(1, IntSetting(settings, "analysis_interval", 2));
        // 单批分析量的取值来源必须唯一，否则会出现"设置页改了却不起作用"：
        //   * 设置页的输入框（Logs per Analysis Run）读写的是 max_logs_per_analysis，
        //     并被校验在 10..5000；
        //   * MAX_LOGS_PER_ANALYSIS 环境变量作为初始默认值；
        //   * 旧键 batch_size 只作为兼容回退（历史上曾是这个键，且没有界面）。
        // 过去这里只读 batch_size，于是界面上改批次大小完全无效——用户看到的
        // "500 一次"其实来自一个从未被读取的环境变量，"1000"来自残留的旧键。
        int batchSize = MaxAnalysisBatch;
        foreach (string key in new[] { "max_logs_per_analysis", "batch_size" })
        {
            int configured = IntSettingAllowZero(settings, key, 0);
            if (configured > 0) { batchSize = configured; break; }
        }
        if (batchSize <= 0)
        {
            batchSize = IntEnv("MAX_LOGS_PER_ANALYSIS", DefaultAnalysisBatch);
        }
        batchSize = Math.Min(batchSize, MaxAnalysisBatch);

        // 两个开关此前只在设置页里存在，代码从不读取：勾掉"启用 AI 分析"或
        // "自动分析"都不会有任何效果。这里定义成"每次使用前重读设置"的函数，
        // 而不是启动时取一次的快照——否则改完设置必须重启容器才生效。
        bool AutoAnalyzeEnabled() =>
            BoolSetting(store.GetSettingsAsync().GetAwaiter().GetResult(), "auto_analyze", true);
        bool AiEnabled() => AiClient.AiEnabledIn(store.GetSettingsAsync().GetAwaiter().GetResult());

        Console.WriteLine("[AppHost] 开关：自动分析=" + (AutoAnalyzeEnabled() ? "启用" : "停用")
            + "，AI 分析=" + (AiEnabled() ? "启用" : "停用") + "（改动即时生效，无需重启）");

        // 每批真正送给模型的样本行数（设置页的 "AI Sample Limit per Batch"）。
        // 这个键此前只存在于设置白名单里，代码从未读取，于是界面上写着"按级别
        // 排序取 N 行"，实际却是整批全部灌进提示词。0/缺省表示不限制。
        int sampleLimit = IntSettingAllowZero(settings, "batch_sample_limit", 200);
        if (sampleLimit < 0) sampleLimit = 0;

        // batch_size 是与设置页不同名的旧键。两边都有值时说明配置已经分叉，
        // 明确记一行，避免以后又出现"改了设置不知道哪个生效"。
        int legacyBatchSize = IntSettingAllowZero(settings, "batch_size", 0);
        int uiBatchSize = IntSettingAllowZero(settings, "max_logs_per_analysis", 0);
        if (legacyBatchSize > 0 && uiBatchSize > 0 && legacyBatchSize != uiBatchSize)
        {
            Console.WriteLine("[AppHost] batch 配置分叉：max_logs_per_analysis=" + uiBatchSize
                + "（设置页，生效）与旧键 batch_size=" + legacyBatchSize
                + " 不一致，已按设置页取值。建议删除旧键以免混淆。");
        }
        Console.WriteLine("[AppHost] 每批分析 " + batchSize + " 条，其中按级别优先送模型 "
            + (sampleLimit > 0 ? sampleLimit + " 条" : "全部") + "（来源："
            + (uiBatchSize > 0 ? "设置页 max_logs_per_analysis"
               : legacyBatchSize > 0 ? "旧键 batch_size" : "环境变量/默认值") + "）");
        // 冷却时间同样只能有一个来源：设置页的 "Notification Cooldown" 写的是
        // telegram_cooldown_minutes，而这里过去读 alert_cooldown_minutes——该键在库里
        // 根本不存在，于是永远落到默认 5 分钟，界面上填 60 也不生效。
        // 现在以设置页的键为准，旧键仅作兼容回退，默认值不变。
        int cooldownMinutes = IntSettingAllowZero(settings, "telegram_cooldown_minutes", -1);
        if (cooldownMinutes < 0) cooldownMinutes = IntSetting(settings, "alert_cooldown_minutes", 5);
        if (cooldownMinutes > 1440) cooldownMinutes = 1440;
        int warnThreshold = IntSetting(settings, "analysis_warn_threshold", 2000);
        bool alertOnCritical = BoolSetting(settings, "alert_on_critical", true);
        bool alertOnError = BoolSetting(settings, "alert_on_error", true);

        // 巡检看门狗：积压 / AI 后端 / 调度卡死
        // 三种条件各自按冷却时间推送 Telegram，恢复正常时补一条"已恢复"，
        // 并有可选的巡检日报。这些设置键此前
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

        // First-run bootstrap (ensure_admin_exists): a fresh
        // deployment has no user at all, so without this nobody could ever sign in.
        // Idempotent: it only acts when no account with role=admin exists.
        bool hasAdmin = false;
        var userIds = await store.Db.SetMembersAsync("users:all");
        var userHashes = await store.HashGetAllBatchAsync(userIds);
        for (int i = 0; i < userIds.Length && !hasAdmin; i++)
            foreach (var f in userHashes[i])
                if (f.Name == "role" && f.Value == "admin") { hasAdmin = true; break; }
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

            // 过滤器用进程内短 TTL 缓存(见 LoadFiltersAsync):规则集变化频率极低,
            // 而这里每条日志都会读一遍。同一进程里的过滤写接口会主动失效缓存
            // (改完立即生效);直接改 Redis 的外部写入最多 2 秒后生效——与
            // /api/stats 的可用性探测、筛选交集缓存同一哲学。
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
            Model = AiClient.ResolveModel(store),
            ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
        };

        // 这两个值在 AnalysisRunner 里每轮都会重新读取（设置页写着"立即生效"），
        // 这里传入的只是读取失败时的兜底与启动日志的展示值。
        var runner = new AnalysisRunner(store, client, new AiHistoryWriter(store), batchSize, sampleLimit);
        var scheduler = new JobScheduler(TimeSpan.FromSeconds(10));

        // The health check reports the age of the last analysis RUN, not of the
        // last stored analysis: that timestamp is updated even
        // when a run analyses nothing, so an idle queue is not mistaken for a
        // stalled analyser.
        DateTimeOffset? lastAnalysis = null;
        // MinValue so the first health run reports immediately, then once a minute.
        DateTimeOffset lastReceiverReport = DateTimeOffset.MinValue;
        bool aiAvailable = false;
        DateTimeOffset? aiChecked = null;

        scheduler.Add("analysis",
            // 关闭"自动分析"时返回 null，调度器即跳过该任务——间隔每次 tick 重新
            // 求值，所以勾选框改完立即生效，不需要重启。手动分析不受此开关影响。
            () => AutoAnalyzeEnabled() ? TimeSpan.FromMinutes(analysisMinutes) : null,
            async ct =>
            {
                try
                {
                    // 双重判定：间隔提供者可能在本轮开始后才发现开关被改，
                    // 这里再确认一次，避免"刚关掉还跑一轮"。
                    if (!AutoAnalyzeEnabled())
                    {
                        Console.WriteLine("[Analysis] 自动分析已关闭，跳过本轮");
                        return;
                    }
                    if (!AiEnabled())
                    {
                        Console.WriteLine("[Analysis] AI 分析总开关已关闭，跳过本轮（不调用模型）");
                        return;
                    }
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
                        // (those two keys are the contract). Sending count/history_id
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

                        // The summary is sent to Telegram whenever an analysis comes
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

        // 巡检周期由 health_watch_minutes 决定（在启动时
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
            // endpoint does not expose them and that payload stays byte identical.
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
                    store, retentionHours, archive: archive, cancellationToken: ct);
                // Log the outcome even when nothing was removed: without a positive
                // line, "the job ran and found nothing" and "the job never ran" look
                // identical in the log, which is exactly how a data deletion can go
                // unexplained.
                Console.WriteLine("[Cleanup] removed " + cleanup.Removed + " expired logs, purged "
                    + cleanup.DeadPurged + " dead ids");

            },
            firstDelay: TimeSpan.FromMinutes(1));

        // 冷热分层归档：把超过 archive_after_hours 的日志哈希搬到 SQLite。间隔固定
        // 5 分钟；阈值每次重读设置（改成 0 即停用，用于回退）。首轮会追平历史存量，
        // 之后每轮只处理"新变老"的一小段。
        scheduler.Add("archive", () => TimeSpan.FromMinutes(5),
            async ct =>
            {
                int hours = IntSettingAllowZero(await store.GetSettingsAsync(), "archive_after_hours", 168);
                if (hours <= 0)
                {
                    Console.WriteLine("[Archive] disabled (archive_after_hours=0)");
                    return;
                }
                var result = await LogAI.Core.Scheduler.LogArchiveJob.RunAsync(
                    store, archive, hours, cancellationToken: ct);
                Console.WriteLine("[Archive] archived " + result.Archived + " log(s), watermark="
                    + result.Watermark.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            },
            firstDelay: TimeSpan.FromSeconds(30));

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
    /// 三点行为：
    ///   1. **按条件各自冷却**（不是整体冷却）：AI 宕机不会因为刚发过积压告警而被吞掉；
    ///   2. **恢复通知**：条件消失时补一条"已恢复"，并清掉该条件的记录，
    ///      否则下次复发要等到冷却结束才通知；
    ///   3. **日报**：距上次日报 ≥24h 时发一条概览（时间戳落在 Redis，
    ///      容器重启不会重复发；此前那段代码在每个巡检周期都写这个键，
    ///      导致"距上次 ≥24h"永远不成立，日报永远不会发）。
    ///
    /// 冷却状态放在进程内，因此重启会重置冷却——
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
        // age > max(interval * 2.5, 300) 视为调度卡死
        long stallAfter = Math.Max((long)analysisMinutes * 150, 300);
        // 只有分析任务至少跑过一次（lastAnalysis 有值）才判定"调度卡死"：
        // 缓存年龄 age 在从未跑过时是 -1（用 age >= 0 挡住这种误报）。
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

    // ---- 过滤器规则缓存 -------------------------------------------------
    // 规则集每条日志读一遍(OnStored),但变化频率极低:进程内缓存 + 2 秒 TTL,
    // 写接口(同进程的 FilterWriteApi)在每次写后主动失效,界面改完立即生效;
    // 外部直接改 Redis 的场景最多等一个 TTL。规则对象只读不改,缓存共享安全。
    private static readonly object FilterCacheLock = new();
    private static List<FilterRule>? _filterCache;
    private static DateTimeOffset _filterCacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan FilterCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>过滤写接口改动了规则集之后调用,让下一条日志立即用上新规则。</summary>
    internal static void InvalidateFilterCache()
    {
        lock (FilterCacheLock) { _filterCache = null; }
    }

    private static async Task<List<FilterRule>> LoadFiltersAsync(RedisStore store)
    {
        List<FilterRule>? cached;
        lock (FilterCacheLock)
        {
            cached = _filterCache;
            if (cached is not null
                && DateTimeOffset.UtcNow - _filterCacheAt < FilterCacheTtl)
                return cached;
        }

        var ids = await store.Db.SetMembersAsync(Keys.Filters);
        // 规则哈希一批取回:原来逐条 HGETALL,规则一多每条日志都要串行等一遍。
        var hashes = await store.HashGetAllBatchAsync(ids);
        var rules = new List<FilterRule>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            if (hashes[i].Length == 0) continue;
            var rule = FilterLoader.FromHash(ids[i].ToString(), hashes[i]);
            if (rule is not null && rule.Enabled) rules.Add(rule);
        }

        lock (FilterCacheLock)
        {
            _filterCache = rules;
            _filterCacheAt = DateTimeOffset.UtcNow;
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
    /// 无效值回退默认——health_backlog_warn 允许 0（语义是 max(0, …)，
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
