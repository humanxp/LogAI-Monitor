// Stateless session cookie: base64url(payload) "." base64url(HMAC-SHA256).
//
// Stateless so several instances can share a session without a session store,
// and signed so a client cannot alter its own role. A tampered or expired
// cookie verifies as null, which the caller treats as "not signed in".

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogAI.Core.Auth;

public sealed record SessionPayload(string Username, string Role, long ExpiresAt);

public sealed class SessionCookie(byte[] secret, string name = "laim_session")
{
    private readonly byte[] _secret = secret.Length >= 16
        ? secret
        : throw new ArgumentException("session secret must be at least 16 bytes", nameof(secret));

    public string Name { get; } = name;

    public string Create(string username, string role, DateTimeOffset expiresAt)
    {
        var payload = new SessionPayload(username, role, expiresAt.ToUnixTimeSeconds());
        string body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return body + "." + Base64Url(Sign(body));
    }

    /// <summary>Returns the payload, or null when the cookie is missing, forged or expired.</summary>
    public SessionPayload? Verify(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue)) return null;

        int dot = cookieValue.IndexOf('.');
        if (dot <= 0 || dot == cookieValue.Length - 1) return null;

        string body = cookieValue[..dot];
        byte[] provided;
        try
        {
            provided = FromBase64Url(cookieValue[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        byte[] expected = Sign(body);
        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) return null;

        SessionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SessionPayload>(FromBase64Url(body));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }

        if (payload is null) return null;
        if (payload.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        return payload;
    }

    private byte[] Sign(string body) =>
        HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(body));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
