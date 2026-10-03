// Redis layout shared with the Python application.
//
// Key names, types and value encodings are identical to services/redis_client.py
// so the two implementations can run side by side against the same data — that
// is what makes the parallel verification and the eventual cut-over possible.

using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace LogAI.Core.Store;

/// <summary>Key names, kept in one place so the layout is auditable.</summary>
public static class Keys
{
    public const string Timeline = "logs:timeline";
    public const string Unanalyzed = "logs:unanalyzed";
    public const string Settings = "settings";
    public const string SourcesIndex = "logs:index:sources";
    public const string HostsIndex = "logs:index:hosts";
    public const string SeveritiesIndex = "logs:index:severities";
    public const string Filters = "filters:all";
    public const string AlertsTimeline = "alerts:timeline";
    public const string AiHistoryTimeline = "ai_history:timeline";
    public const string Users = "users:all";
    public const string ClientsIndex = "syslog:clients:index";
    public const string HostIpName = "host:ipname";
    public const string CleanupLastRun = "cleanup:last_run";
    public const string CleanupLastRemoved = "cleanup:last_removed";
    public const string HealthLastSummary = "health:last_summary";

    /// <summary>
    /// 分析失败的持久记录（最近 N 条，带时间戳与原因）。
    /// 失败此前只在 stdout 里留一行，而 stdout 随容器重建一起消失，
    /// 于是"13:48 之后为什么 4 小时没有分析记录"根本无法事后回答。
    /// 排查这类"历史里一个空洞"的问题，必须有一个不随日志轮转消失的痕迹。
    /// </summary>
    public const string AnalysisFailures = "analysis:failures";

    public static string Log(string id) => "log:" + id;
    public static string LogSource(string source) => "logs:source:" + source;
    public static string LogHost(string host) => "logs:host:" + host;
    public static string LogSeverity(string severity) => "logs:severity:" + severity;
    public static string Filter(string id) => "filter:" + id;
    public static string Alert(string id) => "alert:" + id;
    public static string AiHistory(string id) => "ai_history:" + id;
    public static string User(string id) => "user:" + id;
    public static string UserByName(string name) => "users:username:" + name.ToLowerInvariant();
    public static string Client(string ip) => "syslog:client:" + ip;
    public static string ClientProtocols(string ip) => "syslog:client:" + ip + ":protocols";
    public static string QueryCache(string hash) => "logs:q:" + hash;
    public static string NotifyCooldown(string host, string filterId) => $"notif_cooldown:{host}:{filterId}";
}

public sealed class RedisOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 6379;
    public int Database { get; init; }
    public string? Password { get; init; }

    public ConfigurationOptions ToConfiguration()
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { { Host, Port } },
            DefaultDatabase = Database,
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 10000,
            AsyncTimeout = 10000,
            AllowAdmin = true,
        };
        if (!string.IsNullOrEmpty(Password)) options.Password = Password;
        return options;
    }
}

/// <summary>Typed access to the shared Redis data.</summary>
public sealed class RedisStore : IDisposable
{
    private readonly ConnectionMultiplexer _connection;

    public RedisStore(RedisOptions options)
    {
        _connection = ConnectionMultiplexer.Connect(options.ToConfiguration());
    }

    public IDatabase Db => _connection.GetDatabase();

    public async Task<bool> PingAsync()
    {
        try
        {
            await Db.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- settings

    /// <summary>
    /// Reads the settings hash. Values are JSON encoded by the Python side
    /// (save_settings stores json.dumps(value)), so numbers, booleans and
    /// strings are decoded back to their natural types.
    /// </summary>
    public async Task<Dictionary<string, object?>> GetSettingsAsync()
    {
        var entries = await Db.HashGetAllAsync(Keys.Settings);
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in entries)
            settings[entry.Name.ToString()] = DecodeJson(entry.Value.ToString());
        return settings;
    }

    public static object? DecodeJson(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromElement(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON: the Python side treats a bare string as-is.
            return raw;
        }
    }

    private static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => FromElement(p.Value), StringComparer.Ordinal),
        _ => element.GetRawText(),
    };

    public static int ToInt(object? value, int fallback) => value switch
    {
        null => fallback,
        long l => (int)l,
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        bool b => b ? 1 : 0,
        _ => fallback,
    };

    public static bool ToBool(object? value, bool fallback) => value switch
    {
        null => fallback,
        bool b => b,
        long l => l != 0,
        string s when bool.TryParse(s, out bool parsed) => parsed,
        string s => s is "1" or "true" or "True",
        _ => fallback,
    };

    public static string ToText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // ------------------------------------------------------------ counters

    public Task<long> TotalLogsAsync() => Db.SortedSetLengthAsync(Keys.Timeline);
    public Task<long> BacklogAsync() => Db.SortedSetLengthAsync(Keys.Unanalyzed);

    /// <summary>Most recent log arrival, used by the health endpoint.</summary>
    public async Task<double?> LastAnalysisAgeSecondsAsync()
    {
        var entries = await Db.SortedSetRangeByRankWithScoresAsync(Keys.AiHistoryTimeline, -1, -1);
        if (entries.Length == 0) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - entries[0].Score;
    }

    public void Dispose() => _connection.Dispose();
}
