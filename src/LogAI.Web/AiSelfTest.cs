// Exercises the AI client against the configured endpoint and pushes the reply
// through the extractor. Read-only: the prompt is a tiny fixed question.

using LogAI.Core.Ai;
using LogAI.Core.Store;

namespace LogAI.Web;

internal static class AiSelfTest
{
    public static async Task<int> RunAsync()
    {
        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
        };
        using var store = new RedisStore(options);
        var settings = await store.GetSettingsAsync();

        string baseUrl = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
        if (baseUrl.Length == 0) baseUrl = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
        string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
        if (provider.Length == 0) provider = Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "openai";
        string model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "Llama-3.2-3B-Instruct-4bit";

        Console.WriteLine($"provider : {provider}");
        Console.WriteLine($"base url : {baseUrl}");
        Console.WriteLine($"model    : {model}");

        string apiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? "";
        Console.WriteLine($"api key  : {(apiKey.Length > 0 ? "present (" + apiKey.Length + " chars)" : "absent")}");

        var client = new AiClient { Provider = provider, BaseUrl = baseUrl, Model = model, ApiKey = apiKey };
        var started = DateTime.UtcNow;
        try
        {
            string reply = await client.CompleteAsync(
                "Reply with exactly this JSON and nothing else: {\"ok\":true,\"n\":1}");
            var elapsed = DateTime.UtcNow - started;
            Console.WriteLine($"round trip: {elapsed.TotalSeconds:0.0}s, {reply.Length} chars");
            string flat = reply.Replace("\n", "\\n");
            Console.WriteLine($"reply    : {(flat.Length <= 160 ? flat : flat[..160] + "…")}");

            var extracted = JsonExtractor.Extract(reply);
            Console.WriteLine(extracted is null
                ? "extractor: no JSON found"
                : $"extractor: ok={extracted["ok"]} n={extracted["n"]}");
            return extracted is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED after {(DateTime.UtcNow - started).TotalSeconds:0.0}s: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
