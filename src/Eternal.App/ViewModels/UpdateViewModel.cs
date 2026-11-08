using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Platform;
using Eternal.Core.Updates;

namespace Eternal.App.ViewModels;

public enum UpdateStep { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Finds, downloads and installs launcher updates. A ready update installs when the
/// launcher closes; Restart now closes it straight away and opens the new version.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IHoldable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan CardFor = TimeSpan.FromSeconds(12);
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly UpdateClient _client;
    private readonly Version _current;
    private readonly DispatcherTimer _schedule;
    private Countdown? _card;
    private PreparedUpdate? _prepared;
    private bool _restart;

    public UpdateViewModel(AppServices services, MainViewModel main, UpdateClient client, Version current)
    {
        _services = services;
        _main = main;
        _client = client;
        _current = current;
        Kind = UpdateInstaller.Detect(services.Paths.IsPortable);
        _schedule = new DispatcherTimer(CheckEvery, DispatcherPriority.Background, (_, _) =>
        {
            if (Settings.CheckForUpdates && Step is not (UpdateStep.Downloading or UpdateStep.Ready))
                _ = CheckAsync(manual: false);
        });
    }

    public InstallKind Kind { get; }
    private readonly bool _canWrite = UpdateInstaller.CanWriteTo(AppContext.BaseDirectory);
    public string CurrentVersion => _current.ToString(3);
    private string UpdatesDir => Path.Combine(_services.Paths.CacheDir, "updates");
    private LauncherSettings Settings => _services.Launcher.Settings;

    [ObservableProperty] private UpdateStep _step;
    [ObservableProperty] private Release? _release;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private DateTimeOffset? _checkedAt;
    [ObservableProperty] private bool _cardOpen;

