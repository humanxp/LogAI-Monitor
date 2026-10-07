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

    /// <summary>
    /// 传给 OpenAI 兼容后端的 frequency_penalty。python-legacy 版固定 0.3——它抑制
    /// 小模型被逼着输出 JSON 时陷入的重复循环（同一问题重复列 N 次、把 token 预算烧光
    /// 再截断）。C# 版之前漏了这个参数，导致 3B 模型重复列问题、虚高 critical_count。
    /// </summary>
    public double FrequencyPenalty { get; init; } = 0.3;

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
            if (FrequencyPenalty > 0)
                payload["frequency_penalty"] = FrequencyPenalty;
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
    /// 「优化 AI 模型缓存效率」开关（默认关闭）。开启时用 BatchPromptCached（few-shot
    /// 前缀缓存版，静态块在日志前、命中率高），关闭时用简单版 BatchPrompt。与 AiEnabledIn
    /// 相反的判定：这是 opt-in，只有显式 true/1/yes/on 才视为开启。
    /// </summary>
    public static bool AiCacheOptimizedEnabledIn(IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ai_cache_optimized", out object? raw) || raw is null)
            return false;
        string text = raw.ToString()?.Trim().Trim('"').ToLowerInvariant() ?? "";
        return text is "true" or "1" or "yes" or "on";
    }

    public static bool AiCacheOptimizedEnabledIn(IReadOnlyDictionary<string, string> settings) =>
        AiCacheOptimizedEnabledIn(settings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));

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
