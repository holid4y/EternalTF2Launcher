using System.Windows.Media;

namespace LauncherTF2.Models;

/// <summary>
/// One complete launcher visual pack. Holds every color slot the runtime can
/// swap plus a logo asset URI. Themes are immutable once registered — the
/// ThemeManager animates between them by reading these values and overwriting
/// the corresponding live brush colors in Application.Current.Resources.
///
/// All Color values include the alpha channel from the original App.xaml so
/// acrylic-style transparency is preserved per-theme.
/// </summary>
public class ThemeDefinition
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Pack URI for the logo image (e.g. "/Resources/Assets/Logos/logo_red.png").
    /// Resolution is centralized in ThemeManagerService: if the asset is missing
    /// the manager falls back to the default Eternal logo.
    /// </summary>
    public string LogoAssetPath { get; init; } = "/Resources/Assets/logo_classic.png";
    public string IconAssetPath { get; init; } = "/Resources/Assets/logo64_classic.ico";

    // — Surface palette —
    public Color Background { get; init; }
    public Color Sidebar { get; init; }
    public Color Card { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceAlt { get; init; }
    public Color SurfaceInset { get; init; }
    public Color Border { get; init; }

    // — Page gradient (top → bottom) —
    public Color PageTop { get; init; }
    public Color PageBottom { get; init; }

    // — Accents & glow —
    public Color Accent { get; init; }
    public Color AccentGlow { get; init; }
    public Color AccentForeground { get; init; }

    // — Typography —
    public Color Text { get; init; }
    public Color SecondaryText { get; init; }

    /// <summary>
    /// Short preview swatch shown on the theme card before apply. Defaults to
    /// the accent color when not specified by the catalog.
    /// </summary>
    public Color PreviewSwatch { get; init; }
}
