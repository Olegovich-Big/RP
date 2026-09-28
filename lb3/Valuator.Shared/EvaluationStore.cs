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
        return similarity
        """;

    public async Task<string> SaveAsync(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        string id = Guid.NewGuid().ToString();
        await connection.GetDatabase().ScriptEvaluateAsync(SaveScript,
            new RedisKey[] { "VALUATOR-TEXTS", "TEXT-" + id, "SIMILARITY-" + id, PendingJobs },
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
            return 1
            """;
        var result = await connection.GetDatabase().ScriptEvaluateAsync(script,
            new RedisKey[] { "TEXT-" + id, "RANK-" + id, "WORKER-" + id },
            new RedisValue[] { rank.ToString("R", CultureInfo.InvariantCulture), worker });
        return (int)result == 1;
    }

    public Task<RedisValue[]> GetPendingAsync() => connection.GetDatabase().SetRandomMembersAsync(PendingJobs, 100);
    public Task<bool> MarkPublishedAsync(string id) => connection.GetDatabase().SetRemoveAsync(PendingJobs, id);
}
