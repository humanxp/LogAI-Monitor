// GET /api/docker/containers
//
// Six alphabetically ordered keys per container, with the Docker inspect State
// object passed through unchanged:
//   created id image name state status
//
// This is a DISPLAY endpoint: it lists running containers including the ones the
// collector excludes (logaimonitor, logaimonitor-redis) — the exclusion list
// governs ingestion, not this view.

using System.Net.Sockets;
using System.Text.Json.Nodes;
using LogAI.Core.Docker;

namespace LogAI.Web.Api;

internal static class DockerContainersApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/docker/containers", async () =>
        {
            using var docker = new DockerApi();
            List<ContainerInfo> containers;
            try
            {
                containers = await docker.ListContainersAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
            {
                return ReadApi.JsonBody(Array.Empty<object>());
            }

            var result = new List<object?>(containers.Count);
            foreach (var container in containers)
            {
                JsonNode? inspect = null;
                try
                {
                    inspect = await docker.InspectAsync(container.Id);
                }
                catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or System.Text.Json.JsonException)
                {
                    inspect = null;   // a container that vanished mid-request keeps its row
                }

                string created = inspect?["Created"]?.GetValue<string>() ?? "";
                // Flask serialises with sort_keys=True, so every level of the
                // state tree is emitted alphabetically; Docker inspect has its own
                // field order.
                JsonNode? state = SortKeys(inspect?["State"]);

                result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["created"] = created,
                    ["id"] = container.Id,
                    ["image"] = container.Image,
                    ["name"] = container.Name,
                    ["state"] = state,
                    ["status"] = container.State,
                });
            }
            return ReadApi.JsonBody(result);
        });
    }
    private static JsonNode? SortKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sorted[pair.Key] = SortKeys(pair.Value?.DeepClone());
                return sorted;
            case JsonArray array:
                var list = new JsonArray();
                foreach (JsonNode? item in array) list.Add(SortKeys(item?.DeepClone()));
                return list;
            default:
                return node?.DeepClone();
        }
    }
}
