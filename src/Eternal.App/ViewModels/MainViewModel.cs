using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.App.Theming;
using Eternal.Core;
using Eternal.Core.Engine;
using Eternal.Core.Feed;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Platform;
using Eternal.Core.Settings;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed partial class TabViewModel(string id, string title, PageViewModel page) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public PageViewModel Page { get; } = page;

    [ObservableProperty] private bool _hasAlert;
}

public sealed partial class NoticeViewModel(string message, bool isError, Action<NoticeViewModel> dismiss) : ObservableObject
{
    public string Message { get; } = message;
    public bool IsError { get; } = isError;

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}

public enum PlayState { Ready, Preparing, Starting, Running, Unavailable }

public sealed partial class MainViewModel : ObservableObject, IDialogs
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _gameWatch;
    private CancellationTokenSource? _launch;
    private DateTimeOffset? _launchedAt;

    public MainViewModel(AppServices services, ThemeService theme)
    {
        _services = services;
        Theme = theme;
        Home = new HomeViewModel(services, this);
        Mods = new ModsViewModel(services, this);
        Backpack = new BackpackViewModel(services, this);
        Config = new ConfigViewModel(services, this);
        Launcher = new LauncherViewModel(services, this, theme);

        BuildTabs();
        services.Launcher.Changed += () => Dispatcher.UIThread.Post(BuildTabs);
        services.Preload.StatusChanged += _ => Dispatcher.UIThread.Post(RefreshDock);
        services.Runtime.StatusChanged += _ => Dispatcher.UIThread.Post(RefreshDock);
        services.Game.Settings.Changed += _ => Dispatcher.UIThread.Post(RefreshDock);
        services.Notice += message => Dispatcher.UIThread.Post(() => Notify(message));
        foreach (var notice in services.Notices)
            Notify(notice);

        _gameWatch = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => WatchGame());
        _gameWatch.Start();
        RefreshDock();
    }

    public ThemeService Theme { get; }
    public HomeViewModel Home { get; }
    public ModsViewModel Mods { get; }
    public BackpackViewModel Backpack { get; }
    public ConfigViewModel Config { get; }
    public LauncherViewModel Launcher { get; }
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    [ObservableProperty] private TabViewModel? _selectedTab;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private PlayState _playState;
    [ObservableProperty] private string _playLabel = "PLAY";
    [ObservableProperty] private string _dockTitle = "";
    [ObservableProperty] private string _dockDetail = "";
    [ObservableProperty] private bool _dockBusy;
    [ObservableProperty] private bool _dockProblem;
    [ObservableProperty] private double _dockProgress;
    [ObservableProperty] private bool _dockIndeterminate;
    [ObservableProperty] private string _launchSummary = "";
    [ObservableProperty] private UpdateInfo? _update;

    public bool CanPlay => PlayState is PlayState.Ready or PlayState.Preparing && _launch is null;

    partial void OnSelectedTabChanged(TabViewModel? value) => value?.Page.Activate();

    partial void OnPlayStateChanged(PlayState value) => OnPropertyChanged(nameof(CanPlay));

    private void BuildTabs()
    {
        var pages = new Dictionary<string, (string Title, PageViewModel Page)>
        {
            ["home"] = ("Home", Home),
            ["mods"] = ("Mods", Mods),
            ["backpack"] = ("Backpack", Backpack),
            ["config"] = ("Config", Config),
            ["launcher"] = ("Launcher", Launcher),
        };
        var wanted = _services.Launcher.Settings.NormalizedTabs().Where(t => t.Visible).Select(t => t.Id).ToList();
        if (Tabs.Select(t => t.Id).SequenceEqual(wanted))
            return;

        var selected = SelectedTab?.Id ?? _services.Launcher.Settings.StartTab;
        Tabs.Clear();
        foreach (var id in wanted)
            Tabs.Add(new TabViewModel(id, pages[id].Title.ToUpperInvariant(), pages[id].Page));
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == selected) ?? Tabs.First();
    }

    public void Navigate(string id)
    {
        if (Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SelectedTab = tab;
    }

    public async Task StartAsync()
    {
        try
        {
            await _services.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Engine start failed", ex);
            Notify($"The mod engine couldn't start: {ex.Message}", isError: true);
        }
        RefreshDock();
        Mods.EngineStarted();
        if (_services.Launcher.Settings.CheckForUpdates)
        {
            var current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(2, 0, 0);
            Update = await FeedService.CheckForUpdateAsync(current);
        }
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (Update is not null)
            Shell.OpenUrl(Update.Url);
    }

    public void Notify(string message, bool isError = false)
    {
        if (Notices.Any(n => n.Message == message))
            return;
        Notices.Add(new NoticeViewModel(message, isError, n => Notices.Remove(n)));
        while (Notices.Count > 3)
            Notices.RemoveAt(0);
    }

    private void RefreshDock()
    {
        var status = _services.Preload.Status;
        var engine = _services.Runtime.Status;
        var settings = _services.Game.Settings;
        var running = GameProcess.IsRunning();

        DockBusy = status.Phase == PreloadPhase.Building || engine.State is EngineState.Checking or EngineState.SettingUp;
        DockProblem = status.Phase is PreloadPhase.Failed or PreloadPhase.Blocked || engine.State is EngineState.Broken or EngineState.Missing;
        DockIndeterminate = DockBusy && status.Percent <= 0;
        DockProgress = status.Percent;

        (DockTitle, DockDetail) = (status.Phase, engine.State) switch
        {
            (PreloadPhase.Building, _) => ("Applying mods", status.Message),
            (PreloadPhase.Failed, _) => ("Mods not applied", status.Message),
            (PreloadPhase.Blocked, _) => ("Mods waiting", status.Message),
            (PreloadPhase.Pending, _) => ("Changes pending", "Applying in a moment…"),
            (_, EngineState.Checking) => ("Checking mod engine", ""),
            (_, EngineState.SettingUp) => ("Setting up mod engine", ""),
            (_, EngineState.NeedsSetup or EngineState.NoPython) => ("Mods unavailable", "The mod engine needs setup. Open the Mods tab."),
            (_, EngineState.Broken or EngineState.Missing) => ("Mod engine problem", engine.Message),
            _ when status.Installed => ($"{status.ActiveMods} mod{(status.ActiveMods == 1 ? "" : "s")} ready", status.AppliedAt is { } at ? $"Applied {Ago(at)}" : ""),
            _ => ("Vanilla game", "No mods applied"),
        };

        var mods = Tabs.FirstOrDefault(t => t.Id == "mods");
        if (mods is not null)
            mods.HasAlert = DockProblem;

        if (_launch is null)
        {
            PlayState = running ? PlayState.Running : string.IsNullOrWhiteSpace(_services.TfPath) ? PlayState.Unavailable : PlayState.Ready;
            PlayLabel = PlayState switch
            {
                PlayState.Running => "RUNNING",
                PlayState.Unavailable => "SET UP TF2",
                _ => "PLAY",
            };
        }

        var mode = settings.Get(SettingsCatalog.Get<ChoiceSetting>("DisplayMode"));
        var parts = new List<string>();
        if (settings.IsTouched(SettingsCatalog.ById["DisplayMode"]))
            parts.Add(mode.Label);
        if (settings.IsTouched(SettingsCatalog.ById["Width"]) || settings.IsTouched(SettingsCatalog.ById["Height"]))
            parts.Add($"{settings.Get(SettingsCatalog.Get<SliderSetting>("Width"))}×{settings.Get(SettingsCatalog.Get<SliderSetting>("Height"))}");
        var dx = settings.Get(SettingsCatalog.Get<ChoiceSetting>("DxLevel"));
        if (dx.Value != "0")
            parts.Add(dx.Label);
        LaunchSummary = parts.Count > 0 ? string.Join(" · ", parts) : "TF2's own video settings";
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (PlayState == PlayState.Unavailable)
        {
            Navigate("launcher");
            return;
        }
        if (_launch is not null || PlayState == PlayState.Running)
            return;

        _launch = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanPlay));
        try
        {
            PlayState = PlayState.Preparing;
            PlayLabel = "PREPARING";

            var saved = await _services.Game.FlushAsync();
            if (!saved.Autoexec.Applied && saved.Autoexec.Status != AutoexecStatus.NoGameFolder)
                Notify($"Your settings couldn't be written to autoexec.cfg: {saved.Autoexec.Error}", isError: true);

            var admission = await _services.Preload.AdmitLaunchAsync(allowStale: false, _launch.Token);
            if (!admission.Allowed)
            {
                if (admission.Problem == "TF2 is already starting.")
                    return;
                var anyway = await ConfirmAsync("Mods aren't ready",
                    $"{admission.Problem}\n\nLaunch anyway? TF2 will start without your latest mod changes.",
                    "Launch anyway", "Cancel");
                if (!anyway)
                    return;
                admission = await _services.Preload.AdmitLaunchAsync(allowStale: true, _launch.Token);
                if (!admission.Allowed)
                    return;
            }

            try
            {
                PlayState = PlayState.Starting;
                PlayLabel = "STARTING";
                var arguments = LaunchOptions.Build(_services.Game.Settings, admission.PreloadInstalled);
                GameLauncher.Launch(SteamLocator.FindRoot(), arguments);
                if (_services.Launcher.Settings.MinimizeOnLaunch)
                    MinimizeRequested?.Invoke();

                var deadline = DateTime.UtcNow.AddMinutes(2);
                while (!GameProcess.IsRunning() && DateTime.UtcNow < deadline)
                    await Task.Delay(1000, _launch.Token);
                if (GameProcess.IsRunning())
                    _launchedAt = DateTimeOffset.Now;
                else
                    Notify("Steam hasn't started TF2 after two minutes. Check the Steam window for a prompt or an update.", isError: true);
            }
            finally
            {
                _services.Preload.CompleteLaunch();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Launch failed", ex);
            await AlertAsync("Couldn't start TF2", ex.Message);
        }
        finally
        {
            _launch.Dispose();
            _launch = null;
            RefreshDock();
        }
    }

    [RelayCommand]
    private void CancelLaunch() => _launch?.Cancel();

    public event Action? MinimizeRequested;

    private void WatchGame()
    {
        var running = GameProcess.IsRunning();
        if (_launch is null && (running != (PlayState == PlayState.Running)))
            RefreshDock();

        // A preload that crashes the game does it while loading, within seconds.
        if (!running && _launchedAt is { } started && DateTimeOffset.Now - started < TimeSpan.FromSeconds(40) && PlayState != PlayState.Starting && _launch is null)
        {
            _launchedAt = null;
            var record = _services.Preload.Record;
            if (record.Installed)
                Notify(EarlyExitNotice(DateTimeOffset.Now - started, record), isError: true);
        }
        else if (running && _launchedAt is not null && DateTimeOffset.Now - _launchedAt > TimeSpan.FromSeconds(40))
        {
            _launchedAt = null;
        }
    }

    // States what the game was running with; which mod is at fault can't be known from here.
    private static string EarlyExitNotice(TimeSpan after, PreloadRecord record)
    {
        var mods = record.Addons.Concat(record.Particles).ToList();
        var names = string.Join(", ", mods.Take(3)) + (mods.Count > 3 ? $" and {mods.Count - 3} more" : "");
        var warnings = record.Warnings.Count switch
        {
            0 => "",
            1 => " Its last build reported a warning, shown on the Mods tab.",
            var n => $" Its last build reported {n} warnings, shown on the Mods tab.",
        };
        return $"TF2 closed {Math.Round(after.TotalSeconds)} s after starting, with {mods.Count} mod{(mods.Count == 1 ? "" : "s")} applied ({names})."
            + warnings + " To find out whether the mods are the cause, use Restore vanilla and start TF2 again.";
    }

    public async Task PrepareForExitAsync()
    {
        if (_services.Preload.Status.Phase == PreloadPhase.Building)
        {
            Dialog = new DialogViewModel { Title = "Finishing up", Message = "Waiting for the mod build to finish so the game files stay intact…", ConfirmText = "" };
            await _services.Preload.ShutdownAsync();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm = "OK", string cancel = "Cancel", bool danger = false)
    {
        var dialog = new DialogViewModel { Title = title, Message = message, ConfirmText = confirm, CancelText = cancel, Danger = danger };
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    public async Task AlertAsync(string title, string message)
    {
        var dialog = new DialogViewModel { Title = title, Message = message };
        Dialog = dialog;
        await dialog.Result;
        if (Dialog == dialog)
            Dialog = null;
    }

    public async Task<string?> PromptAsync(string title, string message, string initial = "", string confirm = "Save")
    {
        var dialog = new DialogViewModel { Title = title, Message = message, ConfirmText = confirm, CancelText = "Cancel", HasInput = true, Input = initial };
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog.Input.Trim() : null;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : when.LocalDateTime.ToString("d MMM");
    }
}

public abstract class PageViewModel : ObservableObject
{
    private bool _activated;

    public void Activate()
    {
        if (_activated)
        {
            OnReturn();
            return;
        }
        _activated = true;
        OnFirstActivation();
    }

    protected virtual void OnFirstActivation() { }
    protected virtual void OnReturn() { }
}
