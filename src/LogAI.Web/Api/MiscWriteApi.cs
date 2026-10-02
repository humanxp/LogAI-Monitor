// The two "verify the wiring" endpoints.
//
// POST /api/syslog/test-receive (login required)
//   Sends one UDP syslog line to the receiver on loopback so an operator can
//   confirm the whole chain end to end:
//     <14>Jan  1 00:00:00 LogRadarAI-test test[9999]: Test message from LogRadarAI diagnostics at <iso>
//   -> {"message":"Test message sent to UDP port N. Check if it appears in recent logs.","success":true}
//   -> 500 {"message":"Failed to send test message: ...","success":false}
//
// POST /api/telegram/test
//   Body may override bot_token / chat_id; the notifier is reconfigured and a
//   probe is attempted -> {"message":...,"success":bool}
//
// DELIBERATE DIVERGENCE: Python guards telegram/test with @require_redis_api
// only, and it reconfigures the notifier from the request body - an anonymous
// caller could point the alert channel at their own bot. A session is required
// here.

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LogAI.Core.Auth;
using LogAI.Core.Notify;
using LogAI.Core.Store;

namespace LogAI.Web.Api;

internal static class MiscWriteApi
{
    public static void Map(WebApplication app, RedisStore store, SessionCookie cookies)
    {
        app.MapPost("/api/syslog/test-receive", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null) return Login(http);

            int port = ReceiverState.UdpPort;
            try
            {
                string message = "<14>Jan  1 00:00:00 LogRadarAI-test test[9999]: Test message from LogRadarAI diagnostics at "
                    + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

                using var socket = new UdpClient();
                byte[] payload = Encoding.UTF8.GetBytes(message);
                await socket.SendAsync(payload, payload.Length, "127.0.0.1", port);

                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["message"] = "Test message sent to UDP port " + port
                        + ". Check if it appears in recent logs.",
                    ["success"] = true,
                });
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["message"] = "Failed to send test message: " + ex.Message,
                    ["success"] = false,
                }, 500);
            }
        });

        app.MapPost("/api/telegram/test", async (HttpContext http) =>
        {
            if (AuthApi.CurrentUser(http, cookies) is null) return Login(http);

            var body = await ReadObjectAsync(http);
            string token = Text(body, "bot_token");
            string chat = Text(body, "chat_id");
            if (token.Length == 0 || chat.Length == 0)
            {
                await TelegramState.EnsureAsync(store);
                token = token.Length > 0 ? token : TelegramState.BotToken;
                chat = chat.Length > 0 ? chat : TelegramState.ChatId;
            }

            if (token.Length == 0 || chat.Length == 0)
            {
                return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["message"] = "Bot token or chat id is not configured",
                    ["success"] = false,
                });
            }

            bool ok = await new TelegramNotifier().SendAsync(token, chat,
                "\u2705 LogAI Monitor test message");
            return ReadApi.JsonBody(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = ok ? "Test message sent successfully" : "Failed to send test message",
                ["success"] = ok,
            });
        });
    }

    private static IResult Login(HttpContext http) =>
        Results.Redirect("/login?next=" + Uri.EscapeDataString(http.Request.Path));

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
