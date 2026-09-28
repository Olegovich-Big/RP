using System.Text;
using RabbitMQ.Client;
using Valuator.Shared;

namespace Valuator.Services;

public sealed class RankPublisher(EvaluationStore store, IConfiguration configuration,
    ILogger<RankPublisher> logger) : BackgroundService
{
    public async Task PublishAsync(string id, CancellationToken ct)
    {
        if (await store.GetTextAsync(id) is null) return;
        // A separate channel per publication avoids concurrent channel use by HTTP requests.
        var factory = RankQueue.CreateFactory(configuration["RabbitMQ:Host"] ?? "localhost",
            configuration["RabbitMQ:User"] ?? "valuator", configuration["RabbitMQ:Password"] ?? "valuator-local");
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await RankQueue.DeclareAsync(channel, ct);
        await channel.BasicPublishAsync(exchange: "", routingKey: RankQueue.Name, mandatory: true,
            basicProperties: new BasicProperties { Persistent = true, MessageId = id, ContentType = "text/plain" },
            body: Encoding.UTF8.GetBytes(id), cancellationToken: ct);
        // Remove only AFTER confirmation. Crashes may duplicate delivery, never discard pending work.
        await store.MarkPublishedAsync(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await store.GetPendingAsync())
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await PublishAsync(id.ToString(), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Rank jobs remain in Redis outbox; retrying.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
