using System.Text;
using System.Text.Json;
using Valuator.Shared;

namespace Valuator.Tests;

public class CalculationEventTests
{
    [Theory]
    [InlineData(CalculationEvent.RankCalculated, 0.25, "rank")]
    [InlineData(CalculationEvent.SimilarityCalculated, 0, "similarity")]
    [InlineData(CalculationEvent.SimilarityCalculated, 1, "similarity")]
    public void SerializesEventContext(string type, double value, string metric)
    {
        var message = new CalculationEvent(type, Guid.NewGuid().ToString(), value);
        var parsed = CalculationEvent.Parse(JsonSerializer.SerializeToUtf8Bytes(message));
        Assert.Equal(message, parsed);
        Assert.Equal(metric, parsed.Metric);
        Assert.Equal($"{type}:{message.TextId}", parsed.EventId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"Type\":\"RankCalculated\",\"TextId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"Type\":\"Other\",\"TextId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":0}")]
    [InlineData("{\"Type\":\"RankCalculated\",\"TextId\":\"bad\",\"Value\":0}")]
    [InlineData("{\"Type\":\"RankCalculated\",\"TextId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":2}")]
    [InlineData("{\"Type\":\"SimilarityCalculated\",\"TextId\":\"00000000-0000-0000-0000-000000000000\",\"Value\":0.5}")]
    public void RejectsInvalidEvent(string json)
        => Assert.Throws<JsonException>(() => CalculationEvent.Parse(Encoding.UTF8.GetBytes(json)));
}
