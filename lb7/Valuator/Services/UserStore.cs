using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using StackExchange.Redis;

namespace Valuator.Services;

public sealed record Account(string Id, string Login, string PasswordHash);

public sealed class UserStore(IConnectionMultiplexer connection, IPasswordHasher<Account> hasher)
{
    public static string? NormalizeLogin(string? login)
        => login is not null && Regex.IsMatch(login, "\\A[a-zA-Z0-9_]{3,32}\\z")
            ? login.ToUpperInvariant() : null;

    public static bool ValidPassword(string? password) => password is { Length: >= 8 and <= 128 };

    public async Task<Account?> RegisterAsync(string login, string password)
    {
        string normalized = NormalizeLogin(login) ?? throw new ArgumentException("Invalid login.", nameof(login));
        if (!ValidPassword(password)) throw new ArgumentException("Password must contain 8–128 characters.", nameof(password));
        var account = new Account(Guid.NewGuid().ToString(), login, "");
        account = account with { PasswordHash = hasher.HashPassword(account, password) };
        // A single conditional write guarantees unique logins across web replicas.
        bool created = await connection.GetDatabase().StringSetAsync("USER-" + normalized,
            JsonSerializer.Serialize(account), when: When.NotExists);
        return created ? account : null;
    }

    public async Task<Account?> AuthenticateAsync(string login, string password)
    {
        string? normalized = NormalizeLogin(login);
        if (normalized is null || !ValidPassword(password)) return null;
        var value = await connection.GetDatabase().StringGetAsync("USER-" + normalized);
        if (value.IsNull) return null;
        var account = JsonSerializer.Deserialize<Account>(value.ToString())!;
        var result = hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : account;
    }
}
