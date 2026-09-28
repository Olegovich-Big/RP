using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Valuator.Shared;

// Four independent Redis servers; MAIN contains only id -> region strings.
public sealed class ShardConnections : IDisposable
{
    public static IReadOnlyList<string> Regions { get; } = Array.AsReadOnly(new[] { "RU", "EU", "ASIA" });
    private readonly Dictionary<string, ConnectionMultiplexer> connections = new();
    private readonly ILogger<ShardConnections> logger;

    public ShardConnections(IConfiguration configuration, ILogger<ShardConnections> logger)
    {
        this.logger = logger;
        try
        {
            foreach (var (name, port) in new[] { ("MAIN", 6000), ("RU", 6001), ("EU", 6002), ("ASIA", 6003) })
            {
                var options = ConfigurationOptions.Parse(configuration["DB_" + name] ?? $"localhost:{port}");
                options.AbortOnConnectFail = false;
                connections.Add(name, ConnectionMultiplexer.Connect(options));
            }
        }
        catch { Dispose(); throw; }
    }

    public IDatabase Main => connections["MAIN"].GetDatabase();
    public IDatabase Shard(string region) => Regions.Contains(region)
        ? connections[region].GetDatabase() : throw new InvalidOperationException("Invalid shard map entry.");

    public async Task<IDatabase?> FindAsync(string id)
    {
        var region = await Main.StringGetAsync(id);
        logger.LogInformation("LOOKUP: {TextId}, {ShardKey}", id, region.IsNull ? "NOT_FOUND" : region.ToString());
        return region.IsNull ? null : Shard(region.ToString());
    }

    public void Dispose()
    {
        foreach (var connection in connections.Values) connection.Dispose();
    }
}
