using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
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
    private Color _logoColor;
    private string? _screenshot;
    private int _backgroundVersion;
    private List<(SolidColorBrush Brush, Func<Theme, Color> Color)>? _fluentBrushes;

    private static readonly Theme Markers = new("", "",
        Color.FromRgb(1, 2, 3), Color.FromRgb(4, 5, 6), Color.FromRgb(7, 8, 9), Color.FromRgb(10, 11, 12),
        Color.FromRgb(13, 14, 15), Color.FromRgb(16, 17, 18), Color.FromRgb(19, 20, 21), default, default);

    [ObservableProperty] private Theme _current = Theme.All[0];
    [ObservableProperty] private Color _accent;
    [ObservableProperty] private Bitmap? _logo;
    [ObservableProperty] private WindowIcon? _icon;
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

        if (Logo is null || accent != _logoColor)
        {
            var oldLogo = Logo;
            Logo = Hat(accent);
            Icon = Logo is null ? null : new WindowIcon(Logo);
            oldLogo?.Dispose();
            _logoColor = accent;
        }

        var source = settings.Background switch
        {
            BackgroundMode.Picture => settings.BackgroundImage,
            BackgroundMode.Screenshot => settings.BackgroundScreenshot,
            BackgroundMode.Screenshots or BackgroundMode.Rotate => CurrentScreenshot(),
            _ => null,
        };
        _ = UpdateBackgroundAsync(source, settings.BackgroundBlur);
        StartRotation(settings);
    }

    private static readonly TimeSpan BackgroundSettle = TimeSpan.FromMilliseconds(120);
    private DispatcherTimer? _rotate;
    private int _rotateIndex;

    private void StartRotation(LauncherSettings settings)
    {
        var every = TimeSpan.FromMinutes(Math.Clamp(settings.BackgroundRotateMinutes, 1, 120));
        if (settings.Background != BackgroundMode.Rotate || Screenshots.Count < 2)
        {
            _rotate?.Stop();
            _rotate = null;
            return;
        }
        if (_rotate?.Interval == every)
            return;
        _rotate?.Stop();
        _rotate = new DispatcherTimer(every, DispatcherPriority.Background, (_, _) =>
        {
            var pool = Screenshots.Take(40).ToList();
            if (pool.Count < 2)
                return;
            _rotateIndex = (_rotateIndex + 1) % pool.Count;
            _screenshot = pool[_rotateIndex];
            _ = UpdateBackgroundAsync(_screenshot, settings.BackgroundBlur);
        });
        _rotate.Start();
    }

    /// <summary>The top hat in the accent colour, so a custom accent reaches the window and tray icons too.</summary>
    private static Bitmap? Hat(Color color)
    {
        try
        {
            using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EternalLauncher/Assets/Logos/logo_minimal.png"));
            using var mask = SKBitmap.Decode(source);
            using var tinted = new SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var tint = SKColorFilter.CreateBlendMode(new SKColor(color.R, color.G, color.B), SKBlendMode.SrcIn);
            using (var canvas = new SKCanvas(tinted))
            using (var paint = new SKPaint { ColorFilter = tint })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(mask, 0, 0, paint);
            }
            using var png = tinted.Encode(SKEncodedImageFormat.Png, 100);
            return new Bitmap(png.AsStream());
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException)
        {
            Log.Warn($"Couldn't draw the logo: {ex.Message}");
            return null;
        }
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
        _fluentBrushes ??= FindFluentBrushes(fluent);
        FillPalette(fluent, theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark, theme);
        foreach (var (brush, color) in _fluentBrushes)
            brush.Color = color(theme);
    }

    /// <summary>
    /// Fluent copies most palette colours into its brushes once, the first time each is
    /// read, so a later palette never reaches them. Reading them all while the palette
    /// holds marker colours shows which theme colour each brush stands for.
    /// </summary>
    private List<(SolidColorBrush Brush, Func<Theme, Color> Color)> FindFluentBrushes(FluentTheme fluent)
    {
        FillPalette(fluent, ThemeVariant.Dark, Markers);
        FillPalette(fluent, ThemeVariant.Light, Markers);
        var roles = new Dictionary<Color, Func<Theme, Color>>
        {
            [Markers.Ground] = t => t.Ground,
            [Markers.Surface] = t => t.Surface,
            [Markers.Raised] = t => t.Raised,
            [Markers.Line] = t => t.Line,
            [Markers.Ink] = t => t.Ink,
            [Markers.Muted] = t => t.Muted,
            [Markers.Faint] = t => t.Faint,
            [Mix(Markers.Ink, Markers.Muted, 0.5)] = t => Mix(t.Ink, t.Muted, 0.5),
        };
        var keys = new HashSet<object>();
        foreach (var style in _app.Styles)
            CollectKeys(style, keys);
        var brushes = new List<(SolidColorBrush, Func<Theme, Color>)>();
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var key in keys)
                if (_app.TryFindResource(key, variant, out var value) && value is SolidColorBrush brush && roles.TryGetValue(brush.Color, out var role))
                    brushes.Add((brush, role));
        return brushes;
    }

    private static void CollectKeys(IStyle style, HashSet<object> keys)
    {
        if (style is StyleBase { Resources: IResourceDictionary own })
            CollectKeys(own, keys);
        if (style is Styles group)
        {
            CollectKeys(group.Resources, keys);
            foreach (var child in group)
                CollectKeys(child, keys);
        }
        if (style is StyleInclude { Loaded: { } loaded })
            CollectKeys(loaded, keys);
    }

    private static void CollectKeys(IResourceDictionary dictionary, HashSet<object> keys)
    {
        foreach (var key in dictionary.Keys)
            keys.Add(key);
        foreach (var merged in dictionary.MergedDictionaries.OfType<IResourceDictionary>())
            CollectKeys(merged, keys);
        foreach (var themed in dictionary.ThemeDictionaries.Values.OfType<IResourceDictionary>())
            CollectKeys(themed, keys);
    }

    private static void FillPalette(FluentTheme fluent, ThemeVariant variant, Theme theme)
    {
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
        var version = ++_backgroundVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowBackground(null);
            return;
        }
        try
        {
            // The blur slider asks for a new picture on every tick; only the last one is worth making.
            await Task.Delay(BackgroundSettle).ConfigureAwait(true);
            if (version != _backgroundVersion)
                return;
            var bitmap = await Task.Run(() => RenderBackground(path, (float)blur)).ConfigureAwait(true);
            if (version == _backgroundVersion)
                ShowBackground(bitmap);
            else
                bitmap?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Background image {path} couldn't be loaded: {ex.Message}");
            ShowBackground(null);
        }
    }

    private void ShowBackground(Bitmap? next)
    {
        var old = Background;
        Background = next;
        old?.Dispose();
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
