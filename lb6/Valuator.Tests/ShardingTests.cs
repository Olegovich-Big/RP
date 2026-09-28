using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Valuator.Shared;

namespace Valuator.Tests;

public class CountryTests
{
    [Theory]
    [InlineData("Russia", "RU")]
    [InlineData("France", "EU")]
    [InlineData("Germany", "EU")]
    [InlineData("UAE", "ASIA")]
    [InlineData("India", "ASIA")]
    public void RoutesSupportedCountries(string country, string region)
        => Assert.Equal(region, Countries.RegionFor(country));

    [Theory]
    [InlineData("")]
    [InlineData("USA")]
    [InlineData("EU")]
    [InlineData("russia")]
    public void RejectsUnknownCountries(string country)
        => Assert.Throws<ArgumentException>(() => Countries.RegionFor(country));
}

public sealed class ShardingFactAttribute : FactAttribute
{
    public ShardingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VALUATOR_TEST_SHARDS") != "1")
            Skip = "Set VALUATOR_TEST_SHARDS=1 with four isolated Redis test servers on DB_MAIN/DB_RU/DB_EU/DB_ASIA.";
    }
}

public class ShardingTests
{
    [ShardingFact]
    public async Task RoutesAllDataAndOutboxesAndPreservesAtomicDuplicateDetection()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var log = new CaptureLogger();
        using var connections = new ShardConnections(config, log);
        var store = new EvaluationStore(connections);
        var ids = new Dictionary<string, string>();
        string text = "Sharding test Ё " + Guid.NewGuid();
        try
        {
            foreach (var (country, region) in Countries.Regions)
            {
                string id = await store.SaveAsync(text, country);
                ids.Add(id, region);
                Assert.Equal(region, (string?)await connections.Main.StringGetAsync(id));
                var result = await store.GetAsync(id);
                Assert.NotNull(result);
                Assert.Equal(country, result.Country);
                Assert.Equal(region, result.Region);
                Assert.Equal(country is "Germany" or "India" ? 1 : 0, result.Similarity);
                Assert.Null(result.Rank);
                Assert.Equal(text, await store.GetTextAsync(id));
                Assert.Contains((RedisValue)id, await store.GetPendingAsync());
                Assert.NotNull(await store.GetEventAsync(CalculationEvent.SimilarityCalculated, id));
                Assert.True(await store.CompleteAsync(id, 0.5, "test-worker"));
                Assert.False(await store.CompleteAsync(id, 1, "duplicate-worker"));
                Assert.Equal(0.5, (await store.GetAsync(id))!.Rank);
                Assert.Equal("test-worker", (await store.GetAsync(id))!.Worker);
                Assert.NotNull(await store.GetEventAsync(CalculationEvent.RankCalculated, id));

                foreach (var other in ShardConnections.Regions)
                {
                    var db = connections.Shard(other);
                    foreach (var prefix in new[] { "TEXT-", "COUNTRY-", "REGION-", "SIMILARITY-", "RANK-", "WORKER-" })
                        Assert.Equal(other == region, await db.KeyExistsAsync(prefix + id));
                    Assert.Equal(other == region, await db.SetContainsAsync(EvaluationStore.PendingJobs, id));
                    foreach (var type in new[] { CalculationEvent.SimilarityCalculated, CalculationEvent.RankCalculated })
                        Assert.Equal(other == region, await db.HashExistsAsync(EvaluationStore.EventOutbox(type), id));
                }
                Assert.True(await store.MarkPublishedAsync(id));
                Assert.True(await store.MarkEventPublishedAsync(CalculationEvent.SimilarityCalculated, id));
                Assert.True(await store.MarkEventPublishedAsync(CalculationEvent.RankCalculated, id));
                Assert.False(await store.CompleteAsync(id, 1, "retry"));
                Assert.Null(await store.GetEventAsync(CalculationEvent.RankCalculated, id));
                Assert.Contains(log.Messages, message => message == $"LOOKUP: {id}, {region}");
            }

            string parallelText = text + " concurrent";
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(parallelText, "France")));
            foreach (var id in concurrent) ids.Add(id, "EU");
            var results = await Task.WhenAll(concurrent.Select(store.GetAsync));
            Assert.Single(results.Where(result => result!.Similarity == 0));
            Assert.Equal(7, results.Count(result => result!.Similarity == 1));
            var eventIds = new List<string>();
            await foreach (var entry in store.GetPendingEventsAsync(CalculationEvent.SimilarityCalculated))
                eventIds.Add(entry.Name.ToString());
            Assert.All(concurrent, id => Assert.Contains(id, eventIds));

            // MAIN must contain only GUID -> valid region entries, even after processing/outbox updates.
            var mainKeys = (RedisResult[])(await connections.Main.ExecuteAsync("KEYS", "*"))!;
            Assert.NotNull(mainKeys);
            foreach (var key in mainKeys)
            {
                Assert.True(Guid.TryParseExact(key.ToString(), "D", out _));
                Assert.Equal(RedisType.String, await connections.Main.KeyTypeAsync(key.ToString()));
                Assert.Contains((string)(await connections.Main.StringGetAsync(key.ToString()))!, ShardConnections.Regions);
            }

            string missing = Guid.NewGuid().ToString();
            Assert.Null(await store.GetAsync(missing));
            Assert.Null(await store.GetTextAsync(missing));
            Assert.False(await store.CompleteAsync(missing, 1, "worker"));
            Assert.Null(await store.GetEventAsync(CalculationEvent.RankCalculated, missing));
            Assert.False(await store.MarkPublishedAsync(missing));
        }
        finally
        {
            foreach (var (id, region) in ids)
            {
                var db = connections.Shard(region);
                foreach (var prefix in new[] { "TEXT-", "COUNTRY-", "REGION-", "SIMILARITY-", "RANK-", "WORKER-" })
                    await db.KeyDeleteAsync(prefix + id);
                await db.SetRemoveAsync(EvaluationStore.PendingJobs, id);
                foreach (var type in new[] { CalculationEvent.SimilarityCalculated, CalculationEvent.RankCalculated })
                    await db.HashDeleteAsync(EvaluationStore.EventOutbox(type), id);
                await connections.Main.KeyDeleteAsync(id);
            }
            foreach (var region in ShardConnections.Regions)
                await connections.Shard(region).SetRemoveAsync("VALUATOR-TEXTS", new RedisValue[] { text, text + " concurrent" });
        }
    }

    private sealed class CaptureLogger : ILogger<ShardConnections>
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
