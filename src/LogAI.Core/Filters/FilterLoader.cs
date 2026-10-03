// Loads a filter rule out of its Redis hash.
//
// The rule's conditions live in a nested JSON string ("conditions"), not as flat
// hash fields, so they are decoded here and mapped onto FilterRule. A rule whose
// conditions cannot be decoded is skipped rather than throwing: one malformed
// filter must not stop log ingestion.

using System.Text.Json.Nodes;
using LogAI.Core.Store;

namespace LogAI.Core.Filters;

public static class FilterLoader
{
    public static async Task<FilterRule?> LoadAsync(RedisStore store, string filterId)
    {
        // filters:all members ARE the full key ("filter:<ms>") and the hash is
        // stored under that same string - reads do hgetall(filter_id) with no
        // prefixing. Prefixing here produced filter:filter:<ms> and silently
        // matched nothing, so alerts never fired.
        string key = filterId.StartsWith("filter:", StringComparison.Ordinal) ? filterId : Keys.Filter(filterId);
        var hash = await store.Db.HashGetAllAsync(key);
        if (hash.Length == 0) return null;

        var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
        var severities = new List<string>();
        string sourceContains = "", messageContains = "", messageRegex = "";

        if (fields.TryGetValue("conditions", out string? conditions) && conditions.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(conditions) is JsonObject parsed) { }
                var node = JsonNode.Parse(conditions) as JsonObject;
                if (node?["severity"] is JsonArray severityArray)
                    foreach (JsonNode? item in severityArray)
                        if (item?.GetValue<string>() is { Length: > 0 } level) severities.Add(level);
                sourceContains = Text(node?["source_contains"]);
                messageContains = Text(node?["message_contains"]);
                messageRegex = Text(node?["message_regex"]);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        // Severity may also be stored as a flat comma separated field.
        if (severities.Count == 0 && fields.TryGetValue("severity", out string? flat) && flat.Length > 0)
            severities.AddRange(flat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return new FilterRule
        {
            Id = fields.GetValueOrDefault("id") is { Length: > 0 } id ? id : filterId,
            Name = fields.GetValueOrDefault("name") ?? "",
            Enabled = fields.GetValueOrDefault("enabled") is not "false",
            NotifyTelegram = fields.GetValueOrDefault("notify_telegram") == "true",
            NotifyAnySeverity = fields.GetValueOrDefault("notify_any_severity") == "true",
            Severity = severities,
            SourceContains = sourceContains,
            MessageContains = messageContains,
            MessageRegex = messageRegex,
        };
    }

    private static string Text(JsonNode? node) => node is null ? "" : node.ToString().Trim('"');
}
