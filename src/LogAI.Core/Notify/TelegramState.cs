// Runtime state of the Telegram channel.
//
// A notifier configured exactly once at startup, and
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
// intended behaviour, not an oversight.

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

            // 环境变量回退：除设置页之外也接受 TELEGRAM_BOT_TOKEN /
            // TELEGRAM_CHAT_ID，生产容器也一直带着这两个变量。本实现此前只认
            // Redis 设置，于是"只用环境变量配置"的部署会静默地不发任何通知
            // （日志里只有 Telegram not configured）。设置页优先——那是用户
            // 能改的地方，改完应当立刻生效。
            if (token.Length == 0) token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
            if (chat.Length == 0) chat = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "";

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
