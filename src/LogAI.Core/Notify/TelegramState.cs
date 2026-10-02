// Runtime state of the Telegram channel.
//
// The Python version configures the notifier exactly once, at import time, and
// only when Redis happened to be reachable at that moment:
//
//     if _redis_available:
//         settings = get_settings()
//         if settings['telegram_bot_token'] and settings['telegram_chat_id']:
//             telegram_notifier.configure(...)
//
// A Redis hiccup during startup therefore leaves Telegram disabled for the whole
// process lifetime, with credentials configured and no error logged anywhere —
// which is exactly what this deployment ended up in (telegram_enabled: false).
//
// This implementation re-checks: configuration is attempted on demand and
// retried after a cooldown, so a transient failure self-heals instead of
// disabling alerts until the next restart. That is a deliberate divergence from
// the Python behaviour, not an oversight.

using LogAI.Core.Store;

namespace LogAI.Core.Notify;

public static class TelegramState
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _configured;
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    public static bool Enabled => _configured;
    public static string BotToken { get; private set; } = "";
    public static string ChatId { get; private set; } = "";

    /// <summary>Configures the channel when needed; returns whether it is usable.</summary>
    public static async Task<bool> EnsureAsync(RedisStore store, CancellationToken cancellationToken = default)
    {
        if (_configured && DateTimeOffset.UtcNow - _lastAttempt < RetryAfter) return true;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_configured && DateTimeOffset.UtcNow - _lastAttempt < RetryAfter) return true;
            _lastAttempt = DateTimeOffset.UtcNow;

            var settings = await store.GetSettingsAsync();
            string token = RedisStore.ToText(settings.GetValueOrDefault("telegram_bot_token"));
            string chat = RedisStore.ToText(settings.GetValueOrDefault("telegram_chat_id"));

            BotToken = token;
            ChatId = chat;
            _configured = token.Length > 0 && chat.Length > 0;
            return _configured;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave _configured alone: a failure to read settings is not a
            // configuration error, and the next call will retry.
            return _configured;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Test hook.</summary>
    public static void Reset()
    {
        _configured = false;
        _lastAttempt = DateTimeOffset.MinValue;
    }
}
