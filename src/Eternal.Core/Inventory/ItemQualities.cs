namespace Eternal.Core.Inventory;

public static class ItemQualities
{
    /// <summary>Every quality an item can have, as Steam names them, most common first.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "Unique", "Strange", "Unusual", "Vintage", "Genuine", "Haunted", "Collector's", "Decorated Weapon", "Community", "Self-Made", "Valve", "Normal",
    ];

    /// <summary>The full list, followed by anything Steam sent that isn't on it.</summary>
    public static IReadOnlyList<string> WithFound(IEnumerable<string> found) =>
        [.. All, .. found.Except(All).Distinct().Order(StringComparer.Ordinal)];
}
