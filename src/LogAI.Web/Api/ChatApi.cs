// POST /api/ollama/chat
//
// Behaviour of the chat handler and its prompt builder:
//
//   system = "You are LogAI Monitor, an expert system administrator assistant."
//            [+ "\n\nAvailable context (use ONLY what's relevant to the question):\n{context}"]
//   prompt = system + "\n\nUser question: {message}"
//                   + "\n\nProvide a brief, relevant answer based on what the user is actually asking about:"
//
// The context paragraph is passed as ONE prompt (not a separate system message),
// and its "use ONLY what's relevant" wording is deliberate: without it the model
// pastes unrelated log lines into every answer.
//
// Unavailable endpoint -> 503 {"error":"Ollama not available"}.
//
// DELIBERATE SECURITY CHOICE: a real session is required; a
// session is required here because the endpoint proxies arbitrary prompts to the
// configured model (and counts against its capacity).

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Auth;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class ChatApi
{
    private const string SystemPrompt = "You are LogAI Monitor, an expert system administrator assistant.";

    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/ollama/chat", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null)
                return Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

            var data = await ReadObjectAsync(http);
            string message = Text(data, "message");
            string context = Text(data, "context");

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

            if (!await client.IsAvailableAsync())
                return ReadApi.JsonBody(new { error = "Ollama not available" }, 503);

            string system = context.Length > 0
                ? SystemPrompt + "\n\nAvailable context (use ONLY what's relevant to the question):\n" + context
                : SystemPrompt;
            string prompt = system + "\n\nUser question: " + message
                + "\n\nProvide a brief, relevant answer based on what the user is actually asking about:";

            try
            {
                string response = await client.CompleteAsync(prompt);
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["response"] = response,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                          or System.Text.Json.JsonException or InvalidOperationException)
            {
                return ReadApi.JsonBody(new { error = "Ollama not available" }, 503);
            }
        });
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
