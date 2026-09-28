using System.Text;
using RabbitMQ.Client;

namespace Valuator.Shared;

public sealed class EventPublisher(EvaluationStore store, ConnectionFactory factory)
{
    public async Task PublishAsync(string type, string id, CancellationToken ct)
    {
        string? json = await store.GetEventAsync(type, id);
        if (json is null) return;
        byte[] body = Encoding.UTF8.GetBytes(json);
        var message = CalculationEvent.Parse(body);
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await EventBus.DeclareAsync(channel, ct);
        await channel.BasicPublishAsync(exchange: EventBus.Exchange, routingKey: "", mandatory: true,
            basicProperties: new BasicProperties
            {
                Persistent = true, ContentType = "application/json", Type = type, MessageId = message.EventId
            }, body: body, cancellationToken: ct);
        await store.MarkEventPublishedAsync(type, id);
    }
}
