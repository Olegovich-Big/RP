using Microsoft.AspNetCore.DataProtection;

namespace Valuator.Tests;

public class DataProtectionTests
{
    [Fact]
    public void ReplicasCanDecryptTokensWithSharedKeysAndApplicationName()
    {
        var directory = Directory.CreateTempSubdirectory("valuator-pa2-");
        try
        {
            var first = DataProtectionProvider.Create(directory,
                options => options.SetApplicationName("Valuator.PA2"));
            string token = first.CreateProtector("antiforgery-test").Protect("form-token");
            var second = DataProtectionProvider.Create(directory,
                options => options.SetApplicationName("Valuator.PA2"));
            Assert.Equal("form-token", second.CreateProtector("antiforgery-test").Unprotect(token));
            var otherApplication = DataProtectionProvider.Create(directory,
                options => options.SetApplicationName("DifferentApp"));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
                otherApplication.CreateProtector("antiforgery-test").Unprotect(token));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
