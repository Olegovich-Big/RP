using Microsoft.AspNetCore.Identity;
using StackExchange.Redis;
using Valuator.Services;

namespace Valuator.Tests;

public class UserStoreTests
{
    [Theory]
    [InlineData("alice", "ALICE")]
    [InlineData("Alice_123", "ALICE_123")]
    [InlineData("ab", null)]
    [InlineData("", null)]
    [InlineData(" alice", null)]
    [InlineData("a:b", null)]
    [InlineData("alice\n", null)]
    public void LoginNormalizationIsUnambiguous(string login, string? expected)
        => Assert.Equal(expected, UserStore.NormalizeLogin(login));

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void PasswordLengthIsBounded(int length, bool expected)
        => Assert.Equal(expected, UserStore.ValidPassword(new string('a', length)));

    [RedisFact]
    public async Task RegistrationIsUniqueAndPasswordsAreHashed()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("VALUATOR_TEST_REDIS")!);
        var store = new UserStore(connection, new PasswordHasher<Account>());
        string login = "test_" + Guid.NewGuid().ToString("N")[..20];
        string password = Guid.NewGuid().ToString("N");
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
                store.RegisterAsync(i % 2 == 0 ? login : login.ToUpperInvariant(), password)));
            var account = Assert.Single(results.Where(account => account is not null))!;
            string stored = (await connection.GetDatabase().StringGetAsync("USER-" + login.ToUpperInvariant())).ToString();
            Assert.DoesNotContain(password, stored);
            Assert.NotEqual(password, account.PasswordHash);
            Assert.Equal(account.Id, (await store.AuthenticateAsync(login, password))!.Id);
            Assert.Equal(account.Id, (await store.AuthenticateAsync(login.ToUpperInvariant(), password))!.Id);
            Assert.Null(await store.AuthenticateAsync(login, password + "wrong"));
            Assert.Null(await store.AuthenticateAsync("missing_" + Guid.NewGuid().ToString("N")[..15], password));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync("USER-" + login.ToUpperInvariant()); }
    }
}
