// Polls container logs into the log store.
//
// The one thing that must not go wrong here is duplication: every poll re-reads
// the tail, so without a marker the same lines are ingested again and again and
// the log volume grows quadratically. The marker is the last line ingested per
// container; the next poll resumes right after it.
//
// If the marker is no longer in the tail (the container is very chatty and the
// window moved past it) the poll re-reads the whole tail. That trades a few
// duplicates for never silently dropping lines — a gap in the log is worse than
// a repeated line, and this is stated here because it is a deliberate choice.

using LogAI.Core.Store;
using LogAI.Core.Syslog;

namespace LogAI.Core.Docker;

public sealed class DockerCollector(DockerApi api, LogWriter writer, Func<IReadOnlyList<string>> exclusions)
{
    private readonly Dictionary<string, string> _marker = new(StringComparer.Ordinal);

    public int TailLines { get; init; } = 200;

    /// <summary>Reads every non-excluded container once; returns how many lines were stored.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var containers = DockerApi.ApplyExclusions(await api.ListContainersAsync(cancellationToken: cancellationToken),
                                                   exclusions());
        int stored = 0;

        foreach (var container in containers)
        {
            var lines = await api.LogsAsync(container.Id, TailLines, cancellationToken);
            if (lines.Count == 0) continue;

            int start = 0;
            if (_marker.TryGetValue(container.Id, out string? previous))
            {
                int index = lines.LastIndexOf(previous);
                start = index >= 0 ? index + 1 : 0;
            }

            for (int i = start; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                await writer.StoreAsync(new SyslogEntry
                {
                    // Same convention as the Python collector: docker:<container>.
                    Source = $"docker:{container.Name}",
                    SourceType = "docker",
                    Hostname = "docker-host",
                    Program = container.Name,
                    Facility = "docker",
                    Severity = "info",
                    Message = line.Length <= 20000 ? line : line[..20000],
                    Timestamp = SyslogParser.NowTimestamp(),
                }, cancellationToken);
                stored++;
            }

            _marker[container.Id] = lines[^1];
        }

        return stored;
    }

    public IReadOnlyDictionary<string, string> Markers => _marker;
}
