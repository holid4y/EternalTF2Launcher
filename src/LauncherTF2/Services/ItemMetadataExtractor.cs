namespace LauncherTF2.Services;

/// <summary>
/// Derives stable filterable attributes (Class list, Item Type, Slot) from the
/// Steam Community item description. Single source of truth — every filter UI
/// reads from here so the categorization stays consistent across screens.
/// </summary>
public static class ItemMetadataExtractor
{
    public static readonly string[] AllClasses =
    {
        "Scout", "Soldier", "Pyro", "Demoman", "Heavy",
        "Engineer", "Medic", "Sniper", "Spy", "Multi-Class"
    };

    /// <summary>
    /// Splits the Steam-extracted EquippedOn / class-tag string into a normalized
    /// set of TF2 class names. "All Class" / "Multi-Class" items resolve to "Multi-Class".
    /// </summary>
    public static IReadOnlyList<string> ExtractClasses(string? equippedOn)
    {
        if (string.IsNullOrWhiteSpace(equippedOn))
            return Array.Empty<string>();

        var raw = equippedOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in raw)
        {
            var match = AllClasses.FirstOrDefault(c => token.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                result.Add(match);
                continue;
            }

            // "All Class" / "Multi-Class" / catch-all wording from Steam
            if (token.Contains("All", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("Multi", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("Multi-Class");
            }
        }

        return result.OrderBy(c => Array.IndexOf(AllClasses, c)).ToArray();
    }

    /// <summary>
    /// Categorizes the item into a high-level filter bucket.
    /// Reads the verbose Type string Steam provides (e.g. "Level 3 Primary weapon").
    /// </summary>
    public static string ExtractItemType(string? type, string? name)
    {
        var combined = $"{type} {name}".ToLowerInvariant();

        if (Contains(combined, "primary weapon", "primary pda")) return "Primary";
        if (Contains(combined, "secondary weapon", "secondary pda")) return "Secondary";
        if (Contains(combined, "melee weapon")) return "Melee";
        if (Contains(combined, " pda")) return "PDA";
        if (Contains(combined, "taunt")) return "Taunt";
        if (Contains(combined, "tool")) return "Tool";
        if (Contains(combined, "crate", "case")) return "Crate";
        if (Contains(combined, "package")) return "Package";
        if (Contains(combined, "ticket", "pass")) return "Ticket";
        if (Contains(combined, "action")) return "Action";
        if (Contains(combined, "consumable")) return "Consumable";
        if (Contains(combined, "misc")) return "Misc";
        if (Contains(combined, "headgear", "hat", "cosmetic")) return "Cosmetic";

        return "Other";
    }

    /// <summary>
    /// Derives the equip slot. Returns null for items whose slot can't be determined
    /// so downstream views can still show the detail label without forcing a filter chip.
    /// </summary>
    public static string? ExtractSlot(string? type, string? name)
    {
        var combined = $"{type} {name}".ToLowerInvariant();

        if (Contains(combined, "primary weapon")) return "Primary";
        if (Contains(combined, "secondary weapon")) return "Secondary";
        if (Contains(combined, "melee weapon")) return "Melee";
        if (Contains(combined, " pda")) return "PDA";
        if (Contains(combined, "taunt")) return "Taunt";
        if (Contains(combined, "action")) return "Action";
        if (Contains(combined, "headgear", "hat")) return "Head";
        if (Contains(combined, "misc")) return "Misc";

        return null;
    }

    private static bool Contains(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
}
