// Single-filter endpoint used by the edit dialog.
//
// Applies the same three transformations as the list: conditions parsed into a
// nested object with sorted keys, boolean-looking fields turned into real
// booleans, and alphabetical key order.

using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class FilterApi
{
    public static void Map(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/filters/{id}", async (string id) =>
        {
            string key = id.StartsWith("filter:", StringComparison.Ordinal) ? id : "filter:" + id;
            var hash = await store.Db.HashGetAllAsync(key);
            if (hash.Length == 0) return ReadApi.JsonBody(new { error = "Filter not found" }, 404);

            var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in hash)
            {
                string name = field.Name.ToString();
                string value = field.Value.ToString();
                entry[name] = name switch
                {
                    "conditions" => ReadApi.ParseSortedObject(value),
                    "enabled" or "notify_telegram" or "notify_any_severity" => value == "true",
                    _ => value,
                };
            }

            return ReadApi.JsonBody(entry.OrderBy(p => p.Key, StringComparer.Ordinal)
                                         .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        });
    }
}
