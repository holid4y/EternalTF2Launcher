using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CommunityToolkit.Mvvm.ComponentModel;
using Eternal.Core;
using Eternal.Core.Logging;
using SkiaSharp;

namespace Eternal.App.Theming;

/// <summary>
/// Applies the chosen palette, accent, density, scale and background. Every
/// colour in the UI resolves through these resources, so switching is live.
/// </summary>
public sealed partial class ThemeService : ObservableObject
{
    private readonly Application _app;
    private string? _backgroundSource;
    private double _backgroundBlur = -1;
    private IReadOnlyList<string>? _screenshots;
    private string? _screenshot;

    [ObservableProperty] private Theme _current = Theme.All[0];
    [ObservableProperty] private Color _accent;
    [ObservableProperty] private IImage? _logo;
    [ObservableProperty] private Bitmap? _background;
    [ObservableProperty] private double _backgroundDim;
    [ObservableProperty] private double _scale = 1;

    public ThemeService(Application app) => _app = app;

    public void Apply(LauncherSettings settings)
    {
        var theme = Theme.Find(settings.ThemeId);
        var accent = settings.AccentOverride is { } hex && Color.TryParse(hex, out var custom) ? custom : theme.Accent;
        Current = theme;
        Accent = accent;
        Scale = Math.Clamp(settings.UiScale, 0.8, 1.4);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 0.95);

        _app.RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var r = _app.Resources;
        void Brush(string key, Color color) => r[key] = new SolidColorBrush(color);

        Brush("GroundBrush", theme.Ground);
        Brush("SurfaceBrush", theme.Surface);
        Brush("RaisedBrush", theme.Raised);
        Brush("LineBrush", theme.Line);
        Brush("InkBrush", theme.Ink);
        Brush("MutedBrush", theme.Muted);
        Brush("FaintBrush", theme.Faint);
        Brush("AccentBrush", accent);
        Brush("AccentInkBrush", ReadableOn(accent));
        Brush("AccentSoftBrush", WithAlpha(accent, 0x2E));
        Brush("PositiveBrush", Theme.Positive);
        Brush("WarningBrush", Theme.Warning);
        Brush("DangerBrush", Theme.Danger);
        Brush("WarningSoftBrush", WithAlpha(Theme.Warning, 0x26));
        Brush("DangerSoftBrush", WithAlpha(Theme.Danger, 0x26));
        Brush("ScrimBrush", WithAlpha(theme.Ground, (byte)(BackgroundDim * 255)));
        r["ControlCornerRadius"] = new CornerRadius(3);
        r["OverlayCornerRadius"] = new CornerRadius(4);

        var compact = settings.Density == UiDensity.Compact;
        r["RowPadding"] = compact ? new Thickness(14, 8) : new Thickness(18, 13);
        r["CardPadding"] = compact ? new Thickness(16) : new Thickness(22);
        r["SectionGap"] = compact ? 18.0 : 28.0;
        r["ItemGap"] = compact ? 6.0 : 10.0;

        ApplyFluentPalette(theme);

        try
        {
            Logo = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://EternalLauncher/Assets/Logos/{theme.Logo}.png")));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            Logo = null;
        }

        var source = settings.Background switch
        {
            BackgroundMode.Picture => settings.BackgroundImage,
            BackgroundMode.Screenshots => CurrentScreenshot(),
            _ => null,
        };
        _ = UpdateBackgroundAsync(source, settings.BackgroundBlur);
    }

    /// <summary>TF2 screenshots found in Steam's userdata, listed once per session.</summary>
    public IReadOnlyList<string> Screenshots => _screenshots ??= Eternal.Core.Steam.SteamLocator.Screenshots();

    private string? CurrentScreenshot()
    {
        if (_screenshot is null && Screenshots.Count > 0)
            _screenshot = Screenshots[Random.Shared.Next(Math.Min(Screenshots.Count, 40))];
        return _screenshot;
    }

    /// <summary>Shows a different screenshot (picked among the 40 most recent).</summary>
    public void NextScreenshot(LauncherSettings settings)
    {
        var pool = Screenshots.Take(40).Where(s => s != _screenshot).ToList();
        if (pool.Count == 0)
            return;
        _screenshot = pool[Random.Shared.Next(pool.Count)];
        Apply(settings);
    }

    private void ApplyFluentPalette(Theme theme)
    {
        var fluent = _app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null)
            return;
        var variant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (!fluent.Palettes.TryGetValue(variant, out var palette))
            fluent.Palettes[variant] = palette = new ColorPaletteResources();

        // Fluent paints toggles, sliders and selection with its accent. That
        // stays the ink colour: the theme accent is kept for Play and data.
        palette.Accent = theme.Ink;
        palette.RegionColor = theme.Ground;
        palette.AltHigh = theme.Ground;
        palette.AltMediumHigh = theme.Surface;
        palette.AltMedium = theme.Surface;
        palette.AltMediumLow = theme.Raised;
        palette.AltLow = theme.Raised;
        palette.BaseHigh = theme.Ink;
        palette.BaseMediumHigh = Mix(theme.Ink, theme.Muted, 0.5);
        palette.BaseMedium = theme.Muted;
        palette.BaseMediumLow = theme.Faint;
        palette.BaseLow = theme.Line;
        palette.ChromeAltLow = theme.Ink;
        palette.ChromeBlackHigh = theme.Ground;
        palette.ChromeBlackLow = theme.Raised;
        palette.ChromeBlackMedium = theme.Surface;
        palette.ChromeBlackMediumLow = theme.Surface;
        palette.ChromeDisabledHigh = theme.Line;
        palette.ChromeDisabledLow = theme.Faint;
        palette.ChromeGray = theme.Muted;
        palette.ChromeHigh = theme.Line;
        palette.ChromeLow = theme.Surface;
        palette.ChromeMedium = theme.Raised;
        palette.ChromeMediumLow = theme.Surface;
        // Toggle knobs sit on the ink-coloured track when on; they need the ground colour to show.
        palette.ChromeWhite = theme.Ground;
        palette.ListLow = theme.Raised;
        palette.ListMedium = theme.Line;
        palette.ErrorText = Theme.Danger;
    }

    /// <summary>Blurs the picture once, off the UI thread, so the window never pays for it per frame.</summary>
    private async Task UpdateBackgroundAsync(string? path, double blur)
    {
        if (path == _backgroundSource && Math.Abs(blur - _backgroundBlur) < 0.1)
            return;
        _backgroundSource = path;
        _backgroundBlur = blur;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Background = null;
            return;
        }
        try
        {
            var bitmap = await Task.Run(() => RenderBackground(path, (float)blur)).ConfigureAwait(true);
            if (_backgroundSource == path)
                Background = bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Background image {path} couldn't be loaded: {ex.Message}");
            Background = null;
        }
    }

    private static Bitmap? RenderBackground(string path, float blur)
    {
        using var original = SKBitmap.Decode(path);
        if (original is null)
            return null;
        var scale = Math.Min(1.0, 1920.0 / original.Width);
        var info = new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)));
        using var surface = SKSurface.Create(info);
        using var paint = new SKPaint
        {
            ImageFilter = blur > 0 ? SKImageFilter.CreateBlur(blur, blur, SKShaderTileMode.Clamp) : null,
            FilterQuality = SKFilterQuality.Medium,
        };
        surface.Canvas.DrawBitmap(original, new SKRect(0, 0, info.Width, info.Height), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }

    public static Color ReadableOn(Color background)
    {
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255;
        return luminance > 0.55 ? Color.Parse("#16110D") : Color.Parse("#FFF8EE");
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
