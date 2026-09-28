using Valuator.Shared;
using RankCalculator;
using StackExchange.Redis;

namespace Valuator.Tests;

public class TextEvaluatorTests
{
    [Theory]
    [InlineData("abcXYZабвЯЁё", 0)]
    [InlineData("123 !\n", 1)]
    [InlineData("Hello, мир!", 0.2727272727272727)]
    [InlineData(" ", 1)]
    [InlineData("é中", 1)]
    [InlineData("a😀", 0.5)]
    public void CountsOnlyRussianAndLatinLetters(string text, double expected)
        => Assert.Equal(expected, TextEvaluator.CalculateRank(text), 12);

    [Fact]
    public void RejectsEmptyText()
        => Assert.Throws<ArgumentException>(() => TextEvaluator.CalculateRank(""));

    [Fact]
    public void RejectsNullText()
        => Assert.Throws<ArgumentNullException>(() => TextEvaluator.CalculateRank(null!));
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALUATOR_TEST_REDIS")))
            Skip = "Set VALUATOR_TEST_REDIS to run against a real Redis instance.";
    }
}

public class RedisIntegrationTests
{
    [RedisFact]
    public async Task StoresResultsAndDetectsConcurrentExactDuplicates()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("VALUATOR_TEST_REDIS")!);
        var store = new EvaluationStore(connection);
        string text = "Test Ё " + Guid.NewGuid();
        var ids = new List<string>();
        try
        {
            ids.AddRange(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SaveAsync(text))));
            var results = await Task.WhenAll(ids.Select(store.GetAsync));
            Assert.Single(results.Where(result => result!.Similarity == 0));
            Assert.Equal(7, results.Count(result => result!.Similarity == 1));
            Assert.All(results, result => Assert.Null(result!.Rank));
            Assert.True(await store.CompleteAsync(ids[0], TextEvaluator.CalculateRank(text), "worker1"));
            Assert.False(await store.CompleteAsync(ids[0], 0, "worker2"));
            var completed = await store.GetAsync(ids[0]);
            Assert.Equal(TextEvaluator.CalculateRank(text), completed!.Rank);
            Assert.Equal("worker1", completed.Worker);
            Assert.Equal(text, (string?)await connection.GetDatabase().StringGetAsync("TEXT-" + ids[0]));
            Assert.Null(await store.GetAsync(Guid.NewGuid().ToString()));

            string differentId = await store.SaveAsync(text + " ");
            ids.Add(differentId);
            Assert.Equal(0, (await store.GetAsync(differentId))!.Similarity);
        }
        finally
        {
            var database = connection.GetDatabase();
            foreach (string id in ids)
            {
                await database.KeyDeleteAsync(new RedisKey[] { "TEXT-" + id, "RANK-" + id, "SIMILARITY-" + id, "WORKER-" + id });
                await database.SetRemoveAsync(EvaluationStore.PendingJobs, id);
            }
            await database.SetRemoveAsync("VALUATOR-TEXTS", new RedisValue[] { text, text + " " });
        }
    }
}
