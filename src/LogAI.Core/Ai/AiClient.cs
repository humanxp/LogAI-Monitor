// Chat client for the two providers this deployment supports.
//
//   openai  POST {base}/chat/completions   {"model","messages","max_tokens","temperature"}
//   ollama  POST {base}/api/chat           {"model","messages","stream":false}
//
// Both return the reply text, which the caller then runs through JsonExtractor:
// keeping transport and parsing separate is what lets the extractor be tested
// against recorded replies without a live model.

using System.Net.Http.Headers;
using LogAI.Core.Store;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAI.Core.Ai;

public sealed class AiClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    public string Provider { get; init; } = "openai";
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public int MaxTokens { get; init; } = 2048;

    /// <summary>
    /// 可选：设置后每次调用都会把响应里的 usage（token 数）累加进 Redis，
    /// 供仪表盘 Service Status 显示。自测不传，避免把测试调用算进生产用量。
    /// </summary>
    public RedisStore? Store { get; init; }
    public double Temperature { get; init; } = 0.1;

    public async Task<string> CompleteAsync(string prompt, string? system = null,
                                            CancellationToken cancellationToken = default)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        bool ollama = string.Equals(Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        string url = ollama
            ? NormalizeBaseUrl(BaseUrl, Provider) + "/api/chat"
            : NormalizeBaseUrl(BaseUrl, Provider) + "/chat/completions";

        var payload = new JsonObject { ["model"] = Model, ["messages"] = messages };
        if (ollama)
        {
            payload["stream"] = false;
            payload["options"] = new JsonObject { ["temperature"] = Temperature, ["num_predict"] = MaxTokens };
        }
        else
        {
            payload["max_tokens"] = MaxTokens;
            payload["temperature"] = Temperature;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AI endpoint returned {(int)response.StatusCode}: {Trim(body)}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // Token 用量累计（后端没给 usage 时 RecordAsync 自己会跳过）。
        // 放在返回内容之前，成功响应才有 usage；异常路径不计数。
        if (Store is not null && root.TryGetProperty("usage", out var usage))
        {
            try { await AiUsage.RecordAsync(Store, usage, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 计数失败绝不能影响分析本身
                Console.Error.WriteLine("[AiUsage] record failed: " + ex.Message);
            }
        }

        // OpenAI-compatible: choices[0].message.content — Ollama: message.content
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
            return content.GetString() ?? "";

        if (root.TryGetProperty("message", out var ollamaMessage) &&
            ollamaMessage.TryGetProperty("content", out var ollamaContent))
            return ollamaContent.GetString() ?? "";

        throw new HttpRequestException("AI endpoint reply had no message content");
    }

    /// <summary>
    /// 让模型针对 warning 下限检出的故障行生成具体建议（返回空列表表示模型失败/无输出）。
    /// 程序只负责确定性检出故障行，建议本身交给模型。
    /// </summary>
    public async Task<List<string>> RecommendFailureLinesAsync(List<string> failureLines,
                                                               CancellationToken cancellationToken = default)
    {
        if (failureLines.Count == 0) return [];

        var prompt = new System.Text.StringBuilder();
        prompt.AppendLine("以下是系统检测到含故障词（failed / refused / out of memory 等）的日志行，此前的批量分析没有覆盖这些故障。");
        prompt.AppendLine("请针对这些具体问题给出简洁、可执行的处置建议。要求：每条建议单独一行、纯文本、不要 JSON、不要编号或项目符号；建议要具体到主机和问题（例如引用 /dev/ipmi0、磁盘、服务名等），不要空泛地说\"请检查日志\"。");
        prompt.AppendLine();
        foreach (string line in failureLines.Take(8))
            prompt.AppendLine("- " + line);

        string reply;
        try { reply = await CompleteAsync(prompt.ToString(), cancellationToken: cancellationToken); }
        catch { return []; }

        var recs = new List<string>();
        foreach (string raw in reply.Split('\n'))
        {
            string t = raw.Trim();
            int start = 0;
            while (start < t.Length && !char.IsLetter(t[start]))
                start++;
            t = t[start..].Trim();
            if (t.Length >= 4) recs.Add(t);
        }
        return recs;
    }

    /// <summary>
    /// The configured host often lacks the /v1 suffix (the deployment stores
    /// http://192.168.50.23:8000 while the OpenAI-compatible route lives under
    /// /v1), which produced a 404 until this normalisation was added.
    /// </summary>
    /// <summary>
    /// 模型名的取值来源，顺序固定为：设置页的 ollama_model → 环境变量 OLLAMA_MODEL → 传参默认值。
    ///
    /// 之前各处只读环境变量，于是设置页的 Model 输入框改了完全不起作用——设置页显示
    /// 的是一个值、实际调用用的是另一个值。这里与 batch/冷却一起统一"设置页优先"。
    /// 用同步读是刻意的：调用点都在同步上下文里（构造 AiClient），不值得为一次
    /// HGET 把整条调用链改成 async。
    /// </summary>
    /// <summary>
    /// "启用 AI 分析"总开关（设置页 ollama_enabled）。缺省视为启用。
    ///
    /// 这个键此前只被前端读写、后端从不读取，于是勾掉它毫无效果：定时分析照跑、
    /// 手动分析照调模型。读设置失败时按"启用"处理——宁可多分析一次，也不要因为
    /// 一次读取抖动把整个分析链路静默停掉。
    /// </summary>
    public static bool AiEnabledIn(IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ollama_enabled", out object? raw) || raw is null)
            return true;
        string text = raw.ToString()?.Trim().Trim('"').ToLowerInvariant() ?? "";
        return text is not ("false" or "0" or "no" or "off");
    }

    /// <summary>Dictionary&lt;string,string&gt; 版本（设置页返回的是这个形状）。</summary>
    public static bool AiEnabledIn(IReadOnlyDictionary<string, string> settings) =>
        AiEnabledIn(settings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));

    /// <summary>
    /// 日志去重开关（默认开启）。设置页「Deduplicate log lines before sending」。
    /// 与 AiEnabledIn 相同的布尔判定：只有显式 false/0/no/off 才视为关闭。
    /// </summary>
    public static bool AiDedupEnabledIn(IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ai_dedup_enabled", out object? raw) || raw is null)
            return true;
        string text = raw.ToString()?.Trim().Trim('"').ToLowerInvariant() ?? "";
        return text is not ("false" or "0" or "no" or "off");
    }

    public static bool AiDedupEnabledIn(IReadOnlyDictionary<string, string> settings) =>
        AiDedupEnabledIn(settings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));

    /// <summary>
    /// 输出去重开关（默认开启）。设置页「Deduplicate analysis results」。控制
    /// JsonExtractor 对模型重复列的 issues/建议去重 + critical_count 封顶。
    /// 与 AiDedupEnabledIn 相同的布尔判定。
    /// </summary>
    public static bool AiDedupOutputEnabledIn(IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ai_dedup_output_enabled", out object? raw) || raw is null)
            return true;
        string text = raw.ToString()?.Trim().Trim('"').ToLowerInvariant() ?? "";
        return text is not ("false" or "0" or "no" or "off");
    }

    public static bool AiDedupOutputEnabledIn(IReadOnlyDictionary<string, string> settings) =>
        AiDedupOutputEnabledIn(settings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));

    /// <summary>
    /// AI 判定修正开关（程序兜底，默认开启）。设置页「AI 判定修正（程序兜底）」。
    /// 开启时：critical 闸门 + critical_count 检查 + 故障词一致性升 warning + warning 下限；
    /// 关闭时：100% 纯模型原始数据，模型判什么就显示什么。
    /// </summary>
    public static bool AiStatusGuardEnabledIn(IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ai_status_guard", out object? raw) || raw is null)
            return true;
        string text = raw.ToString()?.Trim().Trim('"').ToLowerInvariant() ?? "";
        return text is not ("false" or "0" or "no" or "off");
    }

    public static bool AiStatusGuardEnabledIn(IReadOnlyDictionary<string, string> settings) =>
        AiStatusGuardEnabledIn(settings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));

    public static string ResolveModel(RedisStore? store, string? fallback = null)
    {
        try
        {
            if (store is not null)
            {
                var value = store.Db.HashGet(Keys.Settings, "ollama_model");
                string text = value.HasValue ? value.ToString().Trim().Trim('"') : "";
                if (text.Length > 0) return text;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // 读设置失败不应阻断分析：回退到环境变量/默认值即可。
            // 这里刻意捕获基类而不是 RedisException——连接超时、序列化等失败
            // 都不应该让"取个模型名"变成致命错误。
        }

        string env = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "";
        if (env.Length > 0) return env;
        return fallback ?? "";
    }

    public static string NormalizeBaseUrl(string baseUrl, string provider)
    {
        string url = (baseUrl ?? "").TrimEnd((char)47);
        if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)
            && !url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url += "/v1";
        return url;
    }

    /// <summary>True when the endpoint answers, used by the availability flag.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // An unconfigured endpoint is simply not available. Without this guard the
        // probe threw InvalidOperationException ("invalid request URI"), which the
        // health job turned into a silent failure.
        if (string.IsNullOrWhiteSpace(BaseUrl)) return false;

        // 用 GET /models（Ollama 是 /api/tags）而不是发一次真实 completion：健康巡检
        // 每分钟探一次，实测一次 "ping" 要 47 个 token —— 一天白烧近 7 万 token，
        // 还会污染 Service Status 上的用量统计。列表端点 0 token，判断可用性同样准。
        bool ollama = string.Equals(Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        string url = NormalizeBaseUrl(BaseUrl, Provider) + (ollama ? "/api/tags" : "/models");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];
}
