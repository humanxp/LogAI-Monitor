// Verifies a password against the hash stored in Redis. Used to prove that the
// rewrite can authenticate the accounts an existing deployment already has.
//
//   dotnet LogAI.Web.dll --verify-password <username> <password>

using LogAI.Core.Auth;
using LogAI.Core.Store;
using StackExchange.Redis;

namespace LogAI.Web;

internal static class PasswordCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        string username = args[1];
        string password = args[2];

        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"),
            Database = int.Parse(Environment.GetEnvironmentVariable("REDIS_DB") ?? "0"),
        };
        using var store = new RedisStore(options);

        string lookup = Keys.UserByName(username);
        var id = await store.Db.StringGetAsync(lookup);
        if (!id.HasValue)
        {
            Console.WriteLine($"user '{username}' not found (looked up {lookup})");
            return 1;
        }

        var hash = await store.Db.HashGetAsync(id.ToString(), "password_hash");
        if (!hash.HasValue)
        {
            Console.WriteLine($"user record {id} has no password_hash");
            return 1;
        }

        string stored = hash.ToString();
        string method = stored.Split('$')[0];
        bool ok = WerkzeugPassword.Verify(password, stored);

        Console.WriteLine($"user       : {username} ({id})");
        Console.WriteLine($"hash method: {method}");
        Console.WriteLine($"supported  : {WerkzeugPassword.IsSupported(stored)}");
        Console.WriteLine($"verify     : {(ok ? "PASS" : "FAIL")}   (password {(ok ? "accepted" : "rejected")})");

        // A wrong password must be rejected, otherwise "always true" would pass.
        bool wrong = WerkzeugPassword.Verify(password + "-wrong", stored);
        Console.WriteLine($"negative   : {(wrong ? "FAIL (wrong password accepted!)" : "PASS (wrong password rejected)")}");

        return ok && !wrong ? 0 : 1;
    }
}
