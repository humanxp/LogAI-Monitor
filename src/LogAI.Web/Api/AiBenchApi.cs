// 模型评测 API（仅管理员）：把 --analysis-bench 的能力暴露给设置页。
//
//   POST /api/ai/bench             启动一次评测（{model, corpus:"builtin"|<id>, repeat?}）
//   GET  /api/ai/bench             轮询当前评测进度/结果
//   POST /api/ai/bench/corpus      上传自定义评测集（JSON）
//   GET  /api/ai/bench/corpora     列出评测集（builtin + 自定义）
//   DELETE /api/ai/bench/corpus/{id}  删除自定义评测集
//
// 安全与资源约束：
//   * 仅 role=admin；其余 403，未登录跳登录页。
//   * 同一时刻只允许一个评测在跑（内存单飞）：新请求在旧任务完成前返回 409。
//   * 评测只调 AI 端点、不写 Redis/日志（唯一写是自定义语料存进 bench:corpora）。
//
// 为什么内置语料与打分逻辑不搬一份到这里：复用 AnalysisBench 的同一条
// PromptBuilder → AiClient → JsonExtractor → AiStatusClassifier 路径，结果才可信。

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class AiBenchApi
{
    private static readonly object Gate = new();
    private static Task? Running;
    private static Snapshot? Current;

    private sealed record Snapshot(string[] Models, string[] PromptModes, string[] Corpora,
                                   int Done, int Total, string Phase, string CurrentModel,
                                   List<Dictionary<string, object?>>? Results, string? Error,
                                   string StartedAt);

    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/ai/bench", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            JsonNode? body = await ReadJson(http);
            if (body is not JsonObject o) return ReadApi.JsonBody(new { error = "No data provided" }, 400);

            // 支持多模型对比：`models`（数组）或单模型 `model`。数组优先。
            var models = new List<string>();
            if (o["models"] is JsonArray arr)
                models.AddRange(arr.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0));
            else
            {
                string single = Text(o, "model");
                if (single.Length > 0) models.Add(single);
            }
            if (models.Count == 0) return ReadApi.JsonBody(new { error = "model(s) is required" }, 400);
            models = models.Distinct(StringComparer.Ordinal).ToList();

            // 支持多语料：`corpora`（数组）或单个 `corpus`。数组优先。
            var corpora = new List<string>();
            if (o["corpora"] is JsonArray carr)
                corpora.AddRange(carr.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0));
            else
            {
                string single = Text(o, "corpus");
                if (single.Length > 0) corpora.Add(single);
            }
            if (corpora.Count == 0) corpora.Add("builtin");
            corpora = corpora.Distinct(StringComparer.Ordinal).ToList();

            int repeat = Int(o, "repeat");
            if (repeat <= 0) repeat = 1;

            // 可选：按用例 id 过滤内置语料（前端展开内置 27 例逐项勾选时用）。空=全量。
            var caseIds = new HashSet<string>(StringComparer.Ordinal);
            if (o["case_ids"] is JsonArray idsArr)
                foreach (var x in idsArr)
                    if (x?.ToString() is { } s && s.Length > 0) caseIds.Add(s);

            // 逐份语料解析并拼接：每个模型都在「选中语料的并集」上跑一轮。
            var allCases = new List<AnalysisBench.BenchCase>();
            foreach (string corpusId in corpora)
            {
                if (corpusId == "builtin")
                {
                    var builtin = AnalysisBench.BuiltinCases();
                    if (caseIds.Count > 0) builtin = builtin.Where(c => caseIds.Contains(c.Id)).ToArray();
                    allCases.AddRange(builtin);
                    continue;
                }
                string? raw = await store.Db.HashGetAsync(CorpusKey, corpusId);
                if (raw is null) return ReadApi.JsonBody(new { error = $"corpus not found: {corpusId}" }, 404);
                var parsed = ParseCorpus(raw)?.Cases ?? [];
                if (parsed.Length == 0) return ReadApi.JsonBody(new { error = $"corpus is empty: {corpusId}" }, 400);
                allCases.AddRange(parsed);
            }
            if (allCases.Count == 0) return ReadApi.JsonBody(new { error = "no cases selected" }, 400);
            var cases = allCases.ToArray();

            // 端点配置与 AiSelfTest 一致：设置页优先，其次环境变量。
            var settings = await store.GetSettingsAsync();
            string baseUrl = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
            if (baseUrl.Length == 0) baseUrl = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
            string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
            if (provider.Length == 0) provider = Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "openai";
            string apiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "";
            if (baseUrl.Length == 0) return ReadApi.JsonBody(new { error = "AI endpoint not configured" }, 400);

            string promptMode = Text(o, "prompt_mode");
            var promptModes = models.Select(m => promptMode.Length > 0 ? promptMode : AnalysisBench.ModeForModel(m)).ToArray();

            bool started = TryStart(models.ToArray(), promptModes, corpora.ToArray(), provider, baseUrl, apiKey, cases, repeat, store);
            if (!started) return ReadApi.JsonBody(new { error = "a benchmark is already running" }, 409);
            return ReadApi.JsonBody(new { started = true, models, corpora, cases = cases.Length });
        });

        app.MapGet("/api/ai/bench", (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            lock (Gate)
            {
                if (Current is null)
                    return ReadApi.JsonBody(new { phase = "idle" });
                var d = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["phase"] = Current.Phase,
                    ["running"] = Current.Phase == "running",
                    ["model"] = Current.CurrentModel,
                    ["models"] = Current.Models,
                    ["prompt_modes"] = Current.PromptModes,
                    ["corpora"] = Current.Corpora,
                    ["done"] = Current.Done,
                    ["total"] = Current.Total,
                    ["error"] = Current.Error,
                    ["started_at"] = Current.StartedAt,
                    ["results"] = Current.Results,
                };
                return ReadApi.JsonBody(Ordered(d));
            }
        });

        // ---- 自定义语料 CRUD ----
        app.MapPost("/api/ai/bench/corpus", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            JsonNode? body = await ReadJson(http);
            if (body is not JsonObject o) return ReadApi.JsonBody(new { error = "No data provided" }, 400);
            var corpus = ParseCorpusNode(o);
            if (corpus is null || corpus.Cases.Length == 0)
                return ReadApi.JsonBody(new { error = "invalid corpus: cases must be a non-empty array" }, 400);

            string id = "c" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = o.ToJsonString();
            await store.Db.HashSetAsync(CorpusKey, id, json);
            return ReadApi.JsonBody(new { id, name = corpus.Name, case_count = corpus.Cases.Length });
        });

        app.MapGet("/api/ai/bench/corpora", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var list = new List<object>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = "builtin", ["name"] = "内置评测集", ["case_count"] = AnalysisBench.BuiltinCases().Length,
                },
            };
            foreach (var entry in (await store.Db.HashGetAllAsync(CorpusKey)).OrderBy(e => e.Name.ToString(), StringComparer.Ordinal))
            {
                var corpus = ParseCorpus(entry.Value.ToString());
                if (corpus is null) continue;
                list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = entry.Name.ToString(), ["name"] = corpus.Name, ["case_count"] = corpus.Cases.Length,
                });
            }
            return ReadApi.JsonBody(list.ToArray());
        });

        // 单个评测集的用例明细（前端展开内置 27 例逐项勾选时用）。
        app.MapGet("/api/ai/bench/corpus/{id}", async (string id, HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            AnalysisBench.BenchCase[] cases;
            string name;
            if (id == "builtin")
            {
                cases = AnalysisBench.BuiltinCases();
                name = "内置评测集";
            }
            else
            {
                string? raw = await store.Db.HashGetAsync(CorpusKey, id);
                if (raw is null) return ReadApi.JsonBody(new { error = "corpus not found" }, 404);
                var parsed = ParseCorpus(raw);
                if (parsed is null) return ReadApi.JsonBody(new { error = "corpus invalid" }, 400);
                cases = parsed.Cases;
                name = parsed.Name;
            }

            var items = cases.Select(c => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = c.Id,
                ["expected"] = c.Expected,
                ["log_count"] = c.Logs.Length,
                ["preview"] = Preview(c),
            }).ToArray();
            return ReadApi.JsonBody(new { id, name, cases = items });
        });

        app.MapDelete("/api/ai/bench/corpus/{id}", async (string id, HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);
            if (id == "builtin") return ReadApi.JsonBody(new { error = "cannot delete the builtin corpus" }, 400);
            bool removed = await store.Db.HashDeleteAsync(CorpusKey, id);
            return ReadApi.JsonBody(new { deleted = removed });
        });

        // 历史成绩：所有评测过的模型各一条（重评覆盖）。前端进「模型评测」类目时读它。
        app.MapGet("/api/ai/bench/results", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);

            var list = new List<System.Text.Json.Nodes.JsonNode>();
            foreach (var entry in await store.Db.HashGetAllAsync(ResultsKey))
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(entry.Value.ToString()) is { } node) list.Add(node);
                }
                catch { /* 跳过坏记录 */ }
            }
            // 按 exact 命中数降序，方便一眼看谁最好
            static int ExactOf(System.Text.Json.Nodes.JsonNode? n) =>
                int.TryParse(n?["exact"]?.ToString(), out int v) ? v : 0;
            list.Sort((a, b) => ExactOf(b).CompareTo(ExactOf(a)));
            return ReadApi.JsonBody(list.ToArray());
        });

        // 清空历史成绩（管理员）。只清成绩，不动评测集。
        app.MapDelete("/api/ai/bench/results", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is not { } session) return Redirect(http);
            if (!string.Equals(session.Role, "admin", StringComparison.Ordinal))
                return ReadApi.JsonBody(new { error = "Access denied" }, 403);
            await store.Db.KeyDeleteAsync(ResultsKey);
            return ReadApi.JsonBody(new { cleared = true });
        });
    }

    private const string CorpusKey = "bench:corpora";
    // 评测成绩持久化：hash，field=模型名，value=该模型最近一次的成绩 JSON（重评即覆盖）。
    private const string ResultsKey = "bench:results";

    // ------------------------------------------------------------------ job
    private static bool TryStart(string[] models, string[] promptModes, string[] corpora,
                                 string provider, string baseUrl, string apiKey,
                                 AnalysisBench.BenchCase[] cases, int repeat, RedisStore store)
    {
        lock (Gate)
        {
            if (Running is { IsCompleted: false }) return false;
            int total = models.Length * cases.Length;
            Current = new Snapshot(models, promptModes, corpora, 0, total, "running", models[0],
                                   null, null, DateTimeOffset.UtcNow.ToString("o"));
            Running = Task.Run(async () =>
            {
                try
                {
                    var results = new List<Dictionary<string, object?>>();
                    int done = 0;
                    for (int i = 0; i < models.Length; i++)
                    {
                        // 每个模型一个 client（Model 不同）；其余配置共享。
                        var client = new AiClient
                        {
                            Provider = provider,
                            BaseUrl = AiClient.NormalizeBaseUrl(baseUrl, provider),
                            Model = models[i],
                            ApiKey = apiKey,
                            EnableThinking = false,
                        };
                        int prior = done;
                        var r = await AnalysisBench.RunModelAsync(client, models[i], promptModes[i], cases, repeat,
                            (d, t) =>
                            {
                                lock (Gate) Current = Current! with { Done = prior + d, CurrentModel = models[i] };
                                return Task.CompletedTask;
                            });
                        var payload = ResultPayload(r);
                        payload["saved_at"] = DateTimeOffset.UtcNow.ToString("o");
                        payload["corpora"] = corpora;
                        payload["case_count"] = cases.Length;
                        results.Add(payload);
                        // 持久化：每个模型一条记录（field=模型名），重评同一模型即覆盖旧成绩。
                        try
                        {
                            await store.Db.HashSetAsync(ResultsKey, models[i],
                                System.Text.Json.JsonSerializer.Serialize(payload));
                        }
                        catch { /* 存不进不影响本次评测本身 */ }
                        done += cases.Length;
                        lock (Gate) Current = Current! with { Done = done, Results = results.ToList() };
                    }
                    lock (Gate) Current = Current! with { Phase = "done", Results = results, Done = total, CurrentModel = models[^1] };
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    lock (Gate) Current = Current! with { Phase = "failed", Error = ex.Message };
                }
            });
            return true;
        }
    }

    private static Dictionary<string, object?> ResultPayload(AnalysisBench.BenchResult r)
    {
        var cases = r.Cases.Select(c => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = c.Id,
            ["expected"] = c.Expected,
            ["verdicts"] = c.Verdicts,
            ["last_issues"] = c.LastIssues,
            ["fabricated"] = c.Fabricated,
        }).ToArray();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = r.Model,
            ["prompt_mode"] = r.PromptMode,
            ["attempts"] = r.Attempts,
            ["exact"] = r.Exact,
            ["tolerant"] = r.Tolerant,
            ["crit_recall"] = r.CritRecall,
            ["crit_total"] = r.CritTotal,
            ["healthy_clean"] = r.HealthyClean,
            ["healthy_total"] = r.HealthyTotal,
            ["issue_hit"] = r.IssueHit,
            ["issue_total"] = r.IssueTotal,
            ["fabricated"] = r.Fabricated,
            ["parse_fail"] = r.ParseFail,
            ["avg_seconds"] = Math.Round(r.AvgSeconds, 1),
            ["cases"] = cases,
        };
    }

    // ------------------------------------------------------------------ corpus
    private sealed record CorpusDto(string Name, AnalysisBench.BenchCase[] Cases);
    private sealed record CaseDto(string Id, string Expected, string[] MustMention, string[] MustNotMention, SampleDto[] Logs);
    private sealed record SampleDto(string Severity, string Host, string Program, string Message);

    private static CorpusDto? ParseCorpus(string json)
    {
        try { return ParseCorpusNode(JsonNode.Parse(json)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static CorpusDto? ParseCorpusNode(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (o["cases"] is not JsonArray cases || cases.Count == 0) return null;
        string name = Text(o, "name");
        if (name.Length == 0) name = "自定义评测集";

        var list = new List<AnalysisBench.BenchCase>();
        foreach (var item in cases)
        {
            if (item is not JsonObject c) continue;
            string id = Text(c, "id");
            string expected = Text(c, "expected").ToLowerInvariant();
            if (id.Length == 0 || expected is not ("critical" or "warning" or "healthy")) continue;
            string[] must = Strings(c, "must_mention");
            string[] mustNot = Strings(c, "must_not_mention");
            var logs = new List<AnalysisBench.Sample>();
            if (c["logs"] is JsonArray arr)
            {
                foreach (var l in arr)
                {
                    if (l is not JsonObject lo) continue;
                    string severity = Text(lo, "severity");
                    string host = Text(lo, "host");
                    string program = Text(lo, "program");
                    string message = Text(lo, "message");
                    if (severity.Length == 0) severity = "info";
                    if (message.Length == 0) continue;
                    logs.Add(new AnalysisBench.Sample(severity, host, program, message));
                }
            }
            if (logs.Count == 0) continue;
            list.Add(new AnalysisBench.BenchCase(id, expected, must, mustNot, logs.ToArray()));
        }
        return list.Count == 0 ? null : new CorpusDto(name, list.ToArray());
    }

    private static string[] Strings(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToArray() : [];

    private static string Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out string? s) && s is not null ? s : "";

    private static int Int(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out int n) ? n : 0;

    /// <summary>用例一句话预览：首条日志的「程序: 消息」截断，供勾选列表显示。</summary>
    private static string Preview(AnalysisBench.BenchCase c)
    {
        var first = c.Logs.FirstOrDefault();
        if (first is null) return "";
        string prog = first.Program ?? "";
        string msg = first.Message ?? "";
        string text = prog.Length > 0 ? prog + ": " + msg : msg;
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    private static async Task<JsonNode?> ReadJson(HttpContext http)
    {
        string raw = await new StreamReader(http.Request.Body).ReadToEndAsync();
        try { return JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { return null; }
    }

    private static IResult Redirect(HttpContext http) =>
        Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

    private static Dictionary<string, object?> Ordered(Dictionary<string, object?> d) =>
        new(d.OrderBy(p => p.Key, StringComparer.Ordinal));
}
