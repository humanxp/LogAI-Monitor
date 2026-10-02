// Filter rule matching.
//
// Semantics taken from the Python implementation: four conditions are ANDed,
// and an empty condition means "no constraint".
//
//   severity         list of levels; empty list matches every level
//   source_contains  case-insensitive substring of the source (sender IP)
//   message_contains case-insensitive substring of the message
//   message_regex    case-insensitive regular expression over the message
//
// The level gate is deliberately NOT part of matching: a rule still raises an
// alert for a level below the gate, it just does not push to Telegram. Mixing
// the two would silently drop alerts that users can see on the Alerts page.

using System.Text.RegularExpressions;

namespace LogAI.Core.Filters;

public sealed class FilterRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool NotifyTelegram { get; init; }
    public bool NotifyAnySeverity { get; init; }
    public IReadOnlyList<string> Severity { get; init; } = [];
    public string SourceContains { get; init; } = "";
    public string MessageContains { get; init; } = "";
    public string MessageRegex { get; init; } = "";

    private Regex? _compiled;

    /// <summary>Compiles the pattern once; null when absent or invalid.</summary>
    public Regex? CompiledRegex
    {
        get
        {
            if (_compiled is not null) return _compiled;
            if (string.IsNullOrEmpty(MessageRegex)) return null;
            try
            {
                _compiled = new Regex(MessageRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                                      TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                return null;                 // an invalid pattern must not break ingest
            }
            return _compiled;
        }
    }
}

public static class FilterMatcher
{
    public static bool Matches(FilterRule rule, string source, string severity, string message)
    {
        if (!rule.Enabled) return false;

        if (rule.Severity.Count > 0 &&
            !rule.Severity.Any(level => string.Equals(level, severity, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.SourceContains.Length > 0 &&
            !ContainsIgnoreCase(source, rule.SourceContains)) return false;

        if (rule.MessageContains.Length > 0 &&
            !ContainsIgnoreCase(message, rule.MessageContains)) return false;

        if (rule.MessageRegex.Length > 0)
        {
            Regex? pattern = rule.CompiledRegex;
            if (pattern is null) return false;         // unusable pattern matches nothing
            try
            {
                if (!pattern.IsMatch(message)) return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;                          // pathological pattern: treat as no match
            }
        }

        return true;
    }

    /// <summary>The Telegram gate: global level settings plus the per-rule bypass.</summary>
    public static bool TelegramAllowed(FilterRule rule, string severity, bool alertOnCritical, bool alertOnError)
    {
        if (!rule.NotifyTelegram) return false;
        if (rule.NotifyAnySeverity) return true;

        return severity.ToLowerInvariant() switch
        {
            "emergency" or "alert" or "critical" => alertOnCritical,
            "error" => alertOnError,
            _ => false,
        };
    }

    public static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
