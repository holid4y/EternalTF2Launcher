using System.Text.RegularExpressions;

namespace LauncherTF2.Models.Inventory;

public enum ItemDescriptionLineKind
{
    /// <summary>Game-mechanic line: attribute, killstreak flag, grade, Strange stat counter.</summary>
    Attribute,
    /// <summary>Player-written flavor text, usually wrapped in straight quotes.</summary>
    Custom,
    /// <summary>"Gift from: X" line shown for gifted items.</summary>
    Gift,
    /// <summary>"Date Received:" line or the bare time-of-day follow-up.</summary>
    Date,
}

public sealed class ItemDescriptionLine
{
    public string Text { get; init; } = string.Empty;
    public ItemDescriptionLineKind Kind { get; init; }
}

/// <summary>
/// Splits Steam's joined description string into structured lines and drops
/// the collection / set item list that follows a "X Collection" header — those
/// are dozens of unrelated item names and aren't useful in a detail panel.
/// </summary>
public static class ItemDescriptionParser
{
    private static readonly Regex CollectionHeaderRegex =
        new(@"^[A-Z0-9].*\sCollection$", RegexOptions.Compiled);

    private static readonly Regex BareTimestampRegex =
        new(@"^\(\d{1,2}:\d{2}:\d{2}\)", RegexOptions.Compiled);

    public static IReadOnlyList<ItemDescriptionLine> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<ItemDescriptionLine>();

        var result = new List<ItemDescriptionLine>();

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Collection headers ("Quarantined Collection", "Spectral Stash Collection")
            // mark the start of a long list of items belonging to that set. Stop here.
            if (CollectionHeaderRegex.IsMatch(line)) break;

            ItemDescriptionLineKind kind;

            if (line.Length > 2 && line.StartsWith('"') && line.EndsWith('"'))
                kind = ItemDescriptionLineKind.Custom;
            else if (line.StartsWith("Gift from:", StringComparison.OrdinalIgnoreCase))
                kind = ItemDescriptionLineKind.Gift;
            else if (line.StartsWith("Date Received:", StringComparison.OrdinalIgnoreCase) ||
                     BareTimestampRegex.IsMatch(line))
                kind = ItemDescriptionLineKind.Date;
            else
                kind = ItemDescriptionLineKind.Attribute;

            result.Add(new ItemDescriptionLine { Text = line, Kind = kind });
        }

        return result;
    }
}
