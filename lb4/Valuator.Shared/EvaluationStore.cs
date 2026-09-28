using System.Globalization;
using StackExchange.Redis;
namespace Valuator.Shared;

public sealed record Evaluation(double? Rank, double Similarity, string? Worker);

public sealed class EvaluationStore(IConnectionMultiplexer connection)
{
    public const string PendingJobs = "VALUATOR-RANK-OUTBOX";
    // Exact duplicate detection and persistence must be atomic for concurrent submissions.
    private const string SaveScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        local similarity = 1 - added
        redis.call('MSET', KEYS[2], ARGV[1], KEYS[3], similarity)
        redis.call('SADD', KEYS[4], ARGV[2])
        redis.call('HSET', KEYS[5], ARGV[2], cjson.encode({Type='SimilarityCalculated', TextId=ARGV[2], Value=similarity}))
        return similarity
        """;

    public async Task<string> SaveAsync(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        string id = Guid.NewGuid().ToString();
        await connection.GetDatabase().ScriptEvaluateAsync(SaveScript,
            new RedisKey[] { "VALUATOR-TEXTS", "TEXT-" + id, "SIMILARITY-" + id, PendingJobs, EventOutbox(CalculationEvent.SimilarityCalculated) },
            new RedisValue[] { text, id });
        return id;
    }

    public async Task<Evaluation?> GetAsync(string id)
    {
        RedisValue[] values = await connection.GetDatabase().StringGetAsync(
            new RedisKey[] { "RANK-" + id, "SIMILARITY-" + id, "WORKER-" + id });
        if (values[1].IsNull) return null;
        return new Evaluation(
            values[0].IsNull ? null : double.Parse(values[0].ToString(), CultureInfo.InvariantCulture),
            double.Parse(values[1].ToString(), CultureInfo.InvariantCulture),
            values[2].IsNull ? null : values[2].ToString());
    }

    public async Task<string?> GetTextAsync(string id)
    {
        RedisValue value = await connection.GetDatabase().StringGetAsync("TEXT-" + id);
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
        var result = await connection.GetDatabase().ScriptEvaluateAsync(script,
            new RedisKey[] { "TEXT-" + id, "RANK-" + id, "WORKER-" + id, EventOutbox(CalculationEvent.RankCalculated) },
            new RedisValue[] { rank.ToString("R", CultureInfo.InvariantCulture), worker, id });
        return (int)result == 1;
    }

    public Task<RedisValue[]> GetPendingAsync() => connection.GetDatabase().SetRandomMembersAsync(PendingJobs, 100);
    public Task<bool> MarkPublishedAsync(string id) => connection.GetDatabase().SetRemoveAsync(PendingJobs, id);

    public static string EventOutbox(string type)
    {
        if (!CalculationEvent.IsType(type)) throw new ArgumentException("Unknown event type.", nameof(type));
        return "VALUATOR-EVENT-OUTBOX-" + type;
    }

    public async Task<string?> GetEventAsync(string type, string id)
    {
        var value = await connection.GetDatabase().HashGetAsync(EventOutbox(type), id);
        return value.IsNull ? null : value.ToString();
    }

    public IAsyncEnumerable<HashEntry> GetPendingEventsAsync(string type)
        => connection.GetDatabase().HashScanAsync(EventOutbox(type), pageSize: 100);

    public Task<bool> MarkEventPublishedAsync(string type, string id)
        => connection.GetDatabase().HashDeleteAsync(EventOutbox(type), id);
}
