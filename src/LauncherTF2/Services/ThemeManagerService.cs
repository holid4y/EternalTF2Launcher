using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using LauncherTF2.Core;
using LauncherTF2.Models;

namespace LauncherTF2.Services;

/// <summary>
/// Owns the live launcher palette + logo. Themes are swapped at runtime by
/// animating the Color property of every named SolidColorBrush / GradientStop
/// resource declared in <c>App.xaml</c>. Because XAML consumers hold the same
/// brush instance regardless of whether they used StaticResource or
/// DynamicResource, this propagates to every screen without forcing a reload.
///
/// The service also exposes:
///   - <see cref="AvailableThemes"/> for the personalization UI
///   - <see cref="CurrentTheme"/> + <see cref="CurrentLogoSource"/> for bindings
///   - persistence through <see cref="SettingsService"/> /
///     <c>launcher_config.json</c>
/// </summary>
public class ThemeManagerService : INotifyPropertyChanged
{
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(380));
    private static readonly IEasingFunction TransitionEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>
    /// Every named SolidColorBrush resource we animate. Listed explicitly so
    /// <see cref="EnsureMutableBrushes"/> can replace any frozen instances
    /// before MainWindow's StaticResource references resolve.
    /// </summary>
    private static readonly string[] AnimatedBrushKeys =
    [
        "BackgroundBrush", "SidebarBrush", "CardBrush", "SurfaceBrush",
        "SurfaceAltBrush", "SurfaceInsetBrush", "BorderBrush",
        "TextBrush", "SecondaryTextBrush", "AccentBrush", "AccentForegroundBrush",
    ];

    private readonly SettingsService _settings;
    private readonly List<ThemeDefinition> _themes;
    private readonly Dictionary<string, ThemeDefinition> _themesById;

    private ThemeDefinition _currentTheme;
    private ImageSource? _currentLogoSource;
    private ImageSource? _currentIconSource;
    private bool _suppressPersist;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised after a theme is applied — useful for one-shot UI side effects.</summary>
    public event EventHandler<ThemeDefinition>? ThemeApplied;

    public ThemeManagerService(SettingsService settings)
    {
        _settings = settings;
        _themes = [.. ThemeCatalog.GetBuiltinThemes()];
        _themesById = _themes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        _currentTheme = _themesById[ThemeCatalog.DefaultThemeId];
    }

    public IReadOnlyList<ThemeDefinition> AvailableThemes => _themes;

    public ThemeDefinition CurrentTheme
    {
        get => _currentTheme;
        private set
        {
            if (_currentTheme.Id == value.Id) return;
            _currentTheme = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? CurrentLogoSource
    {
        get => _currentLogoSource;
        private set
        {
            _currentLogoSource = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? CurrentIconSource
    {
        get => _currentIconSource;
        private set
        {
            _currentIconSource = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Restores theme state from launcher_config.json and applies it without
    /// animation (used at startup, before MainWindow is shown).
    /// </summary>
    public void LoadFromConfig()
    {
        var cfg = _settings.GetLauncherConfig();

        var initial = !string.IsNullOrWhiteSpace(cfg.SelectedThemeId)
                      && _themesById.TryGetValue(cfg.SelectedThemeId!, out var saved)
            ? saved
            : _themesById[ThemeCatalog.DefaultThemeId];

        // Replace any brushes the resource dictionary has already frozen so that
        // BeginAnimation calls later (during animated theme switches) don't throw.
        // This must run before MainWindow resolves its StaticResource references.
        EnsureMutableBrushes();

        _suppressPersist = true;
        try
        {
            ApplyTheme(initial.Id, animate: false);
        }
        finally
        {
            _suppressPersist = false;
        }

        Logger.LogInfo($"[Theme] Loaded — theme='{_currentTheme.Id}'");
    }

    /// <summary>
    /// Replaces every brush that has been frozen by the resource dictionary
    /// with a fresh, mutable copy. Covers both <see cref="SolidColorBrush"/>
    /// entries (<see cref="AnimatedBrushKeys"/>) and the gradient brush used
    /// for the page background. Must be called before any window resolves
    /// StaticResource references against these keys.
    /// </summary>
    private static void EnsureMutableBrushes()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        // Solid-color brushes.
        foreach (var key in AnimatedBrushKeys)
        {
            if (resources[key] is SolidColorBrush scb && scb.IsFrozen)
                resources[key] = new SolidColorBrush(scb.Color);
        }

        // Gradient brush — the brush itself and its GradientStops can all be frozen.
        if (resources["PageBackgroundBrush"] is LinearGradientBrush lgb &&
            (lgb.IsFrozen || lgb.GradientStops.Any(s => s.IsFrozen)))
        {
            var fresh = new LinearGradientBrush
            {
                StartPoint = lgb.StartPoint,
                EndPoint   = lgb.EndPoint,
            };
            foreach (var stop in lgb.GradientStops)
                fresh.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
            resources["PageBackgroundBrush"] = fresh;
        }
    }

    /// <summary>
    /// Switches to the theme with the given ID. Unknown IDs are ignored.
    /// When <paramref name="animate"/> is true (default), all brush colors
    /// crossfade over ~0.4s; otherwise the swap is instant.
    /// </summary>
    public void ApplyTheme(string themeId, bool animate = true)
    {
        if (!_themesById.TryGetValue(themeId, out var theme))
        {
            Logger.LogWarning($"[Theme] Unknown theme id '{themeId}', staying on '{_currentTheme.Id}'");
            return;
        }

        var resources = Application.Current?.Resources;
        if (resources == null) return;

        var dur = animate ? TransitionDuration : new Duration(TimeSpan.Zero);

        ApplyBrush(resources, "BackgroundBrush", theme.Background, dur);
        ApplyBrush(resources, "SidebarBrush", theme.Sidebar, dur);
        ApplyBrush(resources, "CardBrush", theme.Card, dur);
        ApplyBrush(resources, "SurfaceBrush", theme.Surface, dur);
        ApplyBrush(resources, "SurfaceAltBrush", theme.SurfaceAlt, dur);
        ApplyBrush(resources, "SurfaceInsetBrush", theme.SurfaceInset, dur);
        ApplyBrush(resources, "BorderBrush", theme.Border, dur);
        ApplyBrush(resources, "TextBrush", theme.Text, dur);
        ApplyBrush(resources, "SecondaryTextBrush", theme.SecondaryText, dur);
        ApplyBrush(resources, "AccentBrush", theme.Accent, dur);
        ApplyBrush(resources, "AccentForegroundBrush", theme.AccentForeground, dur);

        ApplyGradient(resources, "PageBackgroundBrush", theme.PageTop, theme.PageBottom, dur);

        // The DropShadowEffect on the sidebar logo reads AccentColor via DynamicResource,
        // so keep the loose Color resource in sync with the active brush.
        resources["AccentColor"] = theme.Accent;
        resources["AccentGlowColor"] = theme.AccentGlow;

        CurrentTheme = theme;
        CurrentLogoSource = ResolveLogo(theme);
        CurrentIconSource = ResolveIcon(theme);

        if (!_suppressPersist) PersistConfig();
        ThemeApplied?.Invoke(this, theme);
        Logger.LogInfo($"[Theme] Applied '{theme.Id}' (animated={animate})");
    }

    private static void ApplyBrush(ResourceDictionary resources, string key, Color target, Duration dur)
    {
        if (resources[key] is not SolidColorBrush brush)
        {
            resources[key] = new SolidColorBrush(target);
            return;
        }

        // If the brush was frozen by the resource dictionary (e.g. during
        // pack-URI image loading or BAML deserialization), we cannot animate
        // it in-place — swap in a fresh mutable instance instead.
        if (brush.IsFrozen)
        {
            resources[key] = new SolidColorBrush(target);
            return;
        }

        if (dur.HasTimeSpan && dur.TimeSpan == TimeSpan.Zero)
        {
            // Cancel any in-flight animation before clobbering the value.
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }

        var anim = new ColorAnimation
        {
            To = target,
            Duration = dur,
            EasingFunction = TransitionEase,
            FillBehavior = FillBehavior.HoldEnd,
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static void ApplyGradient(ResourceDictionary resources, string key, Color top, Color bottom, Duration dur)
    {
        if (resources[key] is not LinearGradientBrush brush || brush.GradientStops.Count < 2)
        {
            // Resource missing or wrong type — install a fresh brush.
            resources[key] = MakeGradientBrush(top, bottom);
            return;
        }

        // If the brush or any stop is still frozen (e.g. EnsureMutableBrushes ran before
        // the resource was realized), replace the whole brush rather than crashing.
        if (brush.IsFrozen || brush.GradientStops.Any(s => s.IsFrozen))
        {
            resources[key] = MakeGradientBrush(top, bottom);
            return;
        }

        var first = brush.GradientStops[0];
        var last  = brush.GradientStops[^1];

        if (dur.HasTimeSpan && dur.TimeSpan == TimeSpan.Zero)
        {
            first.BeginAnimation(GradientStop.ColorProperty, null);
            last.BeginAnimation(GradientStop.ColorProperty, null);
            first.Color = top;
            last.Color  = bottom;
            return;
        }

        first.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation
        {
            To = top, Duration = dur, EasingFunction = TransitionEase, FillBehavior = FillBehavior.HoldEnd,
        });
        last.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation
        {
            To = bottom, Duration = dur, EasingFunction = TransitionEase, FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private static LinearGradientBrush MakeGradientBrush(Color top, Color bottom) =>
        new()
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint   = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(top,    0.0),
                new GradientStop(bottom, 1.0),
            },
        };

    /// <summary>
    /// Resolves the logo to an ImageSource. If the theme-specific asset is
    /// missing (404 inside the pack URI), we fall back to the default Eternal
    /// logo so the launcher never renders a broken image placeholder.
    /// </summary>
    private static ImageSource? ResolveLogo(ThemeDefinition theme)
    {
        var primary = TryLoadPack(theme.LogoAssetPath);
        if (primary != null) return primary;

        // Implicit fallback chain: themed asset → default logo.
        if (!string.Equals(theme.LogoAssetPath, "/Resources/Assets/logo_classic.png", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug($"[Theme] Logo '{theme.LogoAssetPath}' missing — using default Eternal logo");
            return TryLoadPack("/Resources/Assets/logo_classic.png");
        }
        return null;
    }

    private static ImageSource? ResolveIcon(ThemeDefinition theme)
    {
        var primary = TryLoadPack(theme.IconAssetPath);
        if (primary != null) return primary;

        if (!string.Equals(theme.IconAssetPath, "/Resources/Assets/logo64_classic.ico", StringComparison.OrdinalIgnoreCase))
        {
            return TryLoadPack("/Resources/Assets/logo64_classic.ico");
        }
        return null;
    }

    private static ImageSource? TryLoadPack(string relativePath)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,{relativePath}", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void PersistConfig()
    {
        var cfg = _settings.GetLauncherConfig();
        cfg.SelectedThemeId = _currentTheme.Id;
        _settings.SaveLauncherConfig(cfg);
    }

    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
