// Minimal Docker Engine client over the unix socket.
//
// The collector talks to the Docker Engine API over a read-only socket mount; the same
// three endpoints are all this needs:
//   GET /containers/json          running containers
//   GET /containers/{id}/json     inspect (raw State object)
//   GET /containers/{id}/logs     stdout+stderr tail
//
// Container names come back with a leading slash ("/logaimonitor") while the
// exclusion list stores them without, so both sides are normalised before the
// comparison - getting that wrong silently disables the exclusion and the
// collector starts ingesting its own log lines.
//
// Created is kept as the RAW Docker string: it carries nanosecond precision
// ("2026-09-24T23:06:41.82329016Z") and re-formatting it through a DateTime type
// changes the digits, which would break the timestamp contract clients rely on.

using System.Text;
using System.Text.Json.Nodes;

namespace LogAI.Core.Docker;

public sealed record ContainerInfo(string Id, string Name, string Image, string State, string Status, string CreatedRaw);

public sealed class DockerApi : IDisposable
{
    private readonly HttpClient _http;

    public DockerApi(string socketPath = "/var/run/docker.sock")
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<ContainerInfo>> ListContainersAsync(bool includeStopped = false,
                                                              CancellationToken cancellationToken = default)
    {
        string body = await _http.GetStringAsync($"/containers/json?all={(includeStopped ? 1 : 0)}", cancellationToken);
        var containers = new List<ContainerInfo>();
        foreach (JsonNode? node in JsonNode.Parse(body) as JsonArray ?? [])
        {
            if (node is not JsonObject container) continue;
            string name = (container["Names"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "";
            string fullId = container["Id"]?.GetValue<string>() ?? "";
            containers.Add(new ContainerInfo(
                Id: fullId.Length >= 12 ? fullId[..12] : fullId,
                Name: name.TrimStart('/'),
                Image: container["Image"]?.GetValue<string>() ?? "",
                State: container["State"]?.GetValue<string>() ?? "",
                Status: container["Status"]?.GetValue<string>() ?? "",
                // The list endpoint reports Created as an integer epoch; the
                // ISO string comes from inspect (see the API).
                CreatedRaw: ""));
        }
        return containers;
    }

    /// <summary>
    /// The full inspect object. Called for Created as well as State, because
    /// /containers/json reports Created as an integer epoch while inspect returns
    /// the nanosecond ISO string the API contract serves.
    /// </summary>
    public async Task<JsonNode?> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        string body = await _http.GetStringAsync($"/containers/{containerId}/json", cancellationToken);
        return JsonNode.Parse(body);
    }

    /// <summary>Tail of a container's stdout/stderr, one entry per line.</summary>
    /// <summary>
    /// Recent container output, split the way the collector expects it.
    ///
    /// container.logs(tail=N, timestamps=True) demultiplexes
    /// the stream - then decodes and splits on "\n" WITHOUT dropping empty
    /// entries. Each line ends with a newline, so the array always carries a
    /// trailing "": tail=5 yields 6 items. Reproducing that matters because the
    /// page renders the array as-is, and the timestamps prefix every line with
    /// Docker's RFC3339 nanosecond stamp.
    /// </summary>
    public async Task<List<string>> LogsAsync(string containerId, int tail = 100,
                                              CancellationToken cancellationToken = default)
    {
        string body = await _http.GetStringAsync(
            $"/containers/{containerId}/logs?stdout=1&stderr=1&tail={tail}&timestamps=1", cancellationToken);

        // Demultiplex: each frame is an 8 byte header (stream, then a big-endian
        // length) followed by the payload. A stream that is not multiplexed (a
        // TTY container) starts with ordinary text, so the header check fails and
        // the raw body is used instead.
        var payload = new StringBuilder();
        int index = 0;
        while (index + 8 <= body.Length)
        {
            if (body[index] is not (char)1 and not (char)2) { payload.Clear(); break; }
            int size = (body[index + 4] << 24) | (body[index + 5] << 16) | (body[index + 6] << 8) | body[index + 7];
            index += 8;
            if (size <= 0 || index + size > body.Length) { payload.Clear(); break; }
            payload.Append(body, index, size);
            index += size;
        }

        string text = payload.Length > 0 ? payload.ToString() : body;
        return text.Split('\n').ToList();
    }


    /// <summary>Drops the collector's own containers (name compared without a leading slash).</summary>
    public static List<ContainerInfo> ApplyExclusions(IEnumerable<ContainerInfo> containers,
                                                      IEnumerable<string> excluded) =>
        containers.Where(c => !excluded.Any(x =>
            string.Equals(x.TrimStart('/'), c.Name, StringComparison.Ordinal))).ToList();

    public void Dispose() => _http.Dispose();
}
