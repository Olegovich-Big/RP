using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using Valuator.Shared;

namespace RankCalculator;

public sealed class Worker(EvaluationStore store, EventPublisher events, IConfiguration configuration, ILogger<Worker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string worker = configuration["WorkerName"] ?? Environment.MachineName;
        int delay = Math.Max(0, configuration.GetValue<int>("ProcessingDelayMs"));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = RankQueue.CreateFactory(configuration["RabbitMQ:Host"] ?? "localhost",
                    configuration["RabbitMQ:User"] ?? "valuator", MiddlewareSettings.Required(configuration, "RabbitMQ:Password"));
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RankQueue.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 1, global: false, cancellationToken: stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    // Decode before awaiting: the client's message buffer is valid only during this callback.
                    string? id = RankQueue.ParseId(delivery.Body.Span);
                    if (id is null)
                    {
                        logger.LogWarning("Rejected malformed rank job.");
                        await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                        return;
                    }
                    try
                    {
                        string? text = await store.GetTextAsync(id);
                        if (string.IsNullOrEmpty(text))
                        {
                            logger.LogWarning("Text for job {Id} is missing; moving to failed queue.", id);
                            await channel.BasicRejectAsync(delivery.DeliveryTag, false, stoppingToken);
                            return;
                        }
                        if (delay > 0) await Task.Delay(delay, stoppingToken);
                        double rank = TextEvaluator.CalculateRank(text);
                        bool saved = await store.CompleteAsync(id, rank, worker);
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await events.PublishAsync(CalculationEvent.RankCalculated, id, timeout.Token);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Rank event {Id} remains in the outbox.", id);
                        }
                        // Only acknowledge after Redis has accepted the result.
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        logger.LogInformation("Worker {Worker}: job {Id}, rank {Rank}, saved {Saved}", worker, id, rank, saved);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (RedisException exception)
                    {
                        logger.LogWarning(exception, "Redis unavailable; requeueing {Id}.", id);
                        await Task.Delay(1000, stoppingToken);
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Processing interrupted; reconnecting to redeliver {Id}.", id);
                        disconnected.TrySetResult();
                    }
                };
                await channel.BasicConsumeAsync(RankQueue.Name, autoAck: false, consumer: consumer,
                    cancellationToken: stoppingToken);
                logger.LogInformation("Worker {Worker} consuming {Queue} with prefetch=1.", worker, RankQueue.Name);
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Worker connection failed; retrying."); }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
