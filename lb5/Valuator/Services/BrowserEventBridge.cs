using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Valuator.Hubs;
using Valuator.Shared;

namespace Valuator.Services;

// Web replicas compete for one durable bridge queue. Redis backplane fans out to all hub instances.
public sealed class BrowserEventBridge(ConnectionFactory factory, EvaluationStore store,
    IHubContext<EvaluationHub> hub, ILogger<BrowserEventBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await EventBus.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 10, false, stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var message = CalculationEvent.Parse(delivery.Body.Span);
                        var result = await store.GetAsync(message.TextId);
                        if (result is not null)
                            await hub.Clients.Group(EvaluationHub.Group(message.TextId)).SendAsync("EvaluationUpdated",
                                new EvaluationUpdate(message.TextId, result.Rank, result.Similarity, result.Worker), stoppingToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (JsonException exception)
                    {
                        logger.LogWarning(exception, "Invalid browser event.");
                        await channel.BasicRejectAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Browser notification failed; reconnecting to retry.");
                        disconnected.TrySetResult();
                    }
                };
                await channel.BasicConsumeAsync(EventBus.Queue(EventBus.BrowserSubscriber), autoAck: false,
                    consumer: consumer, cancellationToken: stoppingToken);
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Browser event bridge disconnected; retrying."); }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
