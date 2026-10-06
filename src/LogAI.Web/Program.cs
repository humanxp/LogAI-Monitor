// LogAI Monitor — .NET 8 host.
//
// Stage 1 provides the page layer: the Jinja engine renders the existing
// templates against the context the pages expect
// supplies, and the static assets are served unchanged.  The REST API, syslog
// ingest, Redis store and scheduler are added in the following stages.
//
//   --render-dump <dir>   render every page to <dir> for byte comparison
//   (no argument)         run the web host

using LogAI.Web;
using LogAI.Web.Rendering;
using Microsoft.AspNetCore.ResponseCompression;

var pages = new (string Path, string Template, string Title)[]
{
    ("/", "index.html", "Dashboard"),
    ("/logs", "logs.html", "Log Entries"),
    ("/alerts", "alerts.html", "Alerts"),
    ("/filters", "filters.html", "Filters"),
    ("/ai-history", "ai_history.html", "AI History"),
    ("/clients", "clients.html", "Syslog Clients"),
    ("/docker", "docker.html", "Docker Logs"),
    ("/analysis", "analysis.html", "AI Analysis"),
    ("/settings", "settings.html", "Settings"),
    ("/users", "users.html", "Users"),
    ("/about", "about.html", "About"),
    ("/login", "login.html", "Login"),
};

string templateRoot = Path.Combine(AppContext.BaseDirectory, "templates");
if (!Directory.Exists(templateRoot))
    templateRoot = Path.Combine(Directory.GetCurrentDirectory(), "templates");

if (args.Length >= 1 && args[0] == "--parse-selftest")
{
    Environment.Exit(ParseSelfTest.Run());
}

if (args.Length >= 3 && args[0] == "--mint-session")
{
    // Verification helper: mints a valid cookie for a given role so the tier
    // checks can be exercised without knowing another account password.
    var mintSecret = System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("SECRET_KEY") ?? "development-secret-key");
    var minted = new LogAI.Core.Auth.SessionCookie(mintSecret);
    Console.WriteLine(minted.Create(args[1], args[2], DateTimeOffset.UtcNow.AddHours(1)));
    return;
}

if (args.Length >= 2 && args[0] == "--scrypt-gen")
{
    Console.WriteLine(LogAI.Core.Auth.WerkzeugPasswordGenerator.Generate(args[1]));
    Environment.Exit(0);
}

if (args.Length >= 3 && args[0] == "--scrypt-check")
{
    // signature is Verify(password, stored)
    Console.WriteLine(LogAI.Core.Auth.WerkzeugPassword.Verify(args[1], args[2]) ? "VALID" : "INVALID");
    Environment.Exit(0);
}

if (args.Length >= 1 && args[0] == "--scrypt-api")
{
    Environment.Exit(ScryptApiDump.Run());
}

