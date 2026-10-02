// Password verification compatible with Werkzeug (Flask) hashes.
//
// Stored format:  <method>$<salt>$<hex digest>  where method is one of
//   scrypt:N:r:p        (current Werkzeug default)
//   pbkdf2:sha256:ITER  / pbkdf2:sha1:ITER
//   sha256:ITER (legacy, unsalted)
//
// The scrypt parameters vary per record, so they are read from the hash itself
// rather than assumed: an existing deployment keeps working without forcing
// anyone to change their password.

using System.Security.Cryptography;
using System.Text;
using Norgerman.Cryptography.Scrypt;

namespace LogAI.Core.Auth;

public static class WerkzeugPassword
{
    public static bool IsSupported(string stored)
    {
        string method = stored.Split('$')[0];
        return method.StartsWith("scrypt", StringComparison.Ordinal) ||
               method.StartsWith("pbkdf2", StringComparison.Ordinal) ||
               method.StartsWith("sha256", StringComparison.Ordinal);
    }

    public static bool Verify(string password, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 3) return false;

        string[] method = parts[0].Split(':');
        string salt = parts[1];
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = method[0] switch
        {
            "scrypt" => ScryptHash(password, salt, method, expected.Length),
            "pbkdf2" => Pbkdf2Hash(password, salt, method, expected.Length),
            "sha256" => Sha256Legacy(password, salt, method, expected.Length),
            _ => [],
        };

        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ScryptHash(string password, string salt, string[] method, int length)
    {
        int n = method.Length > 1 ? int.Parse(method[1]) : 32768;
        int r = method.Length > 2 ? int.Parse(method[2]) : 8;
        int p = method.Length > 3 ? int.Parse(method[3]) : 1;
        return ScryptUtil.Scrypt(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), n, r, p, length);
    }

    private static byte[] Pbkdf2Hash(string password, string salt, string[] method, int length)
    {
        string algorithm = method.Length > 1 ? method[1] : "sha256";
        int iterations = method.Length > 2 ? int.Parse(method[2]) : 260000;
        HashAlgorithmName name = algorithm switch
        {
            "sha1" => HashAlgorithmName.SHA1,
            "sha512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt),
                                         iterations, name, length);
    }

    private static byte[] Sha256Legacy(string password, string salt, string[] method, int length)
    {
        int iterations = method.Length > 1 ? int.Parse(method[1]) : 1;
        byte[] current = Encoding.UTF8.GetBytes(salt + password);
        for (int i = 0; i < iterations; i++) current = SHA256.HashData(current);
        return current;
    }
}

// ---------------------------------------------------------------------------
// Generation, for the user-management endpoint.
//
// Werkzeug's stored form is  scrypt:N:r:p$salt$hash  where BOTH salt and hash are
// HEX STRINGS, and scrypt is fed the salt's ASCII bytes (not a decoded value) -
// which is why Verify above passes the salt string straight through. A generated
// hash is therefore:
//
//   salt  = hex(sha256(60 random bytes))                 -> 64 chars
//   hash  = hex(scrypt(password, salt_ascii, n, r, p, 64)) -> 128 chars
//
// The parameters are Werkzeug's current defaults, and the output is verified by
// asking Python's check_password_hash to accept it.

public static partial class WerkzeugPasswordGenerator
{
    public const int DefaultN = 32768;
    public const int DefaultR = 8;
    public const int DefaultP = 1;
    public const int DefaultDkLen = 64;

    public static string Generate(string password, int n = DefaultN, int r = DefaultR, int p = DefaultP)
    {
        byte[] saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(60);
        string salt = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(saltBytes))
                             .ToLowerInvariant();

        byte[] derived = ScryptUtil.Scrypt(
            System.Text.Encoding.UTF8.GetBytes(password),
            System.Text.Encoding.UTF8.GetBytes(salt),
            n, r, p, DefaultDkLen);

        string hash = Convert.ToHexString(derived).ToLowerInvariant();
        return "scrypt:" + n + ":" + r + ":" + p + "$" + salt + "$" + hash;
    }
}
