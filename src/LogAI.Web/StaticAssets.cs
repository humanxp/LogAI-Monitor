// Static assets under /static, matching url_for('static', filename=...) in the
// templates.
//
// Served through an explicit endpoint rather than UseStaticFiles so the URL
// prefix matches, no extra package is needed, and the path is checked against
// traversal before anything is read from disk.

namespace LogAI.Web;

internal static class StaticAssets
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".eot"] = "application/vnd.ms-fontobject",
        [".map"] = "application/json; charset=utf-8",
    };

    /// <summary>
    /// 缓存破坏令牌：进程启动时取一个时间戳。容器每次部署都重建（=新进程），
    /// 于是模板里 /static/...?v=&lt;token&gt; 会变，浏览器立刻拉新文件；进程存活期间
    /// 令牌不变，仍享受下面的 max-age 缓存、不会每页导航都回源 304。
    /// 这正是"部署即生效 + 平常零回源"的两全，补上原来只设 5 分钟缓存、
    /// 部署后用户要等缓存过期才能看到新界面的坑。
    /// </summary>
    public static readonly string Version = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    public static void Map(WebApplication app)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(root)) root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

        app.MapGet("/static/{**path}", async (string? path, HttpContext http) =>
        {
            if (string.IsNullOrEmpty(path)) return Results.NotFound();

            string full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) return Results.NotFound();
            if (!File.Exists(full)) return Results.NotFound();

            string type = ContentTypes.TryGetValue(Path.GetExtension(full), out string? known)
                ? known
                : "application/octet-stream";
            // 静态资源(js/css)每次部署才变：5 分钟缓存，避免每页导航都回源校验(304)。
            // 用内存字节返回(而非 Results.File 的 sendfile)，好让 gzip 中间件压缩。
            http.Response.Headers.CacheControl = "public, max-age=300";
            byte[] bytes = await File.ReadAllBytesAsync(full);
            return Results.Bytes(bytes, type);
        });
    }
}
