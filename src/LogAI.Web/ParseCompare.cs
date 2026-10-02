// Field-by-field comparison of this parser against the Python implementation.
//
// The Python side (scripts/gen_parse_corpus.py, run inside the application
// container) rebuilds raw syslog lines from real records taken out of Redis,
// parses each one with SyslogReceiver.parse_syslog_message, and writes a JSONL
// corpus:  {"input": "...", "ip": "...", "fields": {...}}
//
// This mode replays the same inputs through the C# parser and diffs every
// field, so a behavioural difference cannot hide behind "looks about right".
//
//   dotnet LogAI.Web.dll --parse-compare <corpus.jsonl>

using System.Text;
using System.Text.Json;
using LogAI.Core.Syslog;

namespace LogAI.Web;

internal static class ParseCompare
{
    private static readonly string[] ComparedFields =
    [
        "source", "source_type", "hostname", "program", "facility",
        "severity", "message", "pid", "proc_id", "msg_id",
    ];

    public static int Run(string path)
    {
        int total = 0, mismatched = 0, skipped = 0;
        var examples = new List<string>();
        var perField = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string input = root.GetProperty("input").GetString() ?? "";
            string ip = root.GetProperty("ip").GetString() ?? "";
            var expected = root.GetProperty("fields");

            var entry = SyslogParser.Parse(Encoding.UTF8.GetBytes(input), ip);
            var actual = entry.Fields().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

            // Timestamps differ by construction: the Python parser keeps the
            // sender's text, and both sides do, so they are comparable; the
            // arrival-time default is not, so it is excluded.
            var differences = new List<string>();
            foreach (string field in ComparedFields)
            {
                if (!expected.TryGetProperty(field, out var wantedElement)) continue;
                string wanted = wantedElement.ValueKind == JsonValueKind.Null
                    ? ""
                    : wantedElement.GetString() ?? "";
                actual.TryGetValue(field, out string? got);
                got ??= "";
                if (string.Equals(wanted, got, StringComparison.Ordinal)) continue;

                differences.Add($"{field}: got '{Truncate(got)}' want '{Truncate(wanted)}'");
                perField[field] = perField.GetValueOrDefault(field) + 1;
            }

            total++;
            if (differences.Count == 0) continue;
            mismatched++;
            if (examples.Count < 12)
            {
                examples.Add($"input: {Truncate(input, 160)}");
                examples.AddRange(differences.Select(d => "    " + d));
            }
        }

        Console.WriteLine($"compared {total} real log records ({skipped} skipped)");
        Console.WriteLine($"field mismatches: {mismatched} records");
        foreach (var (field, count) in perField.OrderByDescending(p => p.Value))
            Console.WriteLine($"    {field,-12} {count}");

        if (examples.Count > 0)
        {
            Console.WriteLine("\nfirst differences:");
            foreach (string example in examples) Console.WriteLine(example);
        }

        Console.WriteLine(mismatched == 0 ? "\nIDENTICAL" : "\nDIFFERENT");
        return mismatched == 0 ? 0 : 1;
    }

    private static string Truncate(string value, int max = 90) =>
        value.Length <= max ? value : value[..max] + "…";
}
