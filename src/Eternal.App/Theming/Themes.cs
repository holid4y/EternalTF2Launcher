using Avalonia.Media;

namespace Eternal.App.Theming;

/// <summary>
/// A palette: one ground, two lifted surfaces, a text ramp and one accent.
/// The accent goes on Play and on data, never on navigation chrome.
/// </summary>
public sealed record Theme(
    string Id,
    string Name,
    Color Ground,
    Color Surface,
    Color Raised,
    Color Line,
    Color Ink,
    Color Muted,
    Color Faint,
    Color Accent,
    Color AccentInk)
{
    public static readonly Color Positive = Color.Parse("#8DB255");
    public static readonly Color Warning = Color.Parse("#E7B53B");
    public static readonly Color Danger = Color.Parse("#D2553F");

    public static IReadOnlyList<Theme> All { get; } =
    [
        new("mann-co", "Mann Co.",
            C("#15110E"), C("#201A16"), C("#2B231E"), C("#3A2F28"),
            C("#EEE4CC"), C("#B9AA90"), C("#8E8170"), C("#CF6A32"), C("#1B110A")),
        new("red", "RED",
            C("#160F0E"), C("#221816"), C("#2E201D"), C("#3D2B27"),
            C("#F0E3D4"), C("#BCA899"), C("#918072"), C("#C24A3E"), C("#FFF4EC")),
        new("blu", "BLU",
            C("#0F1215"), C("#171C21"), C("#20262D"), C("#2D353E"),
            C("#E3E8EC"), C("#A4AEB8"), C("#7D8893"), C("#5B8DB8"), C("#0C141B")),
        new("australium", "Australium",
            C("#14110B"), C("#1F1A11"), C("#2A2317"), C("#3A3120"),
            C("#F1E7CF"), C("#BBAA88"), C("#90826A"), C("#E2B13C"), C("#1A1404")),
        new("carbon", "Carbon",
            C("#111111"), C("#1A1A1A"), C("#242424"), C("#323232"),
            C("#E8E8E8"), C("#ABABAB"), C("#858585"), C("#D8D8D8"), C("#111111")),
        new("toxic", "Toxic",
            C("#0F120E"), C("#171B15"), C("#20251D"), C("#2E3529"),
            C("#E6EDDC"), C("#A9B39B"), C("#838D76"), C("#8FC044"), C("#0E1407")),
        new("unusual", "Unusual",
            C("#120F16"), C("#1B1621"), C("#251E2D"), C("#352B40"),
            C("#ECE4F2"), C("#B3A8BF"), C("#8B8098"), C("#9A62C6"), C("#FBF4FF")),
        new("minimal", "Minimal",
            C("#F2EFEA"), C("#E8E4DD"), C("#DDD8CF"), C("#CCC6BB"),
            C("#1D1A16"), C("#5B544B"), C("#7A7268"), C("#1D1A16"), C("#F2EFEA")),
    ];

    public bool IsLight => Ground.R + Ground.G + Ground.B > 3 * 128;

    public static Theme Find(string? id)
    {
        // Retired themes land on the closest one still offered.
        var mapped = id switch
        {
            "eternal-classic" => "mann-co",
            "midnight" or "plasma" or "synthwave" => "unusual",
            _ => id,
        };
        return All.FirstOrDefault(t => t.Id == mapped) ?? All[0];
    }

    private static Color C(string hex) => Color.Parse(hex);
}