if (args.Length >= 1 && args[0] == "--filter-load-selftest")
{
    Environment.Exit(await FilterLoadSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--client-tracker-selftest")
{
    Environment.Exit(await ClientTrackerSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--maintenance-selftest")
{
    Environment.Exit(await MaintenanceSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--docker-poll-selftest")
{
    Environment.Exit(await DockerPollSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--docker-selftest")
{
    Environment.Exit(await DockerSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--health-selftest")
{
    Environment.Exit(HealthSelfTest.Run());
}

if (args.Length >= 1 && args[0] == "--cleanup-selftest")
{
    Environment.Exit(await CleanupSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--scheduler-selftest")
{
    Environment.Exit(await SchedulerSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--runner-selftest")
{
    Environment.Exit(await RunnerSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--commit-selftest")
{
    Environment.Exit(await CommitSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--prompt-selftest")
{
    Environment.Exit(PromptSelfTest.Run());
}

if (args.Length >= 1 && args[0] == "--batch-selftest")
{
    Environment.Exit(await BatchSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--ai-selftest")
{
    Environment.Exit(await AiSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--ai-history-selftest")
{
    Environment.Exit(await AiHistorySelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--telegram-selftest")
{
    Environment.Exit(TelegramSelfTest.Run());
}

if (args.Length >= 1 && args[0] == "--alert-selftest")
{
    Environment.Exit(await AlertSelfTest.RunAsync());
}

if (args.Length >= 1 && args[0] == "--filter-selftest")
{
    Environment.Exit(FilterSelfTest.Run());
}

if (args.Length >= 1 && args[0] == "--json-selftest")
{
    Environment.Exit(JsonSelfTest.Run());
}

if (args.Length >= 1 && args[0] == "--session-selftest")
{
    Environment.Exit(AuthSelfTest.Run());
}

if (args.Length >= 3 && args[0] == "--verify-password")
{
    Environment.Exit(await PasswordCheck.RunAsync(args));
}

if (args.Length >= 1 && args[0] == "--ingest-selftest")
{
    Environment.Exit(await IngestSelfTest.RunAsync());
}

if (args.Length >= 3 && args[0] == "--ingest")
{
    Environment.Exit(await IngestRunner.RunAsync(args));
}

if (args.Length >= 2 && args[0] == "--parse-compare")
{
    Environment.Exit(ParseCompare.Run(args[1]));
}

if (args.Length >= 2 && args[0] == "--render-dump")
{
    RenderDump(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
// gzip 压缩静态资源与 JSON 响应：app.js(124KB)+socket.io(45KB)+CSS 压缩后 ~60KB，
// 每个页面导航都能少传 ~180KB，明显改善页面加载。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/javascript; charset=utf-8",
        "text/css; charset=utf-8",
    });
});
builder.Services.AddSingleton(new JinjaEngine(templateRoot));
var redisOptions = new LogAI.Core.Store.RedisOptions
{
    Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
    Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
    Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
    Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD"),
};
builder.Services.AddSingleton(new LogAI.Core.Store.RedisStore(redisOptions));

// 冷存储：老日志哈希落盘（SQLite）。路径须指向持久卷，否则容器重建归档就丢了。
var archivePath = Environment.GetEnvironmentVariable("LOG_ARCHIVE_PATH") ?? "/data/logai-archive.db";
builder.Services.AddSingleton(new LogAI.Core.Store.LogArchive(archivePath));

var app = builder.Build();

app.UseResponseCompression();

// Assets are served under /static (url_for('static', ...)),
// so the mount point has to match or every page renders unstyled.
StaticAssets.Map(app);

// Realtime channel (Engine.IO v4 long-polling, wire format captured).
var engineIo = new LogAI.Web.Realtime.EngineIoServer();


// Session cookies and the login endpoints.
var sessionSecret = Environment.GetEnvironmentVariable("SECRET_KEY") ?? "development-secret-key";
var sessionCookies = new LogAI.Core.Auth.SessionCookie(System.Text.Encoding.UTF8.GetBytes(sessionSecret));
LogAI.Web.Api.AuthApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies, app.Services.GetRequiredService<JinjaEngine>());

// The realtime channel is auth gated.
engineIo.Map(app, http => LogAI.Web.Api.AuthApi.CurrentUser(http, sessionCookies) is not null);

LogAI.Web.Api.CurrentUserApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.OllamaApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.StatsApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.DockerContainersApi.Map(app);
LogAI.Web.Api.DockerLogsApi.Map(app);
LogAI.Web.Api.LogWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.HealthApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.AlertWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.SettingsWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.CleanupApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.FilterWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.UserWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.HistoryWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.DiagnosticsApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.MiscWriteApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.ChatApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.AnalyzeApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies,
    new LogAI.Core.Ai.AiHistoryWriter(app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>()));

LogAI.Web.Api.ReadApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(),
    app.Services.GetRequiredService<LogAI.Core.Store.LogArchive>());
LogAI.Web.Api.UsersApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(), sessionCookies);
LogAI.Web.Api.AiHistoryApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.AiHistoryApi.MapList(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.AlertsApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.FilterApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
LogAI.Web.Api.IngestApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(),
    new LogAI.Core.Syslog.LogWriter(app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>(),
        int.TryParse(Environment.GetEnvironmentVariable("LOG_RETENTION_HOURS"), out int rh) ? rh : 720));

foreach (var page in pages)
{
    var current = page;
    app.MapGet(current.Path, (HttpContext http, JinjaEngine engine) =>
    {
        // Every page requires a signed-in user, mirroring login_required
        // decorator; anonymous visitors are sent to the login page.
        var user = LogAI.Web.Api.AuthApi.CurrentUser(http, sessionCookies);
        // The redirect carries the original page so the user
        // lands back where they were after signing in.
        // The login page must NOT be gated: redirecting it to itself produced a
        // loop ("/login?next=%2Flogin" repeated) and the form was unreachable, so
        // nobody could sign in through the browser at all.
        if (user is null && current.Path != "/login")
            return Results.Redirect("/login?next=" + Uri.EscapeDataString(current.Path));

        // /users is admin-only: a non-admin is sent
        // back to the dashboard instead of seeing the page.
        if (current.Path == "/users" && user.Role != "admin") return Results.Redirect("/");

        var scope = BuildScope(current.Title, current.Path, CookiesOf(http), true);
        scope.Set("current_user", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["username"] = user?.Username ?? "",
            ["role"] = user?.Role ?? "",
            ["is_admin"] = user?.Role == "admin",
            ["is_authenticated"] = user is not null,
        });
        return Results.Content(engine.Render(current.Template, scope), "text/html; charset=utf-8");
    });
}

app.MapFallback((HttpContext http, JinjaEngine engine) =>
{
    var scope = BuildScope("Page not found", http.Request.Path, CookiesOf(http), IsSignedIn(http));
    scope.Set("error_code", 404);
    scope.Set("details_title", "How to fix this");
    scope.Set("icon", "exclamation-triangle");
    scope.Set("icon_color", "#dc3545");
    scope.Set("details", new List<object?>());
    return Results.Content(engine.Render("error.html", scope), "text/html; charset=utf-8", statusCode: 404);
});

// Start the background subsystems: syslog receiver, scheduler, docker poll.
//
// 这里刻意不写成 `_ = AppHost.StartAsync(...)`：那样的 fire-and-forget 会把
// 异常直接丢掉。曾真实发生过——Redis 暂时不可达时 StartAsync 在读设置那一步就
// 抛异常，异常被静默吞掉，于是 syslog 接收、分析、巡检、清理**全都没有启动**，
// 而日志里一行提示都没有：进程活着、HTTP 还能响应，看起来"在跑"，实际已经完全
// 停止采集（表现为时间线不再增长，很容易几小时后才发现）。
//
// 现在的做法：先等 Redis 可达，再启动后台子系统。失败会持续重试并明确记录，
// 超过一定时间升级为醒目的警告，绝不静默。
_ = WaitForRedisThenStartAsync(app);

static async Task WaitForRedisThenStartAsync(WebApplication app)
{
    var store = app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>();
    var stopping = app.Lifetime.ApplicationStopping;
    int attempt = 0;

    while (!stopping.IsCancellationRequested)
    {
        try
        {
            // 用一次实际读取探活：AppHost.StartAsync 的第一步就是读设置，
            // 所以这一步成功意味着它也能成功，不会出现"半启动"。
            await store.GetSettingsAsync();
            break;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            attempt++;
            // 前 6 次逐次记录，其后每分钟一次，避免长时间故障刷屏。
            if (attempt <= 6 || attempt % 12 == 1)
            {
                Console.WriteLine("[AppHost] Redis 尚不可用（第 " + attempt + " 次重试，"
                    + (attempt <= 6 ? "5" : "60") + " 秒后重试）：" + ex.GetType().Name + ": " + ex.Message);
            }
            if (attempt == 20)
            {
                Console.WriteLine("[AppHost] 警告：Redis 已持续不可用约 5 分钟，"
                    + "后台子系统（采集/分析/巡检）**尚未启动**，期间不会有任何日志入库。");
            }
        }

        try { await Task.Delay(attempt <= 6 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60), stopping); }
        catch (OperationCanceledException) { return; }
    }

    if (stopping.IsCancellationRequested) return;

    try
    {
        // 正常情况下这个调用在进程存活期间不会返回（它内部跑调度循环），
        // 只在应用停止时随取消令牌退出。所以这里不做"启动成功"的输出——
        // 子系统是否真的起来了，由 AppHost 自己打印的 [AppHost] 标记说明。
        await LogAI.Web.AppHost.StartAsync(app, stopping);
        // 正常情况：StartAsync 只负责"搭建并启动"各子系统
        // （接收器与调度器都在内部以后台任务运行），随即返回。因此返回本身不是
        // 异常信号——子系统是否起来了，由它自己打印的 [AppHost] 标记说明。
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // 只有这里才是真问题：搭建阶段就失败（例如读设置出错），
        // 此时子系统根本没起来，必须显式说出来，否则就是又一次静默停摆。
        Console.WriteLine("[AppHost] 后台子系统启动失败（采集/分析/巡检不会运行）："
            + ex.GetType().FullName + ": " + ex.Message);
        Console.WriteLine(ex.ToString());
    }
}


// Bulk removal of log sources whose containers no longer exist. Used to clean up
// test containers that the Docker collector picked up. Prints what it would do
// unless --confirm is passed, and touches nothing outside the given prefixes.
if (args.Length >= 2 && args[0] == "--purge-sources")
{
    bool confirm = args.Contains("--confirm");
    var prefixes = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var purgeStore = new LogAI.Core.Store.RedisStore(new LogAI.Core.Store.RedisOptions
    {
        Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
        Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
        Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
    });
    var allSources = await purgeStore.Db.SetMembersAsync(LogAI.Core.Store.Keys.SourcesIndex);
    long totalLogs = 0;
    var targets = new List<string>();
    foreach (var value in allSources)
    {
        string source = value.ToString();
        if (!prefixes.Any(p => source.StartsWith(p, StringComparison.Ordinal))) continue;
        long count = await purgeStore.Db.SortedSetLengthAsync(LogAI.Core.Store.Keys.LogSource(source));
        targets.Add(source);
        totalLogs += count;
    }
    Console.WriteLine("[purge] prefixes: " + string.Join(", ", prefixes));
    Console.WriteLine("[purge] matching sources: " + targets.Count + ", logs involved: " + totalLogs);
    foreach (string source in targets.OrderBy(s => s, StringComparer.Ordinal))
        Console.WriteLine("   " + source);
    if (!confirm)
    {
        Console.WriteLine("[purge] dry run - pass --confirm to delete");
        Environment.Exit(0);
    }
    long removed = 0;
    foreach (string source in targets)
    {
        removed += await LogAI.Core.Store.LogMaintenance.DeleteBySourceAsync(purgeStore, source);
        await LogAI.Core.Store.LogMaintenance.DeleteClientAsync(purgeStore, source);
    }
    Console.WriteLine("[purge] deleted " + removed + " logs across " + targets.Count + " sources");
    Console.WriteLine("[purge] remaining sources: " + await purgeStore.Db.SetLengthAsync(LogAI.Core.Store.Keys.SourcesIndex));
    Environment.Exit(0);
}

// 一次性回填：给存量 ai_history 记录补上 status 分类字段（见 AiStatusClassifier），
// 让 /api/ai-history/stats 不必再读 + 解析整份 analysis JSON。幂等：重复跑无害。
if (args.Length >= 1 && args[0] == "--backfill-ai-status")
{
    var bfStore = new LogAI.Core.Store.RedisStore(new LogAI.Core.Store.RedisOptions
    {
        Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
        Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
        Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
    });
    var bfIds = await bfStore.Db.SortedSetRangeByRankAsync(LogAI.Core.Store.Keys.AiHistoryTimeline, 0, -1);
    int bfUpdated = 0;
    const int BfChunk = 500;
    for (int offset = 0; offset < bfIds.Length; offset += BfChunk)
    {
        int size = Math.Min(BfChunk, bfIds.Length - offset);
        var readBatch = bfStore.Db.CreateBatch();
        var reads = new Task<StackExchange.Redis.RedisValue[]>[size];
        for (int i = 0; i < size; i++)
            reads[i] = readBatch.HashGetAsync(bfIds[offset + i].ToString(), ["type", "analysis"]);
        readBatch.Execute();
        var loaded = await Task.WhenAll(reads);

        var writeBatch = bfStore.Db.CreateBatch();
        var writes = new List<Task>(size);
        for (int i = 0; i < size; i++)
        {
            string status = LogAI.Core.Ai.AiStatusClassifier.Classify(
                loaded[i][0].ToString(), loaded[i][1].ToString());
            writes.Add(writeBatch.HashSetAsync(bfIds[offset + i].ToString(), "status", status));
            bfUpdated++;
        }
        writeBatch.Execute();
        await Task.WhenAll(writes);
    }
    Console.WriteLine("[backfill] set status on " + bfUpdated + " ai_history records");
    Environment.Exit(0);
}
app.Run();

// --------------------------------------------------------------- helpers

static Dictionary<string, string> CookiesOf(HttpContext http)
{
    var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var cookie in http.Request.Cookies) cookies[cookie.Key] = cookie.Value;
    return cookies;
}

static bool IsSignedIn(HttpContext http) => http.User?.Identity?.IsAuthenticated == true;

static Scope BuildScope(string title, string path, Dictionary<string, string> cookies, bool authenticated)
{
    var scope = new Scope();
    scope.Set("title", title);
    scope.Set("version", Version);
    scope.Set("VERSION", Version);
    scope.Set("show_home_link", path != "/");

    // request.path / request.cookies.get('sidebar_collapsed')
    var jar = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var (key, value) in cookies) jar[key] = value;
    scope.Set("request", new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["path"] = path,
        ["cookies"] = jar,
    });

    scope.Set("current_user", new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["username"] = authenticated ? "admin" : "",
        ["role"] = authenticated ? "admin" : "viewer",
        ["is_admin"] = authenticated,
        ["is_authenticated"] = authenticated,
    });

    // No flash messages are pending on a fresh page load.
    scope.Set("get_flashed_messages", (Func<CallArgs, object?>)(_ => new List<object?>()));
    scope.Set("messages", new List<object?>());

    scope.Set("url_for", (Func<CallArgs, object?>)(call =>
    {
        string endpoint = ValueFormatter.ToText(call[0]);
        return endpoint == "static"
            ? "/static/" + ValueFormatter.ToText(call.Get("filename"))
            : "/" + endpoint;
    }));

    return scope;
}

void RenderDump(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var engine = new JinjaEngine(templateRoot);
    var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var page in pages)
    {
        string html = engine.Render(page.Template, BuildScope(page.Title, page.Path, cookies, authenticated: true));
        string name = page.Path == "/" ? "index" : page.Path.Trim('/').Replace('/', '_');
        File.WriteAllText(Path.Combine(outputDirectory, name + ".html"), html);
        Console.WriteLine($"rendered {page.Template,-18} -> {name}.html  {html.Length} bytes");
    }
}

partial class Program
{
    internal const string Version = "1.1.0";
}
