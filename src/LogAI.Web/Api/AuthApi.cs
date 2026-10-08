// Login endpoint and session handling.
//
// The contract was taken from observed traffic rather than
// assumed, which corrected three things:
//   * the path is POST /login (form encoded); /api/login does not exist,
//   * the cookie is named "session",
//   * a failed attempt answers 200 with the login page (and a cleared cookie),
//     not 401.

using LogAI.Core.Auth;
using LogAI.Web;
using LogAI.Core.Store;
using LogAI.Web.Rendering;

namespace LogAI.Web.Api;

internal static class AuthApi
{
    public const string CookieName = "session";

    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies, JinjaEngine engine)
    {
        app.MapPost("/login", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            string username = form["username"].ToString();
            string password = form["password"].ToString();
            bool remember = form["remember"].ToString() is "on" or "true" or "1";

            var user = await AuthenticateAsync(store, username, password);
            if (user is null)
            {
                // The cookie is cleared and the page re-rendered with an error.
                http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
                return Results.Content(engine.Render("login.html", LoginScope("Invalid username or password")),
                                       "text/html; charset=utf-8");
            }

            var lifetime = remember ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12);
            http.Response.Cookies.Append(CookieName,
                cookies.Create(user.Value.Name, user.Value.Role, DateTimeOffset.UtcNow.Add(lifetime)),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });

            // Return to the page the visitor originally asked for, when it is a local path.
            string target = http.Request.Query["next"].ToString();
            return Results.Redirect(target.Length > 0 && target.StartsWith(char.Parse("/")) ? target : "/");
        });

        app.MapGet("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
            return Results.Redirect("/login");
        });
    }

    public static async Task<(string Name, string Role)?> AuthenticateAsync(RedisStore store, string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return null;

        var id = await store.Db.StringGetAsync(Keys.UserByName(username));
        if (!id.HasValue) return null;

        var record = await store.Db.HashGetAllAsync(id.ToString());
        if (record.Length == 0) return null;
        var stored = record.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

        if (!stored.TryGetValue("password_hash", out string? hash) || !WerkzeugPassword.Verify(password, hash))
            return null;

        return (stored.GetValueOrDefault("username") ?? username, stored.GetValueOrDefault("role") ?? "viewer");
    }

    public static SessionPayload? CurrentUser(HttpContext http, SessionCookie cookies) =>
        cookies.Verify(http.Request.Cookies[CookieName]);

    /// <summary>Context the login template needs when it is rendered after a failure.</summary>
    private static Scope LoginScope(string? error)
    {
        var scope = new Scope();
        scope.Set("title", "Login");
        scope.Set("version", "1.0.1");
        scope.Set("VERSION", "1.0.1");
        scope.Set("error", error);
        scope.Set("show_home_link", false);
        scope.Set("messages", new List<object?>());
        // The page flashes ("Invalid username or password", "error") and the
        // template renders it through get_flashed_messages(with_categories=true).
        scope.Set("get_flashed_messages", (Func<CallArgs, object?>)(_ => error is null
            ? new List<object?>()
            : new List<object?> { new object?[] { "error", error } }));
        scope.Set("request", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = "/login",
            ["cookies"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        });
        scope.Set("current_user", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["username"] = "", ["role"] = "viewer", ["is_admin"] = false, ["is_authenticated"] = false,
        });
        scope.Set("url_for", (Func<CallArgs, object?>)(call =>
        {
            string endpoint = ValueFormatter.ToText(call[0]);
            return endpoint == "static"
                ? "/static/" + ValueFormatter.ToText(call.Get("filename")) + "?v=" + StaticAssets.Version
                : "/" + endpoint;
        }));
        return scope;
    }
}
