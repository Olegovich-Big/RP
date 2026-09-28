namespace Valuator.Shared;

public static class Countries
{
    public static IReadOnlyDictionary<string, string> Regions { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Russia"] = "RU", ["France"] = "EU", ["Germany"] = "EU",
                ["UAE"] = "ASIA", ["India"] = "ASIA"
            });

    public static string RegionFor(string country) =>
        Regions.TryGetValue(country, out var region) ? region
            : throw new ArgumentException("Unsupported country.", nameof(country));
}
