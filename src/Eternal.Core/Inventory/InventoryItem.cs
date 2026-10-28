using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eternal.Core.Inventory;

public sealed record DescriptionLine(string Text, string? Color);

public sealed partial record InventoryItem
{
    public string AssetId { get; init; } = "";
    public string ClassId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string MarketHashName { get; init; } = "";
    public string Quality { get; init; } = "Unique";
    public string QualityColor { get; init; } = "#7D6D00";
    public string NameColor { get; init; } = "#7D6D00";
    public string Type { get; init; } = "";
    public string Category { get; init; } = "";
    public string? Wear { get; init; }
    public string? Collection { get; init; }
    public IReadOnlyList<string> Classes { get; init; } = [];
    public string? ImageUrl { get; init; }
    public bool Tradable { get; init; }
    public bool Marketable { get; init; }
    public bool Craftable { get; init; } = true;
    public int Amount { get; init; } = 1;
    public string? Effect { get; init; }
    public string? Paint { get; init; }
    public string? Killstreaker { get; init; }
    public string? Sheen { get; init; }
    public IReadOnlyList<string> Spells { get; init; } = [];
    public bool Festivized { get; init; }
    public int? Level { get; init; }
    public long Position { get; init; }
    public IReadOnlyList<DescriptionLine> Lines { get; init; } = [];

    [GeneratedRegex(@"Unusual Effect:\s*(.+)$")]
    private static partial Regex EffectLine();

    [GeneratedRegex(@"^Level\s+(\d+)\b")]
    private static partial Regex LevelPrefix();

    public static InventoryItem From(JsonElement asset, JsonElement description, long position)
    {
        var tags = description.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().ToList() : [];
        string? Tag(string category) => tags.FirstOrDefault(x => Str(x, "category") == category) is { ValueKind: JsonValueKind.Object } tag
            ? Str(tag, "localized_tag_name")
            : null;
        string? TagColor(string category) => tags.FirstOrDefault(x => Str(x, "category") == category) is { ValueKind: JsonValueKind.Object } tag
            ? Str(tag, "color")
            : null;

        var lines = new List<DescriptionLine>();
        if (description.TryGetProperty("descriptions", out var desc) && desc.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in desc.EnumerateArray())
            {
                var text = (Str(line, "value") ?? "").Trim();
                if (text.Length > 0)
                    lines.Add(new DescriptionLine(text, Hex(Str(line, "color"))));
            }
        }

        string? After(string prefix) => lines.Select(l => l.Text).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } hit
            ? hit[prefix.Length..].Trim()
            : null;

        var type = Str(description, "type") ?? "";
        var level = LevelPrefix().Match(type) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;

        return new InventoryItem
        {
            AssetId = Str(asset, "assetid") ?? "",
            ClassId = Str(asset, "classid") ?? "",
            InstanceId = Str(asset, "instanceid") ?? "",
            Amount = int.TryParse(Str(asset, "amount"), out var amount) ? amount : 1,
            Name = Str(description, "name") ?? "Unknown item",
            MarketHashName = Str(description, "market_hash_name") ?? Str(description, "name") ?? "",
            Quality = Tag("Quality") ?? "Unique",
            QualityColor = Hex(TagColor("Quality")) ?? "#7D6D00",
            NameColor = Hex(Str(description, "name_color")) ?? Hex(TagColor("Quality")) ?? "#B2B2B2",
            Type = type,
            Category = Tag("Type") ?? "",
            Wear = Tag("Exterior"),
            Collection = Tag("Collection"),
            Classes = tags.Where(x => Str(x, "category") == "Class").Select(x => Str(x, "localized_tag_name") ?? "").Where(s => s.Length > 0).ToArray(),
            ImageUrl = Str(description, "icon_url") is { Length: > 0 } icon ? $"https://community.akamai.steamstatic.com/economy/image/{icon}/256fx256f" : null,
            Tradable = Num(description, "tradable") == 1,
            Marketable = Num(description, "marketable") == 1,
            Craftable = !lines.Any(l => l.Text.Contains("Not Usable in Crafting", StringComparison.OrdinalIgnoreCase)),
            Effect = lines.Select(l => EffectLine().Match(l.Text)).FirstOrDefault(x => x.Success)?.Groups[1].Value.Trim(),
            Paint = After("Paint Color:"),
            Killstreaker = After("Killstreaker:"),
            Sheen = After("Sheen:"),
            Spells = lines.Where(l => l.Text.StartsWith("Halloween:", StringComparison.OrdinalIgnoreCase))
                          .Select(l => l.Text["Halloween:".Length..].Split('(')[0].Trim()).ToArray(),
            Festivized = lines.Any(l => l.Text.Equals("Festivized", StringComparison.OrdinalIgnoreCase)),
            Level = level,
            Position = position,
            Lines = lines,
        };
    }

    /// <summary>
    /// Names the item may have on backpack.tf, most likely first. Steam keeps
    /// the "The" article and the Unusual quality word; backpack.tf drops the
    /// article, names unusuals by effect and prefixes "Non-Craftable".
    /// </summary>
    public IEnumerable<string> PriceNames()
    {
        var names = new List<string> { MarketHashName.Length > 0 ? MarketHashName : Name };
        if (Effect is not null)
        {
            if (names[0].StartsWith("Strange Unusual ", StringComparison.Ordinal))
                names.Add($"Strange {Effect} {names[0]["Strange Unusual ".Length..]}");
            else if (names[0].StartsWith("Unusual ", StringComparison.Ordinal))
                names.Add($"{Effect} {names[0]["Unusual ".Length..]}");
        }
        names.AddRange(names.Where(n => n.StartsWith("The ", StringComparison.Ordinal)).Select(n => n[4..]).ToList());
        names.AddRange(names.Select(n => TheInMiddle().Replace(n, "$1")).Where(n => !names.Contains(n)).ToList());

        if (!Craftable)
        {
            var uncraftable = new List<string>();
            foreach (var name in names)
            {
                var prefix = KillstreakPrefix().Match(name);
                uncraftable.Add(prefix.Groups[1].Value + "Non-Craftable " + prefix.Groups[2].Value);
                uncraftable.Add("Non-Craftable " + name);
            }
            names.InsertRange(0, uncraftable);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^((?:\w+ )*?)The ")]
    private static partial Regex TheInMiddle();

    [GeneratedRegex(@"^((?:Strange )?(?:(?:Professional |Specialized )?Killstreak )?)(.*)$")]
    private static partial Regex KillstreakPrefix();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string? Hex(string? color) =>
        string.IsNullOrWhiteSpace(color) ? null : color.StartsWith('#') ? color : "#" + color;
}
