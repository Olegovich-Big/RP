using RabbitMQ.Client;

namespace Valuator.Shared;

public static class EventBus
{
    public const string Exchange = "valuator.events";
    public static readonly string[] Subscribers = ["logger1", "logger2"];
    public static string Queue(string subscriber) => $"valuator.events.{subscriber}";

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true,
            autoDelete: false, cancellationToken: ct);
        // Declare both durable subscriptions before publication, even if a logger is offline.
        foreach (string subscriber in Subscribers)
        {
            string queue = Queue(subscriber);
            await channel.QueueDeclareAsync(queue + ".failed", durable: true, exclusive: false,
                autoDelete: false, cancellationToken: ct);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false,
                autoDelete: false, arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queue + ".failed"
                }, cancellationToken: ct);
            await channel.QueueBindAsync(queue, Exchange, routingKey: "", cancellationToken: ct);
        }
    }
}
