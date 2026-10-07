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

using System.Text.Json;
using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

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
                Store = store,   // 累计 token 用量（见 AiUsage）
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
                var analysis = await CompleteAndExtractAsync(client, prompt, 4096);
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

            string batchSummary = PromptBuilder.LogSummary(
                batchFields,
                int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("batch_sample_limit")),
                             out int sample) && sample > 0 ? sample : 200);
            string batchPrompt = AiClient.AiCacheOptimizedEnabledIn(settings)
                ? PromptBuilder.BatchPromptCached(batchSummary)
                : PromptBuilder.BatchPrompt(batchSummary);
            var batchAnalysis = await CompleteAndExtractAsync(client, batchPrompt, 8192);
            if (batchAnalysis is null)
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["success"] = false,
                    ["error"] = "model reply was not a valid JSON object",
                });

            // 给 issues/处理建议补 "[HOST] " 前缀。
            var batchHosts = batchFields
                .Select(l => l.GetValueOrDefault("hostname") ?? l.GetValueOrDefault("source") ?? "")
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (batchAnalysis is JsonObject bObj && batchHosts.Count > 0)
                JsonExtractor.EnsureHostPrefix(bObj, batchHosts);

            await history.WriteAsync(batchIds, batchAnalysis, "batch", 0);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["analysis"] = batchAnalysis,
                ["logs_analyzed"] = batchFields.Count,
                ["success"] = true,
            });
        });

        // POST /api/analysis/reanalyze —— 对一条历史记录重跑分析并写回原记录。
        //
        // AI History 详情里的 Re-analyze 按钮调这里。历史记录本身与它引用的日志都
        // 可能已归档，所以两处都必须走冷热两库：记录（Redis → SQLite），以及按
        // log_ids 回读的日志哈希（Redis → SQLite）——超过热窗口的日志哈希早搬到冷库，
        // 只看 Redis 会把"这批日志全都还在"误判成"没有可重跑的日志"。
        //
        // 契约：{"reanalyzed":true,"updated":true,"available":N} 成功；
        //       {"reanalyzed":true,"updated":false} 重跑了但模型仍没给出合法 JSON；
        //       {"reanalyzed":false,"msg":...} 没重跑（记录不存在/开关关闭/后端不可达）。
        app.MapPost("/api/analysis/reanalyze", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

            var data = await ReadObjectAsync(http);
            string historyId = Text(data, "history_id");
            if (historyId.Length == 0)
                return ReadApi.JsonBody(new { error = "history_id required" }, 400);

            // ① 历史记录：热库取不到就从冷库取
            var hash = await store.Db.HashGetAllAsync(historyId);
            if (hash.Length == 0)
                hash = (await archive.GetHashesBatchAsync([historyId]))[0];
            if (hash.Length == 0)
                return ReadApi.JsonBody(new { reanalyzed = false, error = "History entry not found" }, 404);

            var record = hash.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString(), StringComparer.Ordinal);
            string type = record.GetValueOrDefault("type") ?? "auto";
            bool single = string.Equals(type, "single", StringComparison.Ordinal);
            var logIds = ParseLogIds(record.GetValueOrDefault("log_ids") ?? "");

            // ② 按 log_ids 回读日志：热库批量取，缺失的从冷库补
            var available = await LoadLogsAsync(store, archive, logIds);

            var settings = await store.GetSettingsAsync();
            if (!AiClient.AiEnabledIn(settings))
                return ReadApi.JsonBody(new { reanalyzed = false, available = available.Count,
                    msg = "AI 分析总开关已关闭（设置页 → AI 分析）" });

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
                Store = store,   // 累计 token 用量（见 AiUsage）
            };
            if (!await client.IsAvailableAsync())
                return ReadApi.JsonBody(new { reanalyzed = false, available = available.Count,
                    msg = "AI 后端当前不可达，稍后再试" });

            if (available.Count == 0)
                return ReadApi.JsonBody(new { reanalyzed = false, available = 0,
                    msg = "这条记录引用的日志都已过保留期，没有可重跑的日志" });

            // ③ 重跑
            int sampleLimit = int.TryParse(RedisStore.ToText(settings.GetValueOrDefault("batch_sample_limit")),
                                           out int sample) && sample > 0 ? sample : 200;
            string prompt;
            if (single)
            {
                prompt = PromptBuilderSingle.Build(available[0]);
            }
            else
            {
                string reSummary = PromptBuilder.LogSummary(available, sampleLimit);
                prompt = AiClient.AiCacheOptimizedEnabledIn(settings)
                    ? PromptBuilder.BatchPromptCached(reSummary)
                    : PromptBuilder.BatchPrompt(reSummary);
            }
            var analysis = await CompleteAndExtractAsync(client, prompt, single ? 4096 : 8192);
            if (analysis is null)
                return ReadApi.JsonBody(new { reanalyzed = true, updated = false, available = available.Count,
                    msg = "模型这次仍未返回合法 JSON，原记录保持不变" });

            // 给 issues/处理建议补 "[HOST] " 前缀。
            var reHosts = available
                .Select(l => l.GetValueOrDefault("hostname") ?? l.GetValueOrDefault("source") ?? "")
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (analysis is JsonObject reObj && reHosts.Count > 0)
                JsonExtractor.EnsureHostPrefix(reObj, reHosts);

            // ④ 写回原记录（热库 HSET / 冷库改 JSON），并同步两处状态
            // 纯模型：status 直接用模型的 overall_status（只做同义词归一）。
            string status = AiStatusClassifier.Classify(type, analysis);

            string analysisJson = analysis.ToJsonString();
            bool inRedis = await store.Db.KeyExistsAsync(historyId);
            if (inRedis)
            {
                await store.Db.HashSetAsync(historyId,
                [
                    new HashEntry("analysis", analysisJson),
                    new HashEntry("status", status),
                    new HashEntry("fail_count", "0"),
                ]);
            }
            else
            {
                await archive.UpdateHashFieldAsync(historyId, "analysis", analysisJson);
                await archive.UpdateHashFieldAsync(historyId, "status", status);
                await archive.UpdateHashFieldAsync(historyId, "fail_count", "0");
            }
            await store.Db.HashSetAsync(Keys.AiHistoryStatus, historyId, status);

            Console.WriteLine("[Analysis] re-analyzed " + historyId + " (" + type + ", "
                + available.Count + " log(s), " + (inRedis ? "hot" : "archived") + ") -> " + status);
            LogAI.Web.Api.StatsApi.PushIfNeeded(store);

            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["analysis"] = analysis,
                ["available"] = available.Count,
                ["reanalyzed"] = true,
                ["status"] = status,
                ["updated"] = true,
            });
        });
    }

    /// <summary>把 log_ids（JSON 数组字符串）解析成 id 列表。</summary>
    private static List<string> ParseLogIds(string raw)
    {
        var ids = new List<string>();
        if (raw.Length == 0) return ids;
        try
        {
            if (JsonNode.Parse(raw) is JsonArray array)
                foreach (var node in array)
                    if (node?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
        }
        catch (JsonException) { }
        return ids;
    }

    /// <summary>
    /// 按 id 回读日志哈希：热库批量取，缺失的从冷库补。重跑分析必须两库都看——
    /// 超过热窗口的日志哈希早已搬到 SQLite。
    /// </summary>
    private static async Task<List<IReadOnlyDictionary<string, string>>> LoadLogsAsync(
        RedisStore store, LogArchive archive, List<string> ids)
    {
        var logs = new List<IReadOnlyDictionary<string, string>>();
        if (ids.Count == 0) return logs;

        const int Chunk = 500;
        var found = new Dictionary<int, HashEntry[]>();
        var missing = new List<string>();
        var missingIndex = new List<int>();
        for (int offset = 0; offset < ids.Count; offset += Chunk)
        {
            int size = Math.Min(Chunk, ids.Count - offset);
            var batch = store.Db.CreateBatch();
            var reads = new Task<HashEntry[]>[size];
            for (int i = 0; i < size; i++) reads[i] = batch.HashGetAllAsync(ids[offset + i]);
            batch.Execute();
            var hashes = await Task.WhenAll(reads);
            for (int i = 0; i < size; i++)
            {
                if (hashes[i].Length == 0)
                {
                    missing.Add(ids[offset + i]);
                    missingIndex.Add(offset + i);
                }
                else found[offset + i] = hashes[i];
            }
        }
        if (missing.Count > 0)
        {
            var archived = await archive.GetFieldsBatchAsync(missing);
            for (int i = 0; i < missing.Count; i++)
                if (archived[i].Length > 0) found[missingIndex[i]] = archived[i];
        }

        for (int i = 0; i < ids.Count; i++)
            if (found.TryGetValue(i, out var entries))
                logs.Add(entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal));
        return logs;
    }

    /// <summary>One attempt, then one corrective retry; null when both fail.</summary>
    private static async Task<JsonNode?> CompleteAndExtractAsync(AiClient client, string prompt, int maxTokens)
    {
        try
        {
            var analysis = JsonExtractor.Extract(await client.CompleteAsync(prompt));
            if (analysis is not null && JsonExtractor.HasRequiredFields(analysis)) return analysis;
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
