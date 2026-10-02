// Talks to the real Docker daemon and exercises the exclusion rule.

using LogAI.Core.Docker;
using LogAI.Core.Store;

namespace LogAI.Web;

internal static class DockerSelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        using var docker = new DockerApi();
        List<ContainerInfo> containers;
        try
        {
            containers = await docker.ListContainersAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot reach the docker socket: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"running containers: {containers.Count}");
        foreach (var c in containers)
            Console.WriteLine($"  {c.Name,-24} {c.Image,-24} {c.Status}");

        Check("listed at least one container", containers.Count > 0, containers.Count.ToString());
        Check("names have no leading slash", containers.All(c => !c.Name.StartsWith('/')),
            string.Join(",", containers.Select(c => c.Name)));
        Check("ids are short form", containers.All(c => c.Id.Length == 12), containers[0].Id);

        // Exclusion rule, read from the deployment's own settings.
        using var store = new RedisStore(new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = 0,
        });
        var settings = await store.GetSettingsAsync();
        var excluded = (settings.GetValueOrDefault("docker_excluded_containers") as List<object?> ?? [])
            .Select(v => RedisStore.ToText(v)).ToList();
        Console.WriteLine($"excluded from settings: [{string.Join(", ", excluded)}]");

        var kept = DockerApi.ApplyExclusions(containers, excluded);
        Console.WriteLine($"after exclusions: {kept.Count} of {containers.Count}");
        Check("excluded containers are filtered out",
            kept.All(c => !excluded.Contains(c.Name)) && kept.Count <= containers.Count,
            string.Join(",", kept.Select(c => c.Name)));

        // The exclusion must work on the slash-prefixed name the API returns.
        var fake = new List<ContainerInfo>
        {
            new("aaaaaaaaaaaa", "logaimonitor", "logradarai:local", "running", "Up 1 min", ""),
            new("bbbbbbbbbbbb", "some-service", "nginx:alpine", "running", "Up 1 min", ""),
        };
        var filtered = DockerApi.ApplyExclusions(fake, ["logaimonitor", "logaimonitor-redis"]);
        Check("slash-prefixed API name is matched", filtered.Count == 1 && filtered[0].Name == "some-service",
            string.Join(",", filtered.Select(c => c.Name)));
        Check("empty exclusion list keeps everything", DockerApi.ApplyExclusions(fake, []).Count == 2);

        // Log tailing, if any container is available.
        if (containers.Count > 0)
        {
            var lines = await docker.LogsAsync(containers[0].Id, tail: 5);
            Console.WriteLine($"log lines from {containers[0].Name}: {lines.Count}");
            if (lines.Count > 0) Console.WriteLine($"  last: {lines[^1][..Math.Min(100, lines[^1].Length)]}");
            Check("log tail returns lines", lines.Count > 0, lines.Count.ToString());
        }

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}  ({detail})"); }
    }
}
