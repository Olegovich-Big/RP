using System.Text;
using RabbitMQ.Client;

namespace Valuator.Shared;

public static class RankQueue
{
    public const string Name = "valuator.processing.rank";
    public const string Failed = Name + ".failed";

    public static ConnectionFactory CreateFactory(string host, string user, string password) => new()
    {
        HostName = host,
        UserName = user,
        Password = password,
        AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(Failed, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(Name, durable: true, exclusive: false,
            autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Failed
            }, cancellationToken: ct);
    }

    public static string? ParseId(ReadOnlySpan<byte> body)
    {
        if (body.Length != 36) return null;
        return Guid.TryParseExact(Encoding.UTF8.GetString(body), "D", out var id)
            ? id.ToString() : null;
    }
}
