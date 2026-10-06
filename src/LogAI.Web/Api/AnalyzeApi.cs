// POST /api/ollama/analyze - analyse one log, or a caller supplied batch.
//
//   log_id  -> 404 {"error":"Log not found"} when absent, else a single analysis
//              recorded as type "single" with log_source / log_severity
//   logs    -> a batch analysis recorded as type "batch"
//   neither -> 400 {"error":"No log_id or logs provided"}
//   endpoint unavailable -> 503 {"error":"Ollama not available"}
//
// An unparseable reply is retried once with the corrective prompt, then treated
// as a genuine FAILURE: the analyser deliberately does not fabricate a
// successful analysis, so the log stays queued and heals on a later run.
//
// DELIBERATE SECURITY CHOICE: this endpoint requires a real session, not just reachability.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class AnalyzeApi
{
    public static void Map(WebApplication app, RedisStore store, LogArchive archive, SessionCookie cookies,
                           AiHistoryWriter history)
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

            // 入参校验必须在"后端可用性检查"之前：放在后面时，模型端点不可达就会先
            // 返回 503，超限的请求永远看不到该有的 400（也白白多打一次上游探测）。
            // 上限：单个日志对象可以很小（甚至只有 id），30MB 的请求体能塞下十几万个，
            // 全部拼进提示词同样会撑爆内存。
            if (logs is not null && logs.Count > AppHost.MaxAnalysisBatch)
                return ReadApi.JsonBody(new
                {
                    error = "Too many logs in one request (max " + AppHost.MaxAnalysisBatch + ")",
                }, 400);

            var settings = await store.GetSettingsAsync();
            string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
            if (provider.Length == 0) provider = "openai";
            string host = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
            if (host.Length == 0) host = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
            var client = new AiClient
            {
                Provider = provider,
                BaseUrl = AiClient.NormalizeBaseUrl(host, provider),
                Model = AiClient.ResolveModel(store),
                ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "",
            };
            // 总开关关闭时不调用模型，"配置为停用"与"后端连不上"要给不同的话术，
            // 否则用户会去排查一个根本没启用的后端。
            if (!AiClient.AiEnabledIn(settings))
                return ReadApi.JsonBody(new { error = "AI analysis is disabled in settings" }, 503);

            if (!await client.IsAvailableAsync())
                return ReadApi.JsonBody(new { error = "Ollama not available" }, 503);

            if (logId.Length > 0)
            {
                var hash = await store.Db.HashGetAllAsync(logId);
                if (hash.Length == 0)
                {
                    // 已归档的日志哈希在 SQLite；只读 Redis 会把"分析这条"变成
                    // 404 Log not found（日志明明还在，只是搬去了冷存储）。
                    var archived = await archive.GetFieldsBatchAsync([logId]);
                    hash = archived[0];
                }
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
                client, PromptBuilder.BatchPrompt(PromptBuilder.LogSummary(
                    batchFields,
                    int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("batch_sample_limit")),
                                 out int sample) && sample > 0 ? sample : 200)), 2048);
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
