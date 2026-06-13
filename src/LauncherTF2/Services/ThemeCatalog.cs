using System.Windows.Media;
using LauncherTF2.Models;

namespace LauncherTF2.Services;

/// <summary>
/// Source of truth for built-in launcher themes. Each entry is a complete
/// visual pack — colors, gradient, accent, and logo asset path.
///
/// Adding a new theme:
///   1. Append a new ThemeDefinition to <see cref="GetBuiltinThemes"/>.
///   2. Optionally drop a matching logo at
///      <c>resources/Assets/Logos/logo_&lt;id&gt;.png</c>. If missing, the
///      manager falls back to the default Eternal logo automatically.
///
/// Themes are intentionally registered in code (not XAML) so we can keep
/// strong typing, code-search for color references, and avoid shipping a
/// dictionary file per theme. Future external themes can still be loaded
/// from XAML packs via ThemeManagerService.LoadFromResourceDictionary().
/// </summary>
internal static class ThemeCatalog
{
    public const string DefaultThemeId = "eternal-classic";

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static IReadOnlyList<ThemeDefinition> GetBuiltinThemes() => _themes;

    private static readonly ThemeDefinition[] _themes =
    [
        new ThemeDefinition
        {
            Id = DefaultThemeId,
            DisplayName = "Eternal Classic",
            Description = "The original Eternal palette — dark steel, TF2 orange accent.",
            LogoAssetPath = "/Resources/Assets/logo_classic.png",
            IconAssetPath = "/Resources/Assets/logo64_classic.ico",
            Background = C("#E60C1118"),
            Sidebar = C("#E6091018"),
            Card = C("#171D29"),
            Surface = C("#171D29"),
            SurfaceAlt = C("#1C2332"),
            SurfaceInset = C("#101521"),
            Border = C("#273149"),
            PageTop = C("#111723"),
            PageBottom = C("#0B0F16"),
            Accent = C("#FF6B00"),
            AccentGlow = C("#FF6B00"),
            AccentForeground = C("#FFFFFF"),
            Text = C("#FFFFFF"),
            SecondaryText = C("#A1A1AA"),
            PreviewSwatch = C("#FF6B00"),
        },
        new ThemeDefinition
        {
            Id = "australium",
            DisplayName = "Australium",
            Description = "Polished gold over deep bronze — for the rich and the unusual.",
            LogoAssetPath = "/Resources/Assets/logo_australium.png",
            IconAssetPath = "/Resources/Assets/logo64_australium.ico",
            Background = C("#E6141008"),
            Sidebar = C("#E61A1408"),
            Card = C("#221C0E"),
            Surface = C("#221C0E"),
            SurfaceAlt = C("#2A2210"),
            SurfaceInset = C("#171204"),
            Border = C("#4A3A12"),
            PageTop = C("#1A1408"),
            PageBottom = C("#0E0A02"),
            Accent = C("#E8B23B"),
            AccentGlow = C("#FFD15A"),
            AccentForeground = C("#111111"),
            Text = C("#FFF6D8"),
            SecondaryText = C("#B5A678"),
            PreviewSwatch = C("#E8B23B"),
        },
        new ThemeDefinition
        {
            Id = "red",
            DisplayName = "RED",
            Description = "Reliable Excavation Demolition — warm rust over dark crimson.",
            LogoAssetPath = "/Resources/Assets/logo_RED.png",
            IconAssetPath = "/Resources/Assets/logo64_RED.ico",
            Background = C("#E61A0A0A"),
            Sidebar = C("#E6160808"),
            Card = C("#241010"),
            Surface = C("#241010"),
            SurfaceAlt = C("#2E1414"),
            SurfaceInset = C("#180808"),
            Border = C("#4A1E1E"),
            PageTop = C("#1C0A0A"),
            PageBottom = C("#0E0404"),
            Accent = C("#D9432A"),
            AccentGlow = C("#FF5A3A"),
            AccentForeground = C("#FFFFFF"),
            Text = C("#FFEDE6"),
            SecondaryText = C("#B59285"),
            PreviewSwatch = C("#D9432A"),
        },
        new ThemeDefinition
        {
            Id = "blu",
            DisplayName = "BLU",
            Description = "Builders League United — cool steel over deep navy.",
            LogoAssetPath = "/Resources/Assets/logo_BLU.png",
            IconAssetPath = "/Resources/Assets/logo64_BLU.ico",
            Background = C("#E60A1220"),
            Sidebar = C("#E608101C"),
            Card = C("#101C30"),
            Surface = C("#101C30"),
            SurfaceAlt = C("#142238"),
            SurfaceInset = C("#08111E"),
            Border = C("#1F345A"),
            PageTop = C("#0C162A"),
            PageBottom = C("#050B18"),
            Accent = C("#3D7DD6"),
            AccentGlow = C("#5BA3FF"),
            AccentForeground = C("#FFFFFF"),
            Text = C("#E8F1FF"),
            SecondaryText = C("#8AA0BD"),
            PreviewSwatch = C("#3D7DD6"),
        },
        new ThemeDefinition
        {
            Id = "carbon",
            DisplayName = "Carbon",
            Description = "Pure graphite — minimal contrast, neutral accent.",
            LogoAssetPath = "/Resources/Assets/logo_carbon.png",
            IconAssetPath = "/Resources/Assets/logo64_carbon.ico",
            Background = C("#E60F1014"),
            Sidebar = C("#E60C0D11"),
            Card = C("#16181D"),
            Surface = C("#16181D"),
            SurfaceAlt = C("#1B1E24"),
            SurfaceInset = C("#0D0E12"),
            Border = C("#2A2D36"),
            PageTop = C("#101115"),
            PageBottom = C("#08090C"),
            Accent = C("#9AA0AC"),
            AccentGlow = C("#C4CAD6"),
            AccentForeground = C("#111111"),
            Text = C("#F2F4F8"),
            SecondaryText = C("#7D828C"),
            PreviewSwatch = C("#9AA0AC"),
        },
        new ThemeDefinition
        {
            Id = "midnight",
            DisplayName = "Midnight",
            Description = "Deep indigo with a cyan signal — quiet but alive.",
            LogoAssetPath = "/Resources/Assets/logo_midnight.png",
            IconAssetPath = "/Resources/Assets/logo64_midnight.ico",
            Background = C("#E60A0E1A"),
            Sidebar = C("#E6080B15"),
            Card = C("#121728"),
            Surface = C("#121728"),
            SurfaceAlt = C("#171D33"),
            SurfaceInset = C("#0A0D18"),
            Border = C("#243056"),
            PageTop = C("#0D1120"),
            PageBottom = C("#05070F"),
            Accent = C("#5DD6FF"),
            AccentGlow = C("#85E5FF"),
            AccentForeground = C("#111111"),
            Text = C("#EAF4FF"),
            SecondaryText = C("#8FA0BF"),
            PreviewSwatch = C("#5DD6FF"),
        },
        new ThemeDefinition
        {
            Id = "plasma",
            DisplayName = "Plasma",
            Description = "Magenta-cyan dual accent — high-energy panel glow.",
            LogoAssetPath = "/Resources/Assets/logo_plasma.png",
            IconAssetPath = "/Resources/Assets/logo64_plasma.ico",
            Background = C("#E6100A1E"),
            Sidebar = C("#E60D081A"),
            Card = C("#1B102E"),
            Surface = C("#1B102E"),
            SurfaceAlt = C("#22143A"),
            SurfaceInset = C("#10081C"),
            Border = C("#3A1F60"),
            PageTop = C("#150C24"),
            PageBottom = C("#08040F"),
            Accent = C("#C84BFF"),
            AccentGlow = C("#FF7BD2"),
            AccentForeground = C("#FFFFFF"),
            Text = C("#F5E8FF"),
            SecondaryText = C("#A089BF"),
            PreviewSwatch = C("#C84BFF"),
        },
        new ThemeDefinition
        {
            Id = "toxic",
            DisplayName = "Toxic",
            Description = "Neon green on deep black — radioactive clarity.",
            LogoAssetPath = "/Resources/Assets/logo_toxic.png",
            IconAssetPath = "/Resources/Assets/logo64_toxic.ico",
            Background = C("#E6050C05"),
            Sidebar = C("#E6040904"),
            Card = C("#0C160C"),
            Surface = C("#0C160C"),
            SurfaceAlt = C("#112011"),
            SurfaceInset = C("#071007"),
            Border = C("#1D3D1D"),
            PageTop = C("#0A130A"),
            PageBottom = C("#040804"),
            Accent = C("#39FF14"),
            AccentGlow = C("#7FFF5A"),
            AccentForeground = C("#050C05"),
            Text = C("#EAFFF0"),
            SecondaryText = C("#72A872"),
            PreviewSwatch = C("#39FF14"),
        },
        new ThemeDefinition
        {
            Id = "synthwave",
            DisplayName = "Synthwave",
            Description = "Neon pink-purple grid — '84 in a launcher.",
            LogoAssetPath = "/Resources/Assets/logo_synthwave.png",
            IconAssetPath = "/Resources/Assets/logo64_synthwave.ico",
            Background = C("#E60A0820"),
            Sidebar = C("#E608061A"),
            Card = C("#170E2E"),
            Surface = C("#170E2E"),
            SurfaceAlt = C("#1F143C"),
            SurfaceInset = C("#0C081C"),
            Border = C("#42208A"),
            PageTop = C("#120A28"),
            PageBottom = C("#05030E"),
            Accent = C("#8B5BFF"),
            AccentGlow = C("#FF3DBE"),
            AccentForeground = C("#FFFFFF"),
            Text = C("#F2E6FF"),
            SecondaryText = C("#A488D2"),
            PreviewSwatch = C("#8B5BFF"),
        },
        new ThemeDefinition
        {
            Id = "minimal",
            DisplayName = "Minimal",
            Description = "Soft greys, low contrast — for long sessions.",
            LogoAssetPath = "/Resources/Assets/logo_minimal.png",
            IconAssetPath = "/Resources/Assets/logo64_minimal.ico",
            Background = C("#E613151A"),
            Sidebar = C("#E60F1115"),
            Card = C("#1B1D23"),
            Surface = C("#1B1D23"),
            SurfaceAlt = C("#22242C"),
            SurfaceInset = C("#111317"),
            Border = C("#2F323B"),
            PageTop = C("#14161B"),
            PageBottom = C("#0B0D11"),
            Accent = C("#E0E0E6"),
            AccentGlow = C("#FFFFFF"),
            AccentForeground = C("#111111"),
            Text = C("#F0F0F4"),
            SecondaryText = C("#888892"),
            PreviewSwatch = C("#E0E0E6"),
        },
    ];
}
