using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Valuator.Shared;

// Each host retries only events it owns: web -> Similarity, workers -> Rank.
public sealed class EventOutboxDispatcher(string type, EvaluationStore store,
    EventPublisher publisher, ILogger<EventOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var entry in store.GetPendingEventsAsync(type).WithCancellation(stoppingToken))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await publisher.PublishAsync(type, entry.Name.ToString(), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Retrying {Type} event outbox.", type); }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
