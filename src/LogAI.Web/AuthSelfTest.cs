// Self-test for the signed session cookie.

using LogAI.Core.Auth;

namespace LogAI.Web;

internal static class AuthSelfTest
{
    private static int _failures;

    public static int Run()
    {
        var secret = System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        var cookies = new SessionCookie(secret);

        // Created as a viewer so the forged payload below always differs: an
        // earlier version forged the same values, which made the test pass or
        // fail depending on whether the clock crossed a second boundary.
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        string valid = cookies.Create("bob", "viewer", expiry);
        Check("valid cookie verifies", cookies.Verify(valid) is { Username: "bob", Role: "viewer" });
        Console.WriteLine($"     sample: {valid[..Math.Min(48, valid.Length)]}…");

        // A forged payload (role escalated) must not verify.
        string forged = valid[..valid.IndexOf('.')] + "." + valid[(valid.IndexOf('.') + 1)..];
        var escalated = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new SessionPayload("admin", "admin", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds())))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Check("forged payload rejected", cookies.Verify(escalated + forged[forged.IndexOf('.')..]) is null);

        // Flipping one character of the signature must fail.
        char last = valid[^1];
        string tampered = valid[..^1] + (last == 'A' ? 'B' : 'A');
        Check("tampered signature rejected", cookies.Verify(tampered) is null);

        Check("expired cookie rejected",
            cookies.Verify(cookies.Create("admin", "admin", DateTimeOffset.UtcNow.AddSeconds(-1))) is null);

        Check("empty and malformed rejected",
            cookies.Verify(null) is null && cookies.Verify("") is null && cookies.Verify("garbage") is null);

        var other = new SessionCookie(System.Text.Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff"));
        Check("cookie from another secret rejected", other.Verify(valid) is null);

        Console.WriteLine($"\n{(_failures == 0 ? "ALL PASSED" : "FAILED")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        if (ok) Console.WriteLine($"ok   {name}");
        else { _failures++; Console.WriteLine($"FAIL {name}"); }
    }
}
