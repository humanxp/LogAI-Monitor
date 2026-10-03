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

    public static void Map(WebApplication app)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(root)) root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

        app.MapGet("/static/{**path}", (string? path) =>
        {
            if (string.IsNullOrEmpty(path)) return Results.NotFound();

            string full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) return Results.NotFound();
            if (!File.Exists(full)) return Results.NotFound();

            string type = ContentTypes.TryGetValue(Path.GetExtension(full), out string? known)
                ? known
                : "application/octet-stream";
            return Results.File(full, type);
        });
    }
}
