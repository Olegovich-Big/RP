using System.Globalization;
using StackExchange.Redis;
namespace Valuator.Shared;

public sealed record Evaluation(double? Rank, double Similarity, string? Worker, string Country, string Region);

public sealed class EvaluationStore(ShardConnections connections)
{
    public const string PendingJobs = "VALUATOR-RANK-OUTBOX";
    // Exact duplicate detection and persistence must be atomic for concurrent submissions.
    private const string SaveScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        local similarity = 1 - added
        redis.call('MSET', KEYS[2], ARGV[1], KEYS[3], similarity, KEYS[6], ARGV[3], KEYS[7], ARGV[4])
        redis.call('SADD', KEYS[4], ARGV[2])
        redis.call('HSET', KEYS[5], ARGV[2], cjson.encode({Type='SimilarityCalculated', TextId=ARGV[2], Value=similarity}))
        return similarity
        """;

    public async Task<string> SaveAsync(string text, string country)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        string region = Countries.RegionFor(country);
        string id = Guid.NewGuid().ToString();
        // Register the route before making any job visible in the shard outbox.
        // A failed shard write can leave an unused map entry, never a job without a route.
        while (!await connections.Main.StringSetAsync(id, region, when: When.NotExists))
            id = Guid.NewGuid().ToString();
        var database = await connections.FindAsync(id) ?? throw new InvalidOperationException("Route missing.");
        await database.ScriptEvaluateAsync(SaveScript,
            new RedisKey[] { "VALUATOR-TEXTS", "TEXT-" + id, "SIMILARITY-" + id, PendingJobs, EventOutbox(CalculationEvent.SimilarityCalculated), "COUNTRY-" + id, "REGION-" + id },
            new RedisValue[] { text, id, country, region });
        return id;
    }

    public async Task<Evaluation?> GetAsync(string id)
    {
        var database = await connections.FindAsync(id);
        if (database is null) return null;
        RedisValue[] values = await database.StringGetAsync(
            new RedisKey[] { "RANK-" + id, "SIMILARITY-" + id, "WORKER-" + id, "COUNTRY-" + id, "REGION-" + id });
        if (values[1].IsNull) return null;
        return new Evaluation(
            values[0].IsNull ? null : double.Parse(values[0].ToString(), CultureInfo.InvariantCulture),
            double.Parse(values[1].ToString(), CultureInfo.InvariantCulture),
            values[2].IsNull ? null : values[2].ToString(), values[3].ToString(), values[4].ToString());
    }

    public async Task<string?> GetTextAsync(string id)
    {
        var database = await connections.FindAsync(id);
        if (database is null) return null;
        RedisValue value = await database.StringGetAsync("TEXT-" + id);
        return value.IsNull ? null : value.ToString();
    }

    public async Task<bool> CompleteAsync(string id, double rank, string worker)
    {
        // At-least-once delivery: a repeated message must not change the saved result.
        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            if redis.call('EXISTS', KEYS[2]) == 1 then return 0 end
            redis.call('MSET', KEYS[2], ARGV[1], KEYS[3], ARGV[2])
            redis.call('HSET', KEYS[4], ARGV[3], cjson.encode({Type='RankCalculated', TextId=ARGV[3], Value=tonumber(ARGV[1])}))
            return 1
            """;
        var database = await connections.FindAsync(id);
        if (database is null) return false;
        var result = await database.ScriptEvaluateAsync(script,
            new RedisKey[] { "TEXT-" + id, "RANK-" + id, "WORKER-" + id, EventOutbox(CalculationEvent.RankCalculated) },
            new RedisValue[] { rank.ToString("R", CultureInfo.InvariantCulture), worker, id });
        return (int)result == 1;
    }

    public async Task<RedisValue[]> GetPendingAsync()
    {
        var batches = await Task.WhenAll(ShardConnections.Regions.Select(region =>
            connections.Shard(region).SetRandomMembersAsync(PendingJobs, 100)));
        return batches.SelectMany(batch => batch).ToArray();
    }

    public async Task<bool> MarkPublishedAsync(string id)
    {
        var database = await connections.FindAsync(id);
        return database is not null && await database.SetRemoveAsync(PendingJobs, id);
    }

    public static string EventOutbox(string type)
    {
        if (!CalculationEvent.IsType(type)) throw new ArgumentException("Unknown event type.", nameof(type));
        return "VALUATOR-EVENT-OUTBOX-" + type;
    }

    public async Task<string?> GetEventAsync(string type, string id)
    {
        var database = await connections.FindAsync(id);
        if (database is null) return null;
        var value = await database.HashGetAsync(EventOutbox(type), id);
        return value.IsNull ? null : value.ToString();
    }

    public async IAsyncEnumerable<HashEntry> GetPendingEventsAsync(string type)
    {
        foreach (var region in ShardConnections.Regions)
            await foreach (var entry in connections.Shard(region).HashScanAsync(EventOutbox(type), pageSize: 100))
                yield return entry;
    }

    public async Task<bool> MarkEventPublishedAsync(string type, string id)
    {
        var database = await connections.FindAsync(id);
        return database is not null && await database.HashDeleteAsync(EventOutbox(type), id);
    }
}
