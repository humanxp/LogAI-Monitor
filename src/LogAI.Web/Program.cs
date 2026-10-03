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
builder.Services.AddSingleton(new JinjaEngine(templateRoot));
var redisOptions = new LogAI.Core.Store.RedisOptions
{
    Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
    Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
    Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
    Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD"),
};
builder.Services.AddSingleton(new LogAI.Core.Store.RedisStore(redisOptions));
var app = builder.Build();

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

LogAI.Web.Api.ReadApi.Map(app, app.Services.GetRequiredService<LogAI.Core.Store.RedisStore>());
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
_ = LogAI.Web.AppHost.StartAsync(app, app.Lifetime.ApplicationStopping);


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
    internal const string Version = "1.0.1";
}
