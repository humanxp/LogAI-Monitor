// GET /api/docker/containers/<id>/logs?lines=N
//
// Returns a BARE ARRAY of log lines (the collector's
// list directly, it is not wrapped in {"logs":...}), with lines defaulting to 100.
// A missing docker socket yields an empty array rather than an error, so the page
// renders "no logs" instead of a failure.

using LogAI.Core.Docker;

namespace LogAI.Web.Api;

internal static class DockerLogsApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/docker/containers/{containerId}/logs", async (HttpContext http, string containerId) =>
        {
            // 同样要有上限：lines 请求多少就向 Docker 要多少行并全部读进内存，
            // ?lines=999999999 等于把某个容器的全部日志拉进进程。
            int lines = 100;
            if (int.TryParse(http.Request.Query["lines"], out int requested) && requested > 0)
                lines = Math.Min(requested, 5000);

            try
            {
                using var docker = new DockerApi();
                return ReadApi.JsonBody(await docker.LogsAsync(containerId, lines));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                          or System.Net.Sockets.SocketException
                                          or System.Text.Json.JsonException)
            {
                return ReadApi.JsonBody(Array.Empty<string>());
            }
        });
    }
}
