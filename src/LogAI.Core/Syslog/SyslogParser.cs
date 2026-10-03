// Syslog parsing, ported field-for-field from services/syslog_receiver.py.
//
// Parsing is regex driven, keeping the very same
// patterns rather than hand-parsing: that is what makes a field-by-field
// comparison between the two implementations meaningful.
//
// Ported behaviours that matter and are easy to get wrong:
//   * _plausible_hostname — a program tag such as "connmand[351]:" or "rngd:"
//     must never be taken for a hostname, otherwise sender-side field shifts
//     invent fake hosts (a bug fixed on 2026-10-01).
//   * _smart_decode — UTF-8, then GB18030, then latin-1, so Chinese text from
//     network devices survives and no byte is ever lost.
//   * The RFC 5424 pattern tolerates spaces inside structured data
//     ("[Originator@6876 sub=Default]"), which the strict token version broke.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LogAI.Core.Syslog;

/// <summary>One parsed syslog message and its fixed field set.</summary>
public sealed class SyslogEntry
{
    public string Source { get; set; } = "";
    public string SourceType { get; set; } = "syslog";
    public string Hostname { get; set; } = "";
    public string Program { get; set; } = "unknown";
    public string Facility { get; set; } = "unknown";
    public string Severity { get; set; } = "info";
    public string Message { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string? Pid { get; set; }
    public string? ProcId { get; set; }
    public string? MsgId { get; set; }

    /// <summary>Field order used by the comparison harness.</summary>
    public IEnumerable<(string Name, string Value)> Fields()
    {
        yield return ("source", Source);
        yield return ("source_type", SourceType);
        yield return ("hostname", Hostname);
        yield return ("program", Program);
        yield return ("facility", Facility);
        yield return ("severity", Severity);
        yield return ("message", Message);
        yield return ("timestamp", Timestamp);
        yield return ("pid", Pid ?? "");
        yield return ("proc_id", ProcId ?? "");
        yield return ("msg_id", MsgId ?? "");
    }
}

public static partial class SyslogParser
{
    /// <summary>Severity index to name, identical to SyslogReceiver.SEVERITIES.</summary>
    private static readonly string[] Severities =
    [
        "emergency", "alert", "critical", "error",
        "warning", "notice", "info", "debug",
    ];

    /// <summary>Facility index to name, identical to SyslogReceiver.FACILITIES.</summary>
    private static readonly Dictionary<int, string> Facilities = new()
    {
        [0] = "kern", [1] = "user", [2] = "mail", [3] = "daemon",
        [4] = "auth", [5] = "syslog", [6] = "lpr", [7] = "news",
        [8] = "uucp", [9] = "cron", [10] = "authpriv", [11] = "ftp",
        [12] = "ntp", [13] = "security", [14] = "console", [15] = "solaris-cron",
        [16] = "local0", [17] = "local1", [18] = "local2", [19] = "local3",
        [20] = "local4", [21] = "local5", [22] = "local6", [23] = "local7",
    };

    /// <summary>RFC 3164 timestamp: "Mmm dd hh:mm:ss" (day may be space padded).</summary>
    private const string TimestampPattern =
        @"[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}";

