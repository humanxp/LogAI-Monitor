// GET /api/ollama/status
//
//   {"available":…,"base_url":…,"current_model":…,"host":…,"models":[…],"provider":…}
//
// base_url is the NORMALISED form (with /v1) while host is the raw setting —
// the two differ, and that difference is what the Python endpoint reports.
// models is fetched live from the OpenAI-compatible /v1/models route; the model
// order is whatever the endpoint returns, which is what the dashboard shows.

using System.Text.Json.Nodes;
using LogAI.Core.Ai;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class OllamaApi
{
    public static void Map(WebApplication app, RedisStore store)
    {
        app.MapGet("/api/ollama/status", async () =>
        {
            var settings = await store.GetSettingsAsync();
            string host = RedisStore.ToText(settings.GetValueOrDefault("ollama_host"));
            if (host.Length == 0) host = Environment.GetEnvironmentVariable("AI_BASE_URL") ?? "";
            string provider = RedisStore.ToText(settings.GetValueOrDefault("ai_provider"));
            if (provider.Length == 0) provider = "openai";
            string model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "";

            var models = new List<string>();
            bool available = false;
            try
            {
                models = await FetchModelsAsync(
                    AiClient.NormalizeBaseUrl(host, provider),
                    Environment.GetEnvironmentVariable("AI_API_KEY") ?? "");
                available = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                available = false;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["available"] = available,
                ["base_url"] = AiClient.NormalizeBaseUrl(host, provider),
                ["current_model"] = model,
                ["host"] = host,
                ["models"] = models,
                ["provider"] = provider,
            };
            return ReadApi.JsonBody(payload);
        });
    }

    private static async Task<List<string>> FetchModelsAsync(string baseUrl, string apiKey)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        if (apiKey.Length > 0)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var ids = new List<string>();
        foreach (JsonNode? node in root?["data"] as JsonArray ?? [])
            if (node?["id"]?.GetValue<string>() is { } id) ids.Add(id);
        return ids;
    }
}
