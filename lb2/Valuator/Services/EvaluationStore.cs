using System.Globalization;
using StackExchange.Redis;
namespace Valuator.Services;

public sealed record Evaluation(double Rank, double Similarity);

public sealed class EvaluationStore(IConnectionMultiplexer connection)
{
    // Exact duplicate detection and persistence must be atomic for concurrent submissions.
    private const string SaveScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        local similarity = 1 - added
        redis.call('MSET', KEYS[2], ARGV[1], KEYS[3], ARGV[2], KEYS[4], similarity)
        return similarity
        """;

    public async Task<string> SaveAsync(string text)
    {
        double rank = TextEvaluator.CalculateRank(text);
        string id = Guid.NewGuid().ToString();
        await connection.GetDatabase().ScriptEvaluateAsync(SaveScript,
            new RedisKey[] { "VALUATOR-TEXTS", "TEXT-" + id, "RANK-" + id, "SIMILARITY-" + id },
            new RedisValue[] { text, rank.ToString("R", CultureInfo.InvariantCulture) });
        return id;
    }

    public async Task<Evaluation?> GetAsync(string id)
    {
        RedisValue[] values = await connection.GetDatabase().StringGetAsync(
            new RedisKey[] { "RANK-" + id, "SIMILARITY-" + id });
        if (values.Any(value => value.IsNull)) return null;
        return new Evaluation(
            double.Parse(values[0].ToString(), CultureInfo.InvariantCulture),
            double.Parse(values[1].ToString(), CultureInfo.InvariantCulture));
    }
}
