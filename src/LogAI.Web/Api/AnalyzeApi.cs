// POST /api/ollama/analyze - analyse one log, or a caller supplied batch.
//
//   log_id  -> 404 {"error":"Log not found"} when absent, else a single analysis
//              recorded as type "single" with log_source / log_severity
//   logs    -> a batch analysis recorded as type "batch"
//   neither -> 400 {"error":"No log_id or logs provided"}
//   endpoint unavailable -> 503 {"error":"Ollama not available"}
//
// An unparseable reply is retried once with the corrective prompt, then treated
// as a genuine FAILURE: the Python analyser deliberately does not fabricate a
// successful analysis, so the log stays queued and heals on a later run.
//
// DELIBERATE DIVERGENCE: Python guards this with @require_redis_api only.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class AnalyzeApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies, AiHistoryWriter history)
    {
        app.MapPost("/api/ollama/analyze", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

            var data = await ReadObjectAsync(http);
            string logId = Text(data, "log_id");
            var logs = data?["logs"] as JsonArray;

            if (logId.Length == 0 && (logs is null || logs.Count == 0))
                return ReadApi.JsonBody(new { error = "No log_id or logs provided" }, 400);

            var settings = await store.GetSettingsAsync();
            string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
            if (provider.Length == 0) provider = "openai";
            string host = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
            if (host.Length == 0) host = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
            var client = new AiClient
            {
                Provider = provider,
                BaseUrl = AiClient.NormalizeBaseUrl(host, provider),
                Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "",
                ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
            };
            if (!await client.IsAvailableAsync())
                return ReadApi.JsonBody(new { error = "Ollama not available" }, 503);

            if (logId.Length > 0)
            {
                var hash = await store.Db.HashGetAllAsync(logId);
                if (hash.Length == 0) return ReadApi.JsonBody(new { error = "Log not found" }, 404);
                var fields = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);

                string prompt = PromptBuilderSingle.Build(fields);
                var analysis = await CompleteAndExtractAsync(client, prompt, 1024);
                if (analysis is null)
                    return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["success"] = false,
                        ["error"] = "model reply was not a valid JSON object",
                    });

                await history.WriteAsync([logId], analysis, "single", 0,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["log_source"] = fields.GetValueOrDefault("source") ?? "unknown",
                        ["log_severity"] = fields.GetValueOrDefault("severity") ?? "info",
                    });

                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["analysis"] = analysis,
                    ["success"] = true,
                });
            }

            var batch = logs!.Select(node => node as JsonObject).Where(o => o is not null).ToList();
            var batchFields = batch
                .Select(o => (IReadOnlyDictionary<string, string>)o!
                    .ToDictionary(p => p.Key, p => p.Value?.ToString().Trim('"') ?? "", StringComparer.Ordinal))
                .ToList();
            var batchIds = batch
                .Select(o => o!["id"]?.ToString().Trim('"') ?? "")
                .Where(id => id.Length > 0).ToList();

            var batchAnalysis = await CompleteAndExtractAsync(
                client, PromptBuilder.BatchPrompt(PromptBuilder.LogSummary(batchFields)), 2048);
            if (batchAnalysis is null)
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["success"] = false,
                    ["error"] = "model reply was not a valid JSON object",
                });

            await history.WriteAsync(batchIds, batchAnalysis, "batch", 0);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["analysis"] = batchAnalysis,
                ["logs_analyzed"] = batchFields.Count,
                ["success"] = true,
            });
        });
    }

    /// <summary>One attempt, then one corrective retry; null when both fail.</summary>
    private static async Task<JsonNode?> CompleteAndExtractAsync(AiClient client, string prompt, int maxTokens)
    {
        try
        {
            var analysis = JsonExtractor.Extract(await client.CompleteAsync(prompt));
            if (analysis is not null) return analysis;
            return JsonExtractor.Extract(await client.CompleteAsync(PromptBuilder.CorrectivePrompt(prompt)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Text(JsonObject? data, string name) =>
        data is not null && data.TryGetPropertyValue(name, out JsonNode? node) && node is JsonValue value
            && value.TryGetValue(out string? text) ? text : "";

    private static async Task<JsonObject?> ReadObjectAsync(HttpContext http)
    {
        string raw = await new StreamReader(http.Request.Body).ReadToEndAsync();
        try { return JsonNode.Parse(raw) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