    private static readonly Regex Rfc3164Host = new(
        $@"^({TimestampPattern})\s+(\S+)\s+(\S+?)(?:\[(\d+)\])?:\s*(.*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Rfc3164NoHost = new(
        $@"^({TimestampPattern})\s+(\S+?)(?:\[(\d+)\])?:\s*(.*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Rfc5424 = new(
        @"^(\d{4}-\d{2}-\d{2}T[^\s]+)\s+(\S+)\s+(\S+?):?\s+(\S+)\s+(\S+)\s+((?:\[[^\]]*\])|-)\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HostnameShape = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.Compiled);

    private static int _codePagesRegistered;

    /// <summary>Registers the code page encodings needed for GB18030.</summary>
    public static void EnsureEncodings()
    {
        if (Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>UTF-8, then GB18030, then latin-1 — matching _smart_decode.</summary>
    public static string SmartDecode(byte[] data)
    {
        if (data.Length == 0) return "";
        EnsureEncodings();

        // UTF-8: reject the decode when it produces replacement characters,
        // which is how a strict UTF-8 decoder behaves.
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(data);
        }
        catch (DecoderFallbackException) { }

        foreach (string name in new[] { "GB18030", "ISO-8859-1" })
        {
            try
            {
                var encoding = Encoding.GetEncoding(name);
                return encoding.GetString(data);
            }
            catch (ArgumentException) { }
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>True when the token really looks like a hostname or IP.</summary>
    public static bool PlausibleHostname(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Contains(':') || token.Contains('[') || token.Contains(']')) return false;
        return HostnameShape.IsMatch(token);
    }

    /// <summary>Formats the arrival time as the fixed ISO-8601 (+00:00) contract.</summary>
    public static string NowTimestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    public static SyslogEntry Parse(byte[] data, string sourceIp) =>
        Parse(SmartDecode(data), sourceIp);

    public static SyslogEntry Parse(string message, string sourceIp)
    {
        var entry = new SyslogEntry
        {
            Source = sourceIp,
            SourceType = "syslog",
            Hostname = sourceIp,
            Program = "unknown",
            Facility = "unknown",
            Severity = "info",
            Timestamp = NowTimestamp(),
        };

        string text = message.Trim();

        // ---- PRI ------------------------------------------------------
        if (text.StartsWith('<'))
        {
            int close = text.IndexOf('>');
            if (close > 1 && close <= 4 &&
                int.TryParse(text.AsSpan(1, close - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pri) &&
                pri is >= 0 and <= 191)
            {
                int facility = pri >> 3;
                int severity = pri & 0x07;
                entry.Facility = Facilities.TryGetValue(facility, out var name) ? name : $"facility{facility}";
                entry.Severity = Severities[severity];
                text = text[(close + 1)..];
            }
        }

        // ---- RFC 3164 -------------------------------------------------
        // NOTE: the 3164 timestamp text is deliberately not stored. The arrival
        // parser keeps the arrival time for RFC 3164 (only 5424 assigns the
        // sender timestamp), and a drop-in replacement has to match that.
        var host = Rfc3164Host.Match(text);
        if (host.Success && PlausibleHostname(host.Groups[2].Value))
        {
            entry.Hostname = host.Groups[2].Value;
            entry.Program = host.Groups[3].Value;
            if (host.Groups[4].Success && host.Groups[4].Value.Length > 0) entry.Pid = host.Groups[4].Value;
            entry.Message = host.Groups[5].Value;
            return entry;
        }

        var noHost = Rfc3164NoHost.Match(text);
        if (noHost.Success)
        {
            // The sender omitted the hostname: keep the source IP, never the tag.
            entry.Program = noHost.Groups[2].Value;
            if (noHost.Groups[3].Success && noHost.Groups[3].Value.Length > 0) entry.Pid = noHost.Groups[3].Value;
            entry.Message = noHost.Groups[4].Value;
            return entry;
        }

        // ---- RFC 5424 -------------------------------------------------
        var rfc5424 = Rfc5424.Match(text);
        if (rfc5424.Success)
        {
            entry.Timestamp = rfc5424.Groups[1].Value;
            string hostname = rfc5424.Groups[2].Value;
            entry.Hostname = PlausibleHostname(hostname) ? hostname : sourceIp;
            entry.Program = rfc5424.Groups[3].Value.TrimEnd(':');
            entry.ProcId = rfc5424.Groups[4].Value;
            entry.MsgId = rfc5424.Groups[5].Value;
            string structured = rfc5424.Groups[6].Value;
            string body = rfc5424.Groups[7].Value;
            entry.Message = body.Length > 0 ? body : structured;
            return entry;
        }

        // ---- Fallback: the whole message ------------------------------
        entry.Message = text;
        return entry;
    }
}
