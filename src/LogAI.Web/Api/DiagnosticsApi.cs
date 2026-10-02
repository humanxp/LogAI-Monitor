// GET /api/syslog/diagnostics (login required).
//
// Eleven top level fields and thirteen per client, all computed from the client
// records the tracker writes. Thresholds and wording are copied verbatim from
// get_client_diagnostics:
//
//   status  active < 60s | idle < 300s | stale otherwise
//   issues  "Errors: N" when error_count > 0
//           "Last error: <first 80 chars>" when last_error is set
//           "No messages for N minutes" when silent for more than 600s
//
// Clients are listed most-recently-active first (ZREVRANGE), and index entries
// whose hash is gone are skipped rather than rendered as blank rows.

using System.Globalization;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

/// <summary>Receiver facts published by AppHost at startup.</summary>
internal static class ReceiverState
{
    public static LogAI.Core.Syslog.SyslogReceiver? Receiver { get; set; }
    public static bool Running => Receiver is not null;
    // Live flags, not a startup snapshot: the listeners bind asynchronously, so
    // reading them from the receiver avoids reporting a stale "not bound".
    public static bool UdpBound => Receiver?.UdpBound ?? false;
    public static bool TcpBound => Receiver?.TcpBound ?? false;
    public static int UdpPort { get; set; }
    public static int TcpPort { get; set; }
    public static DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal static class DiagnosticsApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapGet("/api/syslog/diagnostics", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

            if (!ReceiverState.Running)
            {
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error"] = "Syslog receiver not initialized",
                    ["receiver_running"] = false,
                    ["clients"] = Array.Empty<object>(),
                }, 500);
            }

            double now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks)
                         / (double)TimeSpan.TicksPerSecond;
            var db = store.Db;
            var ips = await db.SortedSetRangeByRankAsync(Keys.ClientsIndex, 0, -1, Order.Descending);

            var clients = new List<Dictionary<string, object?>>();
            int active = 0, withIssues = 0;

            foreach (var ipValue in ips)
            {
                string ip = ipValue.ToString();
                var hash = await db.HashGetAllAsync("syslog:client:" + ip);
                if (hash.Length == 0) continue;                       // stale index entry
                var stats = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

                double lastSeen = Number(stats, "last_seen");
                double firstSeen = Number(stats, "first_seen");
                double lastErrorTime = Number(stats, "last_error_time");
                long secondsSinceLast = lastSeen > 0 ? (long)(now - lastSeen) : 1_000_000_000;

                var protocols = (await db.SetMembersAsync("syslog:client:" + ip + ":protocols"))
                    .Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();
                long perMinute = await db.SortedSetLengthAsync("syslog:client:" + ip + ":recent", now - 60, now);
                long messageCount = (long)Number(stats, "message_count");
                long errorCount = (long)Number(stats, "error_count");
                string lastError = stats.GetValueOrDefault("last_error") ?? "";

                string status = secondsSinceLast < 60 ? "active"
                    : secondsSinceLast < 300 ? "idle" : "stale";

                var issues = new List<string>();
                if (errorCount > 0) issues.Add("Errors: " + errorCount);
                if (lastError.Length > 0)
                    issues.Add("Last error: " + (lastError.Length <= 80 ? lastError : lastError[..80]));
                if (secondsSinceLast > 600)
                    issues.Add("No messages for " + (secondsSinceLast / 60) + " minutes");

                if (status == "active") active++;
                if (errorCount > 0) withIssues++;

                clients.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error_count"] = errorCount,
                    ["first_seen"] = Iso(firstSeen),
                    ["hostname"] = stats.GetValueOrDefault("hostname") is { Length: > 0 } host ? host : ip,
                    ["ip"] = ip,
                    ["issues"] = issues,
                    ["last_error"] = lastError.Length > 0 ? lastError : null,
                    ["last_error_time"] = Iso(lastErrorTime),
                    ["last_seen"] = Iso(lastSeen),
                    ["message_count"] = messageCount,
                    ["messages_per_minute"] = perMinute,
                    ["protocols"] = protocols,
                    ["seconds_since_last"] = secondsSinceLast,
                    ["status"] = status,
                });
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["active_clients"] = active,
                ["clients"] = clients,
                ["clients_with_issues"] = withIssues,
                ["is_primary"] = true,
                ["receiver_running"] = true,
                ["tcp_bound"] = ReceiverState.TcpBound,
                ["tcp_port"] = ReceiverState.TcpPort,
                ["total_clients"] = clients.Count,
                ["udp_bound"] = ReceiverState.UdpBound,
                ["udp_port"] = ReceiverState.UdpPort,
                ["uptime_seconds"] = (long)(DateTimeOffset.UtcNow - ReceiverState.StartedAt).TotalSeconds,
            };
            return ReadApi.JsonBody(payload);
        });
    }

    /// <summary>Python renders these with datetime.fromtimestamp(ts, utc).isoformat().</summary>
    private static string? Iso(double epochSeconds) =>
        epochSeconds <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds((long)(epochSeconds * 1000))
                .ToOffset(TimeSpan.Zero)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    private static double Number(Dictionary<string, string> stats, string field) =>
        stats.TryGetValue(field, out string? raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : 0.0;
}
