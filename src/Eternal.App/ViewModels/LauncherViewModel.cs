using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.App.Theming;
using Eternal.Core;
using Eternal.Core.Inventory;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed partial class TabPreferenceViewModel(string id, string title, bool visible, bool locked, Action changed) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool Locked { get; } = locked;
    public bool CanHide => !Locked;

    [ObservableProperty] private bool _visible = visible;

    partial void OnVisibleChanged(bool value) => changed();
}

public sealed record ThemeChoice(Theme Theme)
{
    public string Name => Theme.Name;
    public IBrush Ground { get; } = new SolidColorBrush(Theme.Ground);
    public IBrush Surface { get; } = new SolidColorBrush(Theme.Raised);
    public IBrush Accent { get; } = new SolidColorBrush(Theme.Accent);
    public IBrush Ink { get; } = new SolidColorBrush(Theme.Ink);
}

public sealed partial class LauncherViewModel : PageViewModel
{
    private static readonly Dictionary<string, string> TabTitles = new()
    {
        ["home"] = "Home", ["mods"] = "Mods", ["backpack"] = "Backpack", ["config"] = "Config", ["launcher"] = "Launcher",
    };

    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly ThemeService _theme;
    private bool _loading = true;

    public LauncherViewModel(AppServices services, MainViewModel main, ThemeService theme)
    {
        _services = services;
        _main = main;
        _theme = theme;
        var s = services.Launcher.Settings;

        Themes = Theme.All.Select(t => new ThemeChoice(t)).ToArray();
        _selectedTheme = Themes.FirstOrDefault(t => t.Theme.Id == Theme.Find(s.ThemeId).Id) ?? Themes[0];
        _accent = s.AccentOverride is { } hex && Color.TryParse(hex, out var c) ? c : _selectedTheme.Theme.Accent;
        _hasCustomAccent = s.AccentOverride is not null;
        _backgroundImage = s.BackgroundImage;
        _backgroundMode = s.Background;
        _backgroundDim = s.BackgroundDim;
        _backgroundBlur = s.BackgroundBlur;
        _uiScale = s.UiScale;
        _compact = s.Density == UiDensity.Compact;
        _minimizeOnLaunch = s.MinimizeOnLaunch;
        _closeToTray = s.CloseToTray;
        _checkForUpdates = s.CheckForUpdates;
        _currency = s.Currency;
        _logLevel = s.LogLevel;
        _clearLogsOnStart = s.ClearLogsOnStart;
        _startTab = s.StartTab;
        foreach (var tab in s.NormalizedTabs())
            Tabs.Add(new TabPreferenceViewModel(tab.Id, TabTitles[tab.Id], tab.Visible, tab.Id == "launcher", SaveTabs));
        services.Runtime.StatusChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(EngineSummary)));
        _loading = false;
    }

    public IReadOnlyList<ThemeChoice> Themes { get; }
    public ObservableCollection<TabPreferenceViewModel> Tabs { get; } = [];
    public string[] Currencies { get; } = [.. PriceService.SteamCurrencies.Keys];
    public string[] LogLevels { get; } = ["Debug", "Info", "Warning", "Error"];
    public string[] StartTabs { get; } = ["home", "mods", "backpack", "config"];
    public BackgroundMode[] BackgroundModes { get; } = [BackgroundMode.Screenshots, BackgroundMode.Picture, BackgroundMode.None];

    public bool UsesScreenshots => BackgroundMode == BackgroundMode.Screenshots;
    public bool CanCycleScreenshots => UsesScreenshots && _theme.Screenshots.Count > 1;
    public bool UsesPicture => BackgroundMode == BackgroundMode.Picture;
    public bool HasBackground => BackgroundMode != BackgroundMode.None;
    public string ScreenshotNote => _theme.Screenshots.Count switch
    {
        0 => "No TF2 screenshots found yet. Press F12 in game to take some; they show up here next time.",
        1 => "1 TF2 screenshot from your Steam library.",
        var n => $"{n} TF2 screenshots from your Steam library. A different one shows each time the launcher opens.",
    };

    [ObservableProperty] private ThemeChoice _selectedTheme;
    [ObservableProperty] private Color _accent;
    [ObservableProperty] private bool _hasCustomAccent;
    [ObservableProperty] private string? _backgroundImage;
    [ObservableProperty] private BackgroundMode _backgroundMode;
    [ObservableProperty] private double _backgroundDim;
    [ObservableProperty] private double _backgroundBlur;
    [ObservableProperty] private double _uiScale;
    [ObservableProperty] private bool _compact;
    [ObservableProperty] private bool _minimizeOnLaunch;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _currency;
    [ObservableProperty] private string _logLevel;
    [ObservableProperty] private bool _clearLogsOnStart;
    [ObservableProperty] private string _startTab;

    public string TfPath => _services.TfPath ?? "Not set";
    public bool TfValid => SteamLocator.IsTf2Folder(_services.TfPath);
    public string TfStatus => TfValid ? "TF2 found." : "This doesn't look like TF2's tf folder (gameinfo.txt is missing).";
    public string Account => SteamLocator.ActiveAccount() is { } a ? $"{a.PersonaName ?? "Steam account"} ({a.SteamId64})" : "No Steam account found";
    public string ScaleText => $"{UiScale * 100:0}%";
    public bool TraySupported => OperatingSystem.IsWindows();
    public string TrayNote => OperatingSystem.IsWindows() ? "" : "Needs a system tray (KDE, Cinnamon, or GNOME with the AppIndicator extension). Starting the launcher again always shows the window.";
    public string Version => _main.Version;

    public string EngineSummary
    {
        get
        {
            var s = _services.Runtime.Status;
            var parts = new List<string> { s.Message };
            if (s.UpstreamVersion is not null)
                parts.Add($"casual-pre-loader {s.UpstreamVersion}" + (s.UpstreamCommit is { Length: >= 7 } c ? $" ({c[..7]})" : ""));
            if (s.PythonVersion is not null && _services.Runtime.FrozenExecutable is null)
                parts.Add($"Python {s.PythonVersion}");
            if (s.WineAvailable is { } wine)
                parts.Add(wine ? "Wine found" : "Wine not installed (QuickPrecache off)");
            return string.Join(" · ", parts);
        }
    }

    private void Save(Action<LauncherSettings> change, bool restyle = false)
    {
        if (_loading)
            return;
        _services.Launcher.Update(change);
        if (restyle)
            _theme.Apply(_services.Launcher.Settings);
    }

    partial void OnSelectedThemeChanged(ThemeChoice value)
    {
        if (!HasCustomAccent)
            Accent = value.Theme.Accent;
        Save(s => s.ThemeId = value.Theme.Id, restyle: true);
    }

    partial void OnAccentChanged(Color value)
    {
        if (_loading || value == SelectedTheme.Theme.Accent && !HasCustomAccent)
            return;
        HasCustomAccent = value != SelectedTheme.Theme.Accent;
        Save(s => s.AccentOverride = HasCustomAccent ? value.ToString() : null, restyle: true);
    }

    [RelayCommand]
    private void ResetAccent()
    {
        HasCustomAccent = false;
        Accent = SelectedTheme.Theme.Accent;
        Save(s => s.AccentOverride = null, restyle: true);
    }

    partial void OnBackgroundModeChanged(BackgroundMode value)
    {
        OnPropertyChanged(nameof(UsesScreenshots));
        OnPropertyChanged(nameof(CanCycleScreenshots));
        OnPropertyChanged(nameof(UsesPicture));
        OnPropertyChanged(nameof(HasBackground));
        Save(s => s.Background = value, restyle: true);
    }

    [RelayCommand]
    private void NextScreenshot() => _theme.NextScreenshot(_services.Launcher.Settings);

    partial void OnBackgroundDimChanged(double value) => Save(s => s.BackgroundDim = value, restyle: true);
    partial void OnBackgroundBlurChanged(double value) => Save(s => s.BackgroundBlur = value, restyle: true);
    partial void OnUiScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ScaleText));
        Save(s => s.UiScale = value, restyle: true);
    }
    partial void OnCompactChanged(bool value) => Save(s => s.Density = value ? UiDensity.Compact : UiDensity.Comfortable, restyle: true);
    partial void OnMinimizeOnLaunchChanged(bool value) => Save(s => s.MinimizeOnLaunch = value);
    partial void OnCloseToTrayChanged(bool value) => Save(s => s.CloseToTray = value);
    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s.CheckForUpdates = value);
    partial void OnCurrencyChanged(string value) => Save(s => s.Currency = value);
    partial void OnClearLogsOnStartChanged(bool value) => Save(s => s.ClearLogsOnStart = value);
    partial void OnStartTabChanged(string value) => Save(s => s.StartTab = value);

    partial void OnLogLevelChanged(string value)
    {
        if (Enum.TryParse<LogLevel>(value, out var level))
            Log.MinimumLevel = level;
        Save(s => s.LogLevel = value);
    }

    [RelayCommand]
    private async Task ChooseBackgroundAsync()
    {
        var files = await Pickers.OpenFilesAsync("Background picture", false, "Images", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp");
        if (files.Count == 0)
            return;
        BackgroundImage = files[0];
        Save(s => s.BackgroundImage = files[0], restyle: true);
        BackgroundMode = BackgroundMode.Picture;
    }

    [RelayCommand]
    private async Task BrowseTf2Async()
    {
        var folder = await Pickers.OpenFolderAsync("Select TF2's tf folder", _services.TfPath);
        if (folder is null)
            return;
        if (!SteamLocator.IsTf2Folder(folder) && SteamLocator.IsTf2Folder(Path.Combine(folder, "tf")))
            folder = Path.Combine(folder, "tf");
        await SetTf2Async(folder);
    }

    [RelayCommand]
    private async Task DetectTf2Async()
    {
        if (SteamLocator.FindTf2() is { } found)
            await SetTf2Async(found);
        else
            await _main.AlertAsync("TF2 not found", "Steam's libraries don't contain Team Fortress 2. Pick the tf folder by hand.");
    }

    private async Task SetTf2Async(string folder)
    {
        _services.Launcher.Update(s => s.TfPath = folder);
        _services.RefreshObserved();
        OnPropertyChanged(nameof(TfPath));
        OnPropertyChanged(nameof(TfValid));
        OnPropertyChanged(nameof(TfStatus));
        await _services.Game.ReapplyAsync();
        _services.Preload.RequestBuild();
    }

    private void SaveTabs()
    {
        Save(s => s.Tabs = Tabs.Select(t => new TabPreference { Id = t.Id, Visible = t.Visible || t.Locked }).ToList());
    }

    [RelayCommand]
    private void MoveTabUp(TabPreferenceViewModel? tab)
    {
        if (tab is null || Tabs.IndexOf(tab) is var i && i <= 0)
            return;
        Tabs.Move(i, i - 1);
        SaveTabs();
    }

    [RelayCommand]
    private void MoveTabDown(TabPreferenceViewModel? tab)
    {
        if (tab is null || Tabs.IndexOf(tab) is var i && (i < 0 || i >= Tabs.Count - 1))
            return;
        Tabs.Move(i, i + 1);
        SaveTabs();
    }

    [RelayCommand]
    private async Task ChoosePythonAsync()
    {
        var files = await Pickers.OpenFilesAsync("Python interpreter", false, "Python", OperatingSystem.IsWindows() ? "python*.exe" : "python*");
        if (files.Count == 0)
            return;
        Save(s => s.PythonOverride = files[0]);
        await _services.StartAsync();
    }

    [RelayCommand]
    private Task RecheckEngine() => _services.StartAsync();

    [RelayCommand]
    private void OpenLogs() => Shell.OpenFolder(_services.Paths.LogsDir);

    [RelayCommand]
    private void OpenData() => Shell.OpenFolder(_services.Paths.DataDir);

    [RelayCommand]
    private static void OpenRepo() => Shell.OpenUrl("https://github.com/holid4y/EternalTF2Launcher");

    [RelayCommand]
    private static void OpenPreloader() => Shell.OpenUrl("https://github.com/cueki/casual-pre-loader");
}
