using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Valuator.Shared;

public static class MiddlewareSettings
{
    public static string Required(IConfiguration configuration, string key)
        => !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]!
            : throw new InvalidOperationException($"Set configuration parameter {key} before starting the service.");

    public static ConfigurationOptions Redis(IConfiguration configuration)
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? "localhost:6379");
        options.Password = Required(configuration, "Redis:Password");
        options.AbortOnConnectFail = false;
        return options;
    }
}
