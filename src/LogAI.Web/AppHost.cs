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
    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var store = app.Services.GetRequiredService<RedisStore>();
        var settings = await store.GetSettingsAsync();

        int retentionHours = IntSetting(settings, "log_retention_hours", 720);
        int analysisMinutes = Math.Max(1, IntSetting(settings, "analysis_interval", 2));
        int batchSize = IntSetting(settings, "batch_size", 500);
        int cooldownMinutes = IntSetting(settings, "alert_cooldown_minutes", 5);
        int warnThreshold = IntSetting(settings, "analysis_warn_threshold", 2000);
        bool alertOnCritical = BoolSetting(settings, "alert_on_critical", true);
        bool alertOnError = BoolSetting(settings, "alert_on_error", true);

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

            foreach (var rule in await LoadFiltersAsync(store))
            {
                if (!FilterMatcher.Matches(rule, entry.Source, entry.Severity, entry.Message)) continue;

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

                if (!FilterMatcher.TelegramAllowed(rule, entry.Severity, alertOnCritical, alertOnError)) continue;
                if (!await alerts.AcquireCooldownAsync(entry.Hostname, rule.Id, cooldownMinutes)) continue;
                if (!await TelegramState.EnsureAsync(store, cancellationToken)) continue;

                await notifier.SendAsync(TelegramState.BotToken, TelegramState.ChatId,
                    TelegramNotifier.BuildAlertText(entry.Severity, entry.Source, entry.Message, entry.Hostname),
                    cancellationToken);
            }
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
                    if (outcome.Status == "analyzed")
                    {
                        LogAI.Web.Api.StatsApi.PushIfNeeded(store);
                        // The dashboard reads data.logs_analyzed and renders data.analysis
                        // (Python emitted exactly those two keys). Sending count/history_id
                        // alone made the toast read "Analyzed undefined logs" and left an
                        // automatic analysis invisible on the page.
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
                    }
                }
                finally { lastAnalysis = DateTimeOffset.UtcNow; }
            },
            firstDelay: TimeSpan.FromSeconds(20));

        scheduler.Add("health", () => TimeSpan.FromSeconds(30), async ct =>
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
            HealthState.WarnThreshold = warnThreshold;
            HealthState.AnalysisMinutes = analysisMinutes;

            var inputs = new HealthInputs(backlog, total, aiAvailable, age, warnThreshold, analysisMinutes);
            bool ok = HealthCheck.IsOk(inputs);

            // Python stores the epoch of the last summary under this key (a float).
            await store.Db.StringSetAsync(Keys.HealthLastSummary,
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!ok)
                Console.WriteLine("[Health] not ok: backlog=" + backlog + " total=" + total
                    + " ai=" + aiAvailable + " age=" + age + " warn=" + warnThreshold);

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
                    + " warn=" + warnThreshold;
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
            ct => CleanupJob.RunAsync(store, retentionHours, cancellationToken: ct),
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
