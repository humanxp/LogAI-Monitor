// Read-only REST endpoints.
//
// Response shapes are not guessed: each one was captured from the running
// Python application and compared against it at the same moment, because a
// baseline captured earlier can differ purely because the data moved on.
//
// Details that are easy to get wrong and are encoded explicitly here:
//   * /api/sources and /api/severities return a FLAT ARRAY OF STRINGS sorted
//     with plain string ordering ("10.10.10.12" before "10.10.10.5", and
//     severities alphabetically rather than by urgency).
//   * /api/hosts (no query) returns one entry PER DEVICE, grouped by sender IP:
//     a device that sends some logs with a hostname and some without would
//     otherwise appear twice. Entries carry {count, full, kind, label, value}
//     with kind 'ip' for grouped devices and 'host' for names with no sender
//     IP; counts come from logs:source:<ip> for IP groups and logs:host:<name>
//     otherwise; duplicate short labels are disambiguated with the last octet.
//   * /api/hosts?plain=1 keeps the legacy flat list of host-index names.
//   * Flask's jsonify emits keys alphabetically and without whitespace, so the
//     payload types below declare their members in alphabetical order.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web.Api;

internal static class ReadApi
{
    private const int HostLabelMax = 16;

    public static void Map(WebApplication app, RedisStore store) {

        // --- /api/logs 的筛选助手 -------------------------------------------
        // 空串视为"未提供"，与 Python 的 request.args.get(...) 语义一致。
        static string? Text(HttpRequest req, string name)
        {
            string raw = (req.Query[name].ToString() ?? "").Trim();
            return raw.Length == 0 ? null : raw;
        }

        // ?start=&end= 解析为纪元秒窗口：UI 传纪元秒，也接受 ISO-8601；
        // 两端都能解析才生效，颠倒时交换——与 Python 端点同一套契约。
        static (double Start, double End) ParseWindow(HttpRequest req, out bool hasWindow)
        {
            hasWindow = false;
            double? a = ParseOne(req.Query["start"]);
            double? b = ParseOne(req.Query["end"]);
            if (a is null || b is null) return (0, 0);
            hasWindow = true;
            return (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));

            static double? ParseOne(string? raw)
            {
                raw = (raw ?? "").Trim();
                if (raw.Length == 0) return null;
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double epoch))
                    return epoch;
                if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    return dt.ToUnixTimeMilliseconds() / 1000.0;
                return null;
            }
        }
        app.MapGet("/api/sources", async () =>
            JsonBody((await IndexNamesAsync(store, "logs:source:", Keys.SourcesIndex)).ToArray()));

        app.MapGet("/api/severities", async () =>
            JsonBody((await IndexNamesAsync(store, "logs:severity:", Keys.SeveritiesIndex)).ToArray()));

        app.MapGet("/api/logs", async (HttpRequest request) =>
        {
            int limit = int.TryParse(request.Query["limit"], out int l) && l > 0 ? l : 100;
            int offset = int.TryParse(request.Query["offset"], out int o) && o > 0 ? o : 0;

            string? source = Text(request, "source");
            string? host = Text(request, "host");
            string? severity = Text(request, "severity");
            string? search = Text(request, "search");
            var (startTime, endTime) = ParseWindow(request, out bool hasWindow);

            // 索引筛选（来源/主机/级别/时间窗）交给 Redis 在维度有序集合上完成：
            // 这些集合的分数就是写入时间，因此用 ZINTERSTORE（聚合取 MAX，保证分数
            // 仍是时间戳而不是被求和）求交后即可按 rank 分页。关键字搜索按 Python
            // 的做法在"取回窗口之后"过滤，且单独使用时不改变 total（仍为时间线总数）。
            var dimensions = new List<RedisKey>(3);
            if (!string.IsNullOrEmpty(source)) dimensions.Add(Keys.LogSource(source));
            if (!string.IsNullOrEmpty(host)) dimensions.Add(Keys.LogHost(host));
            if (!string.IsNullOrEmpty(severity)) dimensions.Add(Keys.LogSeverity(severity));

            string? tempKey = null;
            RedisKey scanKey = Keys.Timeline;
            long total;
            try
            {
                if (dimensions.Count > 0)
                {
                    tempKey = "logs:tmp:logsapi:" + Guid.NewGuid().ToString("N");
                    await store.Db.SortedSetCombineAndStoreAsync(
                        SetOperation.Intersect, tempKey, dimensions.ToArray(), aggregate: Aggregate.Max);
                    if (hasWindow)
                    {
                        // 保留 [start, end]（闭区间，与 ZRANGEBYSCORE 一致）
                        await store.Db.SortedSetRemoveRangeByScoreAsync(
                            tempKey, double.NegativeInfinity, startTime, Exclude.Stop);
                        await store.Db.SortedSetRemoveRangeByScoreAsync(
                            tempKey, endTime, double.PositiveInfinity, Exclude.Start);
                    }
                    await store.Db.KeyExpireAsync(tempKey, TimeSpan.FromMinutes(1));
                    scanKey = tempKey;
                    total = await store.Db.SortedSetLengthAsync(tempKey);
                }
                else if (hasWindow)
                {
                    total = await store.Db.SortedSetLengthAsync(
                        Keys.Timeline, startTime, endTime, Exclude.None);
                }
                else
                {
                    total = await store.Db.SortedSetLengthAsync(Keys.Timeline);
                }

                RedisValue[] ids = hasWindow && dimensions.Count == 0
                    ? await store.Db.SortedSetRangeByScoreAsync(
                          Keys.Timeline, startTime, endTime, Exclude.None, Order.Descending, offset, limit)
                    : await store.Db.SortedSetRangeByRankAsync(
                          scanKey, offset, offset + limit - 1, Order.Descending);

                var entries = new List<Dictionary<string, object?>>(ids.Length);
                foreach (var id in ids)
                {
                    var hash = await store.Db.HashGetAllAsync(id.ToString());
                    var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var field in hash)
                    {
                        string name = field.Name.ToString();
                        string value = field.Value.ToString();
                        // analyzed 存的是字符串，对外按布尔值返回。
                        entry[name] = name == "analyzed" ? value == "true" : value;
                    }
                    if (!string.IsNullOrEmpty(search))
                    {
                        string message = entry.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
                        if (message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    // Flask 的 jsonify 会按键名排序，保持一致的键序便于逐字节比对。
                    entries.Add(entry.OrderBy(p => p.Key, StringComparer.Ordinal)
                                     .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
                }

                return JsonBody(new { count = entries.Count, logs = entries, total });
            }
            finally
            {
                if (tempKey is not null) await store.Db.KeyDeleteAsync(tempKey);
            }
        });

        // A single record. Comparing this on both sides is the only way to
        // verify a payload whose data changes several times a second.
        app.MapGet("/api/logs/{id}", async (string id) =>
        {
            var hash = await store.Db.HashGetAllAsync(id);
            // The Python API answers with a JSON body, not an empty 404; the UI parses it.
            if (hash.Length == 0) return JsonBody(new { error = "Log not found" }, 404);

            var single = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in hash)
            {
                string name = field.Name.ToString();
                string value = field.Value.ToString();
                single[name] = name == "analyzed" ? value == "true" : value;
            }

            return JsonBody(single.OrderBy(p => p.Key, StringComparer.Ordinal)
                                       .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        });

        // Filters. Three transformations relative to the stored hash: the
        // conditions JSON becomes a nested object, boolean-looking fields become
        // real booleans, and Flask's sort_keys applies recursively so the nested
        // object's keys are sorted as well.
        app.MapGet("/api/filters", async () =>
        {
            var ids = await store.Db.SetMembersAsync(Keys.Filters);
            var list = new List<Dictionary<string, object?>>(ids.Length);

            foreach (var id in ids)
            {
                var hash = await store.Db.HashGetAllAsync(id.ToString());
                if (hash.Length == 0) continue;

                var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var field in hash)
                {
                    string name = field.Name.ToString();
                    string value = field.Value.ToString();
                    entry[name] = name switch
                    {
                        "conditions" => ParseSortedObject(value),
                        "enabled" or "notify_telegram" or "notify_any_severity" => value == "true",
                        _ => value,
                    };
                }

                list.Add(entry.OrderBy(p => p.Key, StringComparer.Ordinal)
                              .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
            }

            // Newest first, matching the order the Filters page shows.
            list.Sort((a, b) => string.CompareOrdinal(
                b.GetValueOrDefault("created_at")?.ToString() ?? "",
                a.GetValueOrDefault("created_at")?.ToString() ?? ""));

            return JsonBody(list);
        });

        app.MapGet("/api/hosts", async (HttpRequest request) =>
        {
            bool plain = request.Query.TryGetValue("plain", out var flag) &&
                         (flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));

            if (plain)
                return JsonBody((await IndexNamesAsync(store, "logs:host:", Keys.HostsIndex)).ToArray());

            return JsonBody(await HostGroupsAsync(store));
        });
    }

    /// <summary>Sorted names from the registry set, exactly like _index_names().</summary>
    private static async Task<List<string>> IndexNamesAsync(RedisStore store, string prefix, string registry)
    {
        var members = await store.Db.SetMembersAsync(registry);
        return members.Select(m => m.ToString()).OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    /// <summary>Port of get_host_groups(): one entry per device.</summary>
    private static async Task<List<HostEntry>> HostGroupsAsync(RedisStore store)
    {
        var mappingEntries = await store.Db.HashGetAllAsync(Keys.HostIpName);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in mappingEntries) mapping[entry.Name.ToString()] = entry.Value.ToString();

        var hostNames = await IndexNamesAsync(store, "logs:host:", Keys.HostsIndex);
        var absorbed = new HashSet<string>(mapping.Values, StringComparer.Ordinal);

        // 1) One entry per sender IP: the mapped IPs plus any IP that appears in
        //    the host index (a device that never sent a parseable hostname).
        var ips = new SortedSet<string>(mapping.Keys, StringComparer.Ordinal);
        foreach (string name in hostNames)
            if (IsIp(name)) ips.Add(name);

        var groups = new List<HostEntry>(ips.Count);
        foreach (string ip in ips)
        {
            mapping.TryGetValue(ip, out string? name);
            string label = string.IsNullOrEmpty(name) ? ip : ShortHostLabel(name);
            long count = await store.Db.SortedSetLengthAsync(Keys.LogSource(ip));
            groups.Add(new HostEntry(count, string.IsNullOrEmpty(name) ? ip : $"{name} ({ip})", "ip", label, ip));
        }

        // Duplicate short labels (one hostname seen from two IPs) are
        // disambiguated with the last octet: "uefi-x86 (.30)" / "uefi-x86 (.33)".
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
            seen[group.label] = seen.GetValueOrDefault(group.label) + 1;
        for (int i = 0; i < groups.Count; i++)
        {
            if (seen[groups[i].label] <= 1 || groups[i].kind != "ip") continue;
            string octet = groups[i].value.Split('.').Last();
            groups[i] = groups[i] with { label = $"{groups[i].label} (.{octet})" };
        }

        // 2) Names with no sender IP behind them, minus parser artifacts: a
        //    program tag in the hostname slot must never name a device.
        foreach (string name in hostNames)
        {
            if (IsIp(name) || absorbed.Contains(name) || LooksLikeProgramTag(name)) continue;
            long count = await store.Db.SortedSetLengthAsync(Keys.LogHost(name));
            groups.Add(new HostEntry(count, name, "host", ShortHostLabel(name), name));
        }

        groups.Sort((a, b) => string.CompareOrdinal(a.label.ToLowerInvariant(), b.label.ToLowerInvariant()));
        return groups;
    }

    private static string ShortHostLabel(string name)
    {
        name = (name ?? "").Trim();
        return name.Length <= HostLabelMax + 1 ? name : name[..HostLabelMax] + "…";
    }

    /// <summary>name[pid]: or name: — never a real device name.</summary>
    private static bool LooksLikeProgramTag(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string trimmed = name.Trim();
        if (!trimmed.EndsWith(':')) return false;
        string head = trimmed[..^1];
        int bracket = head.IndexOf('[');
        if (bracket < 0) return !head.Contains(':') && head.Length > 0;
        if (!head.EndsWith(']')) return false;
        string digits = head[(bracket + 1)..^1];
        return digits.Length > 0 && digits.All(char.IsDigit) && !head[..bracket].Contains(':');
    }

    private static bool IsIp(string value) => IPAddress.TryParse(value, out _);

    /// <summary>
    /// Flask serialises JSON with a trailing newline, so a byte-identical
    /// response needs one too — without it every payload is exactly one byte
    /// short of the Python original.
    private static readonly JsonSerializerOptions FlaskJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// </summary>
    internal static IResult JsonBody(object payload, int statusCode = 200) =>
        Results.Text(EscapeNonAscii(JsonSerializer.Serialize(payload, FlaskJsonOptions)) + "\n", "application/json", statusCode: statusCode);

    /// <summary>Field names match the Python payload; the order is alphabetical.</summary>
    private sealed record HostEntry(long count, string full, string kind, string label, string value);

    /// <summary>
    /// Python's json.dumps(ensure_ascii=True) escapes every non-ASCII character
    /// as a lowercase \uXXXX (surrogate pairs included) but leaves HTML
    /// characters such as &lt; &gt; &amp; raw. UnsafeRelaxedJsonEscaping gives
    /// the second half; this adds the first.
    /// </summary>
    /// <summary>
    /// Serialises exactly the way the HTTP responses do (Flask semantics). Socket
    /// event payloads must use this too: the default .NET encoder escapes "+" as
    /// \u002B, which Python leaves literal, so a pushed timestamp differed byte
    /// for byte from the HTTP copy of the same value.
    /// </summary>
    internal static string SerializeLikeFlask(object? payload) =>
        EscapeNonAscii(JsonSerializer.Serialize(payload, FlaskJsonOptions));

    private static string EscapeNonAscii(string json)
    {
        var builder = new System.Text.StringBuilder(json.Length + 16);
        foreach (char c in json)
        {
            if (c <= 0x7F) builder.Append(c);
            else builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Parses the stored conditions JSON, sorting keys like Flask's sort_keys.
    /// </summary>
    internal static Dictionary<string, object?> ParseSortedObject(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (parsed is null) return new Dictionary<string, object?>(StringComparer.Ordinal);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in parsed.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            JsonElement element = parsed[key];
            result[key] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long number) ? number : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => element.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? (object?)item.GetString() : item.GetRawText())
                    .ToList(),
                _ => element.GetRawText(),
            };
        }
        return result;
    }
}
