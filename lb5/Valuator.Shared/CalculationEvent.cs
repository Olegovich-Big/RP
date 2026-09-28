using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valuator.Shared;

public sealed record CalculationEvent(
    [property: JsonRequired] string Type,
    [property: JsonRequired] string TextId,
    [property: JsonRequired] double Value)
{
    public const string RankCalculated = "RankCalculated";
    public const string SimilarityCalculated = "SimilarityCalculated";
    public string EventId => $"{Type}:{TextId}";
    public string Metric => Type == RankCalculated ? "rank" : "similarity";

    public static bool IsType(string type) => type is RankCalculated or SimilarityCalculated;

    public static CalculationEvent Parse(ReadOnlySpan<byte> json)
    {
        var message = JsonSerializer.Deserialize<CalculationEvent>(json)
            ?? throw new JsonException("Missing event.");
        if (!IsType(message.Type) || !Guid.TryParseExact(message.TextId, "D", out _)
            || !double.IsFinite(message.Value) || message.Value < 0 || message.Value > 1
            || (message.Type == SimilarityCalculated && message.Value is not (0 or 1)))
            throw new JsonException("Invalid event type, text ID or metric value.");
        return message;
    }
}