    public string NewVersion => Release?.VersionText ?? "";
    public bool CanUpdateItself => Kind != InstallKind.Unmanaged;
    public bool CanSelfUpdate => CanUpdateItself && _canWrite && Release?.AssetFor(Kind) is not null;
    public string ActionLabel => CanSelfUpdate ? $"Update · {Format.Size(Release!.AssetFor(Kind)!.Size)}" : "Open the releases page";
    public bool IsReady => Step == UpdateStep.Ready;
    public bool IsDownloading => Step == UpdateStep.Downloading;
    public bool IsAvailable => Step == UpdateStep.Available;
    public bool IsFailed => Step == UpdateStep.Failed;
    public bool IsBusy => Step is UpdateStep.Checking or UpdateStep.Downloading;
    public bool HasNews => Step is UpdateStep.Available or UpdateStep.Downloading or UpdateStep.Ready;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Release?.Notes);
    public string Notes => Release?.Notes.Split("\n\n", 2)[0] ?? "";
    public string ProgressText => Release?.AssetFor(Kind) is { } asset ? $"{Progress:P0} of {Format.Size(asset.Size)}" : $"{Progress:P0}";

    public string Status => Step switch
    {
        UpdateStep.Checking => "Checking for updates…",
        UpdateStep.UpToDate => "You're on the latest version.",
        UpdateStep.Available when CanSelfUpdate => $"Version {NewVersion} is out.",
        UpdateStep.Available when !_canWrite => $"Version {NewVersion} is out, but the launcher can't write to its own folder, so get it from the releases page.",
        UpdateStep.Available => $"Version {NewVersion} is out. This copy wasn't installed with the installer or install.sh, so get it from the releases page.",
        UpdateStep.Downloading => $"Downloading version {NewVersion}… {ProgressText}",
        UpdateStep.Ready => $"Version {NewVersion} is downloaded. Restart to install it, or it installs the next time you quit.",
        UpdateStep.Failed => Problem ?? "",
        _ when !Settings.CheckForUpdates => "Update checks are off.",
        _ => "",
    };

    public string Checked => CheckedAt is { } at ? $"Checked {MainViewModel.Ago(at)}" : "";

    public string CardTitle => Step switch
    {
        UpdateStep.Ready => $"Eternal {NewVersion} is ready",
        UpdateStep.Downloading => $"Downloading Eternal {NewVersion}",
        _ => $"Eternal {NewVersion} is out",
    };

    public string CardText => Step switch
    {
        UpdateStep.Ready => "Restart to finish updating. Your mods and settings stay as they are.",
        UpdateStep.Downloading => "It installs when you quit the launcher.",
        _ when CanSelfUpdate => "Download it now and it installs when you restart.",
        _ => "Get it from the releases page.",
    };

    public string SidebarText => Step switch
    {
        UpdateStep.Ready => "Update ready",
        UpdateStep.Downloading => $"Updating · {Progress:P0}",
        UpdateStep.Available => $"Version {NewVersion} is out",
        _ => "",
    };

    public bool Held { set => _card?.Held = value; }

    partial void OnStepChanged(UpdateStep value)
    {
        foreach (var name in new[] { nameof(IsReady), nameof(IsDownloading), nameof(IsAvailable), nameof(IsFailed), nameof(IsBusy),
                     nameof(HasNews), nameof(Status), nameof(CardTitle), nameof(CardText), nameof(SidebarText) })
            OnPropertyChanged(name);
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnReleaseChanged(Release? value)
    {
        foreach (var name in new[] { nameof(NewVersion), nameof(CanSelfUpdate), nameof(ActionLabel), nameof(HasNotes), nameof(Notes), nameof(Status),
                     nameof(CardTitle), nameof(CardText) })
            OnPropertyChanged(name);
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(SidebarText));
    }

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(Status));
    partial void OnCheckedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(Checked));

    public async Task StartAsync()
    {
        await ResumeOrCleanUpAsync();
        _schedule.Start();
        if (Step == UpdateStep.Ready)
            return;
        if (Settings.CheckForUpdates)
            await CheckAsync(manual: false);
        else
            OnPropertyChanged(nameof(Status));
    }

    /// <summary>Runs the check or download the new settings allow.</summary>
    public void SettingsChanged()
    {
        OnPropertyChanged(nameof(Status));
        if (!Settings.CheckForUpdates)
            return;
        if (Step is UpdateStep.Idle or UpdateStep.Failed)
            _ = CheckAsync(manual: false);
        else if (Step == UpdateStep.Available && Settings.AutoUpdate && CanSelfUpdate)
            _ = DownloadAsync();
    }

    private bool CanCheck() => !IsBusy && Step != UpdateStep.Ready;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (IsBusy || Step == UpdateStep.Ready)
            return;
        Step = UpdateStep.Checking;
        try
        {
            var release = await _client.NewerThanAsync(_current);
            CheckedAt = DateTimeOffset.Now;
            Problem = null;
            if (release is null)
            {
                Release = null;
                Step = UpdateStep.UpToDate;
                return;
            }
            Release = release;
            Step = UpdateStep.Available;
            Log.Info($"Launcher {release.VersionText} is out ({Kind})");
            if (Settings.AutoUpdate && CanSelfUpdate)
                await DownloadAsync();
            else
                OpenCard();
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.Message}");
            CheckedAt = DateTimeOffset.Now;
            Problem = ex is UpdateException ? ex.Message : "The update check ran into a problem. The launcher tries again later.";
            Step = UpdateStep.Failed;
            if (manual)
                _main.Notify(Problem, isError: true);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Release is not { } release || Step is UpdateStep.Downloading or UpdateStep.Ready)
            return;
        if (!CanSelfUpdate || release.AssetFor(Kind) is not { } asset)
        {
            OpenPage();
            return;
        }
        Step = UpdateStep.Downloading;
        Progress = 0;
        try
        {
            ArchiveIngest.TryDelete(UpdatesDir);
            var file = await _client.DownloadAsync(asset, Path.Combine(UpdatesDir, release.VersionText),
                new Progress<double>(p => Progress = p));
            _prepared = await Task.Run(() =>
            {
                var prepared = UpdateInstaller.Prepare(Kind, release.Version, file);
                UpdateInstaller.Remember(prepared, file);
                return prepared;
            });
            Step = UpdateStep.Ready;
            OpenCard();
        }
        catch (Exception ex)
        {
            Log.Warn($"Update to {release.VersionText} failed: {ex.Message}");
            _prepared = null;
            ArchiveIngest.TryDelete(UpdatesDir);
            Problem = ex is UpdateException ? ex.Message : $"The update couldn't be prepared: {ex.Message}";
            Step = UpdateStep.Failed;
            CardOpen = false;
            _main.Notify(Problem, isError: true);
        }
    }

    [RelayCommand]
    private void RestartNow()
    {
        if (_prepared is null)
            return;
        try
        {
            UpdateInstaller.WriteScript(_prepared);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The update can't be installed from {_prepared.WorkDir}: {ex.Message}");
            _main.Notify($"The update couldn't be set up, so the launcher stayed open: {ex.Message}", isError: true);
            return;
        }
        _restart = true;
        CardOpen = false;
        _main.RequestQuit();
    }

    [RelayCommand]
    private void Later()
    {
        _card?.Stop();
        CardOpen = false;
    }

    [RelayCommand]
    private void ShowCard()
    {
        if (HasNews)
            OpenCard();
    }

    [RelayCommand]
    private void OpenPage() => Shell.OpenUrl(Release?.PageUrl ?? UpdateClient.ReleasesPage);

    /// <summary>Called as the launcher closes. If the script can't start, the download is still there for the next start.</summary>
    public void FinishOnExit()
    {
        _schedule.Stop();
        if (_prepared is null)
            return;
        try
        {
            UpdateInstaller.Launch(_prepared, reopen: _restart);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"The update to {_prepared.Version.ToString(3)} couldn't be started: {ex.Message}");
        }
    }

    private void OpenCard()
    {
        _card?.Stop();
        CardOpen = true;
        _card = Step == UpdateStep.Ready ? null : new Countdown(CardFor, () => CardOpen = false);
    }

    private async Task ResumeOrCleanUpAsync()
    {
        try
        {
            if (UpdateInstaller.TakeResult(UpdatesDir) is { Version: var version, Problem: var problem })
            {
                if (problem is null && version.ToString(3) == CurrentVersion)
                    _main.Notify($"Eternal was updated to {CurrentVersion}. Your mods and settings are as you left them.");
                else if (problem is not null)
                    _main.Notify($"The update to {version.ToString(3)} didn't install: {problem}. This version still works, and the next check tries again.", isError: true);
                ArchiveIngest.TryDelete(UpdatesDir);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read how the last update went: {ex.Message}");
        }

        if (CanUpdateItself && _canWrite && await Task.Run(() => UpdateInstaller.Resume(UpdatesDir, Kind, _current)) is { } pending)
        {
            _prepared = pending;
            Release = new Release(pending.Version, UpdateClient.ReleasesPage, "", []);
            Step = UpdateStep.Ready;
            OpenCard();
            return;
        }
        ArchiveIngest.TryDelete(UpdatesDir);
    }
}
