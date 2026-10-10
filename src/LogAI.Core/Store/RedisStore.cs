// Redis layout of this project (the storage contract).
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

    /// <summary>
    /// 归档水位：score（epoch 秒）小于等于该值的日志哈希已搬到 SQLite 冷存储。
    /// 归档任务据此只处理"新变老"的一小段，无需每次全量重扫时间线。
    /// </summary>
    public const string ArchiveWatermark = "logs:archive:watermark";
    /// <summary>分析历史归档水位（score-based，与 ai_history:timeline 分数一致）。</summary>
    public const string AiHistoryArchiveWatermark = "ai_history:archive:watermark";
    /// <summary>告警归档水位（score-based，与 alerts:timeline 分数一致）。</summary>
    public const string AlertsArchiveWatermark = "alerts:archive:watermark";
    /// <summary>
    /// 分析历史的状态分类小字段（id → healthy/warning/critical/other）。独立成一条
    /// 哈希、不随归档搬走，/api/ai-history/stats 直接读它即可，不必为已归档记录
    /// 回读 SQLite 里 5KB 的分析 JSON（实测冷调用从 444ms 降到毫秒级）。
    /// </summary>
    public const string AiHistoryStatus = "ai_history:status";

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

    /// <summary>
    /// SCAN + 删除匹配 pattern 的键（如 "syslog:client:*"），返回删除条数。
    /// 用 SCAN 而不是只按索引取：历史遗留的孤儿键（只在旧版本里删了 index 没删
    /// 哈希）不在任何索引里，只有 SCAN 能扫到。
    /// </summary>
    public async Task<long> DeleteKeysByPatternAsync(string pattern, Func<string, bool>? predicate = null,
                                                     int pageSize = 500)
    {
        var endpoints = _connection.GetEndPoints();
        if (endpoints.Length == 0) return 0;
        var server = _connection.GetServer(endpoints[0]);
        long count = 0;
        // 攒够一批就整批 DEL：键可能几十万（如清空日志时的 log:*），逐条删会是
        // 几十万次往返；批量后只有几百次。UNLINK 语义等价但释放是异步的，这里用 DEL
        // 让调用方返回时内存已经真正回收。
        var buffer = new List<RedisKey>(pageSize);
        await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: pageSize))
        {
            if (predicate is not null && !predicate(key.ToString())) continue;
            buffer.Add(key);
            if (buffer.Count >= pageSize)
            {
                await Db.KeyDeleteAsync(buffer.ToArray());
                count += buffer.Count;
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            await Db.KeyDeleteAsync(buffer.ToArray());
            count += buffer.Count;
        }
        return count;
    }

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
    /// Reads the settings hash. Values are stored JSON encoded
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
            // Not JSON: a bare string is used as-is.
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

    // ------------------------------------------------------------ batched reads

    /// <summary>
    /// 分批管道化读取一批哈希:一批一次往返,替代逐条 HGETALL 的 N 次串行往返。
    /// 实测 /api/logs 在 limit=1000 时 0.10s、/api/ai-history 0.13s,几乎全部
    /// 花在逐条往返的等待上;与 /api/ai-history/stats 修复时确立的
    /// "500 条一批"同一做法(整批结果与逐条读完全一致,只是不再逐条等待)。
    /// </summary>
    public async Task<HashEntry[][]> HashGetAllBatchAsync(
        IReadOnlyList<RedisValue> ids, int chunkSize = 500)
    {
        var hashes = new HashEntry[ids.Count][];
        for (int offset = 0; offset < ids.Count; offset += chunkSize)
        {
            int size = Math.Min(chunkSize, ids.Count - offset);
            var batch = Db.CreateBatch();
            var reads = new Task<HashEntry[]>[size];
            for (int i = 0; i < size; i++)
                reads[i] = batch.HashGetAllAsync(ids[offset + i].ToString());
            batch.Execute();
            var loaded = await Task.WhenAll(reads);
            for (int i = 0; i < size; i++)
                hashes[offset + i] = loaded[i];
        }
        return hashes;
    }

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
