using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using Valuator.Shared;

namespace EventsLogger;

public sealed class EventSubscriber(IConfiguration configuration, ILogger<EventSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string subscriber = configuration["SubscriberId"] ?? "logger1";
        if (!EventBus.Subscribers.Contains(subscriber))
            throw new InvalidOperationException("SubscriberId must be logger1 or logger2; use a distinct ID for each logger.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = RankQueue.CreateFactory(configuration["RabbitMQ:Host"] ?? "localhost",
                    configuration["RabbitMQ:User"] ?? "valuator", configuration["RabbitMQ:Password"] ?? "valuator-local");
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await EventBus.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 20, false, stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var message = CalculationEvent.Parse(delivery.Body.Span);
                        // Structured JSON includes Type, TextId, Value, Metric and stable EventId.
                        // Console output precedes ack. Redelivery may legitimately repeat a log line.
                        Console.WriteLine($"EVENT {subscriber} {JsonSerializer.Serialize(message)}");
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (JsonException exception)
                    {
                        logger.LogWarning(exception, "Invalid event; moving to failed queue.");
                        await channel.BasicRejectAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Logger interrupted; unacknowledged event will be redelivered.");
                        disconnected.TrySetResult();
                    }
                };
                await channel.BasicConsumeAsync(EventBus.Queue(subscriber), autoAck: false,
                    consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("{Subscriber} subscribed to both calculation events via {Queue}.", subscriber, EventBus.Queue(subscriber));
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Event subscription disconnected; retrying."); }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
