using LauncherTF2.Core;
using LauncherTF2.Models;
using LauncherTF2.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Data;
using System.Windows.Input;

namespace LauncherTF2.ViewModels;

/// <summary>
/// ViewModel for the Mods tab. The tab is built entirely around the Casual
/// Preloader engine: importing a mod adds it to the engine library and toggles
/// set the load order.
///
/// "Instant Readiness": there is no manual "Apply Preload" button in the happy
/// path. After an import, toggle, or remove, a debounced background "bake"
/// (engine install) rebuilds the preload into <c>tf/custom</c> automatically.
/// Rapid changes are coalesced into a single bake.
///
/// A running bake is NEVER interrupted: the engine writes into real game files
/// (patches PCFs inside tf2_misc VPKs, edits gameinfo.txt, compiles precache
/// models), so killing it mid-write can leave TF2 crashing on startup.
/// Interactive actions instead queue behind the engine's single-flight gate
/// and trigger a fresh coalesced bake once their change lands. A manual "Sync"
/// button appears only when an auto-bake can't run (TF2 open, no path, or a
/// failure) so the user always has an escape hatch.
/// </summary>
public class ModsViewModel : ViewModelBase
{
    private static readonly TimeSpan AutoBakeDebounce = TimeSpan.FromMilliseconds(1500);

    private readonly IPreloaderService _preloader;

    private ICollectionView _filteredView;
    private string _searchQuery = string.Empty;
    private string _currentFilter = "All";
    private bool _isGridView = true;
    private bool _isDropPanelExpanded = true;
    private bool _isBusy;
    private int _percent;
    private string _statusMessage = string.Empty;

    // Instant-readiness bake state
    private bool _isBaking;
    private bool _hasUnbakedChanges;
    private bool _bakeBlocked;
    private bool _bakeWorkerActive;
    private int _bakeRequestId;

    public ThemeManagerService Theme => ServiceLocator.Theme;

    public ObservableCollection<PreloaderAddonModel> AllAddons { get; } = new();

    public ICollectionView FilteredView
    {
        get => _filteredView;
        private set => SetProperty(ref _filteredView, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (SetProperty(ref _searchQuery, value)) FilteredView?.Refresh(); }
    }

    public string CurrentFilter
    {
        get => _currentFilter;
        set { if (SetProperty(ref _currentFilter, value)) FilteredView?.Refresh(); }
    }

    public bool IsGridView
    {
        get => _isGridView;
        set => SetProperty(ref _isGridView, value);
    }

    public bool IsDropPanelExpanded
    {
        get => _isDropPanelExpanded;
        set => SetProperty(ref _isDropPanelExpanded, value);
    }

    /// <summary>
    /// True while a short interactive engine call runs (import/toggle/remove).
    /// Serializes those commands. The background bake uses <see cref="IsBaking"/>
    /// instead so it never disables the UI.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>True while the debounced background bake (engine install) runs.</summary>
    public bool IsBaking
    {
        get => _isBaking;
        private set
        {
            if (SetProperty(ref _isBaking, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(ShowSyncButton));
                OnPropertyChanged(nameof(HasBakeWarnings));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Any engine work in progress — drives the progress bar.</summary>
    public bool IsWorking => IsBusy || IsBaking;

    public int Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Manual "Sync" affordance — shown only when an auto-bake couldn't run
    /// (TF2 open, no TF2 path, or a failed bake) so changes aren't stuck.
    /// </summary>
    public bool ShowSyncButton => _bakeBlocked && !IsBaking && _preloader.IsAvailable;

    private string[] _lastBakeWarnings = [];

    /// <summary>
    /// Non-fatal warnings about the current preload — either from the last bake
    /// (e.g. a structurally broken model skipped) or from the proactive
    /// compatibility scan (e.g. an IK-chain mismatch that will load as the
    /// vanilla model in Casual). Surfaced as an amber sync dot so the user is
    /// told *why* a mod won't take effect instead of seeing a silent vanilla.
    /// </summary>
    public bool HasBakeWarnings => _lastBakeWarnings.Length > 0 && !IsBaking && !ShowSyncButton;

    public string BakeWarningsTooltip => _lastBakeWarnings.Length == 0
        ? string.Empty
        : "Preload warnings:\n• " + string.Join("\n• ", _lastBakeWarnings)
          + "\n\nSee logs/preloader.log for details.";

    private void SetBakeWarnings(string[] warnings)
    {
        _lastBakeWarnings = warnings ?? [];
        OnPropertyChanged(nameof(HasBakeWarnings));
        OnPropertyChanged(nameof(BakeWarningsTooltip));
    }

    public bool IsPreloaderAvailable => _preloader.IsAvailable;
    public bool IsPreloaderUnavailable => !_preloader.IsAvailable;
    public string EngineDescription => _preloader.EngineDescription;

    public int TotalCount    => AllAddons.Count;
    public int ActiveCount   => AllAddons.Count(a => a.IsEnabled);
    public int InactiveCount => AllAddons.Count(a => !a.IsEnabled);
    public int ParticleCount => AllAddons.Count(a => a.IsParticleMod);

    public ICommand ToggleViewModeCommand { get; }
    public ICommand ChangeFilterCommand { get; }
    public ICommand ToggleDropPanelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCustomFolderCommand { get; }
    public ICommand ChangeThumbnailCommand { get; }
    public ICommand RemoveThumbnailCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ToggleAddonCommand { get; }
    public ICommand RemoveAddonCommand { get; }
    public ICommand SyncNowCommand { get; }
    public ICommand RemovePreloadCommand { get; }
    public ICommand DownloadPythonCommand { get; }

    public ModsViewModel()
    {
        _preloader = ServiceLocator.Preloader;

        _filteredView = CollectionViewSource.GetDefaultView(AllAddons);
        _filteredView.Filter = FilterAddon;

        ToggleViewModeCommand = new RelayCommand(_ => IsGridView = !IsGridView);
        ChangeFilterCommand   = new RelayCommand(o => CurrentFilter = o?.ToString() ?? "All");
        ToggleDropPanelCommand = new RelayCommand(_ => IsDropPanelExpanded = !IsDropPanelExpanded);
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanRunEngine());
        OpenCustomFolderCommand = new RelayCommand(_ => OpenCustomFolder());

        // Thumbnails are launcher-side cosmetics (no engine involvement), so
        // they stay usable even while the engine is busy.
        ChangeThumbnailCommand = new RelayCommand(
            o => ChangeThumbnail(o as PreloaderAddonModel),
            o => o is PreloaderAddonModel);
        RemoveThumbnailCommand = new RelayCommand(
            o => RemoveThumbnail(o as PreloaderAddonModel),
            o => o is PreloaderAddonModel { ThumbnailPath: not null });

        ImportCommand  = new AsyncRelayCommand(_ => ImportViaDialogAsync(), _ => CanRunEngine());
        ToggleAddonCommand = new AsyncRelayCommand(
            o => ToggleAddonAsync(o as PreloaderAddonModel),
            o => CanRunEngine() && o is PreloaderAddonModel { IsParticleMod: false });
        RemoveAddonCommand = new AsyncRelayCommand(
            o => RemoveAddonAsync(o as PreloaderAddonModel),
            o => CanRunEngine() && o is PreloaderAddonModel);
        SyncNowCommand = new AsyncRelayCommand(_ => SyncNowAsync(),
            _ => _preloader.IsAvailable && !IsBusy && !IsBaking);
        RemovePreloadCommand = new AsyncRelayCommand(_ => RemovePreloadAsync(), _ => CanRunEngine());
        DownloadPythonCommand = new RelayCommand(_ =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.python.org/downloads/windows/") { UseShellExecute = true }); }
            catch (Exception ex) { Logger.LogError("[Mods] Failed to open Python download page", ex); }
        });

        StatusMessage = _preloader.IsAvailable
            ? "Loading mod library..."
            : "Casual Preloader engine not found — check Settings → Launcher.";

        if (_preloader.IsAvailable)
        {
            _ = RefreshAsync().ContinueWith(t =>
            {
                if (t.Exception != null)
                    Logger.LogError("[Mods] Initial library load crashed", t.Exception.Flatten());
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private bool CanRunEngine() => _preloader.IsAvailable && !IsBusy;

    // ─────────────────────────── load / refresh ───────────────────────────

    public async Task RefreshAsync()
    {
        // Don't query the engine while a bake holds it — the in-memory list is
        // already current, and blocking here would freeze the tab for the bake.
        if (!_preloader.IsAvailable || IsBusy || IsBaking) return;
        IsBusy = true;
        try
        {
            var report = await _preloader.GetReportAsync();

            AllAddons.Clear();
            if (report == null)
            {
                StatusMessage = "Could not read the preloader library — see logs/preloader.log.";
                RefreshCounts();
                return;
            }

            var enabled = new HashSet<string>(report.EnabledAddons, StringComparer.OrdinalIgnoreCase);
            foreach (var addon in report.Addons.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                AllAddons.Add(new PreloaderAddonModel
                {
                    Name = addon.Name,
                    DisplayName = addon.DisplayName,
                    Type = addon.Type,
                    IsParticleMod = addon.IsParticleMod,
                    // particle mods are always applied; regular addons follow the load order
                    IsEnabled = addon.IsParticleMod || enabled.Contains(addon.Name),
                    ThumbnailPath = ThumbnailStore.Find(addon.Name),
                });
            }

            FilteredView?.Refresh();
            RefreshCounts();
            if (!IsBaking)
                StatusMessage = AllAddons.Count == 0
                    ? "No mods imported yet — drop files below or use Import."
                    : $"{AllAddons.Count} mod(s) • {ActiveCount} active • engine {report.EngineVersion}";

            // Proactive compatibility scan: flag mods that will load as vanilla in
            // Casual (per-card badge + aggregate amber dot) *before* the user spends
            // a launch on it. Read-only, best-effort — never blocks the library load.
            try
            {
                var issues = await _preloader.GetCompatibilityIssuesAsync(
                    AllAddons.Select(a => a.Name).ToArray());

                var byAddon = issues
                    .GroupBy(i => i.Addon, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var addon in AllAddons)
                {
                    if (byAddon.TryGetValue(addon.Name, out var addonIssues))
                    {
                        addon.Compat = addonIssues.Any(i => i.Severity == "error")
                            ? PreloaderAddonModel.CompatStatus.Incompatible
                            : PreloaderAddonModel.CompatStatus.Warning;
                        addon.CompatMessage = string.Join("\n", addonIssues.Select(i => i.Message));
                    }
                    else
                    {
                        addon.Compat = PreloaderAddonModel.CompatStatus.Compatible;
                        addon.CompatMessage = null;
                    }
                }

                SetBakeWarnings(issues.Select(i => i.Message).ToArray());
                if (issues.Count > 0 && !IsBaking)
                    StatusMessage = $"{byAddon.Count} mod(s) may not load in Casual — see the badge on the card.";
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Mods] Compatibility scan failed: {ex.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─────────────────────────── import ───────────────────────────

    /// <summary>Drag-and-drop entry point (used by ModsView code-behind).</summary>
    public async Task HandleDropAsync(string[] paths)
    {
        if (paths is { Length: > 0 }) await ImportCoreAsync(paths);
    }

    private async Task ImportViaDialogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import mods into the Casual Preloader",
            Filter = "Mod files (*.vpk;*.zip;*.rar;*.7z)|*.vpk;*.zip;*.rar;*.7z|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
            await ImportCoreAsync(dialog.FileNames);
    }

    private async Task ImportCoreAsync(string[] paths)
    {
        if (!_preloader.IsAvailable)
        {
            StatusMessage = "Engine not available — cannot import.";
            return;
        }
        if (IsBusy) return;

        IsBusy = true;

        var tempRoots = new List<string>();
        var namesBefore = new HashSet<string>(AllAddons.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var importedOk = false;
        try
        {
            Percent = 0;
            StatusMessage = "Preparing import...";

            // RAR/7z aren't readable by the engine — repack them to a temp zip.
            var toImport = new List<string>();
            var preFailures = new List<string>();
            foreach (var path in paths)
            {
                if (ArchiveExtractor.NeedsRepack(path))
                {
                    var zip = ArchiveExtractor.RepackToZip(path, out var tempRoot);
                    tempRoots.Add(tempRoot);
                    if (zip != null) toImport.Add(zip);
                    else preFailures.Add(Path.GetFileName(path));
                }
                else
                {
                    toImport.Add(path);
                }
            }

            if (toImport.Count == 0)
            {
                StatusMessage = "Nothing importable in the dropped items.";
                if (preFailures.Count > 0)
                    Views.MessageDialog.ShowError("Import Failed",
                        "Could not read:\n" + string.Join("\n", preFailures));
                return;
            }

            // Never interrupt a running bake — the import call below simply
            // queues behind the engine gate until the bake finishes.
            if (_preloader.IsRunning)
                StatusMessage = "Finishing the current sync, then importing...";

            var progress = new Progress<PreloaderProgress>(p =>
            {
                Percent = p.Percent;
                StatusMessage = p.Message;
            });

            var result = await _preloader.ImportAsync(toImport.ToArray(), progress);

            if (!result.Success)
            {
                StatusMessage = "Import failed.";
                var detail = result.ErrorSummary;
                if (preFailures.Count > 0)
                    detail += "\n\nAlso could not read: " + string.Join(", ", preFailures);
                
                // Don't early return here, we want the finally block to execute,
                // and then RefreshAsync to run for any successful partial imports.
                Views.MessageDialog.ShowError("Import Failed", detail);
            }
            else
            {
                importedOk = true;
                StatusMessage = "Import complete.";
            }
        }
        finally
        {
            foreach (var root in tempRoots) ArchiveExtractor.Cleanup(root);
            Percent = 0;
            IsBusy = false;
        }

        // Fix: Always refresh even if there were partial failures,
        // so any successfully imported mods appear immediately.
        await RefreshAsync();

        if (importedOk)
        {
            // "Instant Readiness": newly imported mods come in Active. Enable any
            // new non-particle addons (particles are always active), then bake.
            await EnableNewlyImportedAsync(namesBefore);
            ScheduleAutoBake();
        }
    }

    private async Task EnableNewlyImportedAsync(HashSet<string> namesBefore)
    {
        var newAddons = AllAddons
            .Where(a => !a.IsParticleMod && !a.IsEnabled && !namesBefore.Contains(a.Name))
            .ToList();

        foreach (var addon in newAddons)
        {
            try
            {
                var result = await _preloader.EnableAsync(addon.Name);
                if (result.Success) addon.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Mods] Could not auto-enable imported mod '{addon.Name}': {ex.Message}");
            }
        }

        if (newAddons.Count > 0)
        {
            RefreshCounts();
            FilteredView?.Refresh();
        }
    }

    // ─────────────────────────── per-addon actions ───────────────────────────

    private async Task ToggleAddonAsync(PreloaderAddonModel? addon)
    {
        if (addon == null || addon.IsParticleMod || IsBusy) return;

        IsBusy = true;
        try
        {
            if (_preloader.IsRunning)
                StatusMessage = "Finishing the current sync, then applying your change...";

            var enable = !addon.IsEnabled;
            var result = enable
                ? await _preloader.EnableAsync(addon.Name)
                : await _preloader.DisableAsync(addon.Name);

            if (result.Success)
            {
                addon.IsEnabled = enable;
                RefreshCounts();
                FilteredView?.Refresh();
                StatusMessage = $"{(enable ? "Enabled" : "Disabled")} {addon.DisplayName} • {ActiveCount} active.";
                ScheduleAutoBake();
            }
            else
            {
                // The click already flipped the checkbox visually; the OneWay
                // binding won't revert it on its own since IsEnabled is unchanged.
                addon.RaiseEnabledChanged();
                StatusMessage = $"Failed to {(enable ? "enable" : "disable")} {addon.DisplayName}: {result.ErrorSummary}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveAddonAsync(PreloaderAddonModel? addon)
    {
        if (addon == null || IsBusy) return;

        var confirmed = Views.MessageDialog.ShowConfirm(
            "Remove Mod",
            $"Permanently remove \"{addon.DisplayName}\" from the preloader library?");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            if (_preloader.IsRunning)
                StatusMessage = "Finishing the current sync, then removing...";

            var result = await _preloader.RemoveAsync(addon.Name);
            if (result.Success)
            {
                AllAddons.Remove(addon);
                ThumbnailStore.Delete(addon.Name);
                RefreshCounts();
                FilteredView?.Refresh();
                StatusMessage = $"Removed {addon.DisplayName}.";
                ScheduleAutoBake();
            }
            else
            {
                StatusMessage = $"Failed to remove {addon.DisplayName}: {result.ErrorSummary}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Right-click → "Change Thumbnail": pick an image, crop it to 16:9, store
    /// it in the launcher's AppData (never inside the engine's mod folders —
    /// files there would be baked into the game VPKs on the next sync).
    /// </summary>
    private void ChangeThumbnail(PreloaderAddonModel? addon)
    {
        if (addon == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a thumbnail for {addon.DisplayName}",
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var cropDialog = new Views.ImageCropDialog(dialog.FileName)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            if (cropDialog.ShowDialog() != true || cropDialog.CroppedImagePath == null) return;

            var stored = ThumbnailStore.Save(addon.Name, cropDialog.CroppedImagePath);
            if (stored == null)
            {
                Views.MessageDialog.ShowError("Thumbnail Failed", "The cropped image could not be saved.");
                return;
            }

            addon.ThumbnailPath = stored;
            StatusMessage = $"Updated thumbnail for {addon.DisplayName}.";
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Changing thumbnail for '{addon.Name}' failed", ex);
            Views.MessageDialog.ShowError("Thumbnail Failed", ex.Message);
        }
    }

    private void RemoveThumbnail(PreloaderAddonModel? addon)
    {
        if (addon?.ThumbnailPath == null) return;
        ThumbnailStore.Delete(addon.Name);
        addon.ThumbnailPath = null;
        StatusMessage = $"Removed thumbnail for {addon.DisplayName}.";
    }

    // ─────────────────────────── instant-readiness bake ───────────────────────────

    /// <summary>
    /// Marks the preload dirty and (re)starts the debounced background bake.
    /// Coalesces rapid changes: each call bumps a request id so the worker
    /// re-waits the debounce window, and a single bake runs once changes settle.
    /// </summary>
    private void ScheduleAutoBake()
    {
        _hasUnbakedChanges = true;
        _bakeBlocked = false;
        OnPropertyChanged(nameof(ShowSyncButton));
        Interlocked.Increment(ref _bakeRequestId);

        if (_bakeWorkerActive) return;
        _bakeWorkerActive = true;
        _ = AutoBakeLoopAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                Logger.LogError("[Mods] Auto-bake loop crashed", t.Exception.Flatten());
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task AutoBakeLoopAsync()
    {
        try
        {
            while (_hasUnbakedChanges)
            {
                var seen = Volatile.Read(ref _bakeRequestId);

                try { await Task.Delay(AutoBakeDebounce); }
                catch (TaskCanceledException) { }

                // More changes arrived during the debounce — restart the window.
                if (Volatile.Read(ref _bakeRequestId) != seen) continue;

                // Don't fight an interactive engine call; wait for it to finish.
                if (IsBusy) { await Task.Delay(300); continue; }

                var outcome = await TryBakeAsync(auto: true);
                if (outcome == BakeOutcome.Success)
                {
                    // Only clear dirty if no new change slipped in while baking.
                    if (Volatile.Read(ref _bakeRequestId) == seen)
                        _hasUnbakedChanges = false;
                }
                else
                {
                    // Can't bake right now (TF2 open / no path / failure). Stop
                    // and surface the manual Sync button; the user retries.
                    _bakeBlocked = true;
                    OnPropertyChanged(nameof(ShowSyncButton));
                    break;
                }
            }
        }
        finally
        {
            _bakeWorkerActive = false;
        }
    }

    private enum BakeOutcome { Success, Blocked }

    /// <summary>
    /// Runs one engine install. Deliberately NOT cancellable: the install
    /// patches real game files (tf2_misc VPKs, gameinfo.txt, precache models),
    /// and killing the engine mid-write has corrupted TF2 startup before.
    /// Coalescing happens by re-baking after the fact, never by interrupting.
    /// </summary>
    private async Task<BakeOutcome> TryBakeAsync(bool auto)
    {
        var tfPath = ServiceLocator.Settings.GetSettings()?.SteamPath;
        if (string.IsNullOrWhiteSpace(tfPath))
        {
            StatusMessage = "Set your TF2 path in Settings to sync mods to the game.";
            return BakeOutcome.Blocked;
        }
        if (ServiceLocator.Game.IsGameRunning())
        {
            StatusMessage = "Close TF2 to sync your mod changes, then click Sync.";
            return BakeOutcome.Blocked;
        }

        IsBaking = true;
        try
        {
            Percent = 0;
            StatusMessage = "Syncing mods to the game...";

            var progress = new Progress<PreloaderProgress>(p =>
            {
                Percent = p.Percent;
                StatusMessage = p.Message;
            });

            var result = await _preloader.InstallAsync(tfPath, progress);

            if (result.Success)
            {
                _bakeBlocked = false;
                SetBakeWarnings(result.Warnings);

                var status = ActiveCount == 0
                    ? "Synced — no active mods (game is clean)."
                    : $"Synced — {ActiveCount} mod(s) ready in Casual.";

                if (result.Warnings.Length > 0)
                {
                    status += $" ({result.Warnings.Length} warning(s) — hover the dot)";
                    Logger.LogWarning($"[Mods] Auto-bake completed with {result.Warnings.Length} warning(s): {string.Join("; ", result.Warnings)}");
                }

                StatusMessage = status;
                Logger.LogInfo($"[Mods] Auto-bake installed preload in {result.DurationMs} ms");
                return BakeOutcome.Success;
            }

            StatusMessage = $"Sync failed: {result.ErrorSummary}";
            return BakeOutcome.Blocked;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Mods] Auto-bake failed", ex);
            StatusMessage = $"Sync failed: {ex.Message}";
            return BakeOutcome.Blocked;
        }
        finally
        {
            Percent = 0;
            IsBaking = false;
        }
    }

    /// <summary>Manual "Sync" — runs the bake immediately (used when auto-bake was blocked).</summary>
    private async Task SyncNowAsync()
    {
        if (IsBusy || IsBaking) return;
        _hasUnbakedChanges = true;
        var outcome = await TryBakeAsync(auto: false);
        if (outcome == BakeOutcome.Success)
        {
            _hasUnbakedChanges = false;
            _bakeBlocked = false;
        }
        else if (outcome == BakeOutcome.Blocked)
        {
            _bakeBlocked = true;
        }
        OnPropertyChanged(nameof(ShowSyncButton));
    }

    private async Task RemovePreloadAsync()
    {
        if (IsBusy || IsBaking) return;

        var tfPath = ServiceLocator.Settings.GetSettings()?.SteamPath;
        if (string.IsNullOrWhiteSpace(tfPath))
        {
            StatusMessage = "TF2 path not configured — set it in Settings first.";
            return;
        }

        var confirmed = Views.MessageDialog.ShowConfirm(
            "Remove Preload",
            "Remove all preloader-generated content from TF2 and restore the clean state?\nYour imported mod library is kept.");
        if (!confirmed) return;

        if (ServiceLocator.Game.IsGameRunning())
        {
            StatusMessage = "Close TF2 before removing the preload.";
            return;
        }

        IsBusy = true;
        try
        {
            Percent = 0;
            StatusMessage = "Removing preload...";

            var progress = new Progress<PreloaderProgress>(p =>
            {
                Percent = p.Percent;
                StatusMessage = p.Message;
            });

            var result = await _preloader.UninstallAsync(tfPath, progress);
            if (result.Success)
            {
                _hasUnbakedChanges = false;
                _bakeBlocked = false;
                OnPropertyChanged(nameof(ShowSyncButton));
                SetBakeWarnings([]); // nothing is baked anymore — drop stale warnings
                StatusMessage = "Preload removed — TF2 restored to clean state.";
            }
            else
            {
                StatusMessage = $"Failed to remove preload: {result.ErrorSummary}";
            }
        }
        finally
        {
            Percent = 0;
            IsBusy = false;
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private bool FilterAddon(object obj)
    {
        if (obj is not PreloaderAddonModel addon) return false;

        var matchesFilter = CurrentFilter switch
        {
            "Active"    => addon.IsEnabled,
            "Inactive"  => !addon.IsEnabled,
            "Particles" => addon.IsParticleMod,
            _ => true, // "All"
        };
        if (!matchesFilter) return false;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            return addon.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || addon.Type.Contains(q, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(InactiveCount));
        OnPropertyChanged(nameof(ParticleCount));
    }

    private void OpenCustomFolder()
    {
        try
        {
            var tfPath = ServiceLocator.Settings.GetSettings()?.SteamPath;
            if (string.IsNullOrWhiteSpace(tfPath))
            {
                StatusMessage = "TF2 path not configured — set it in Settings first.";
                return;
            }

            // Normalize: the stored TF2 path uses forward slashes (Steam
            // registry), which explorer.exe rejects.
            var custom = Path.GetFullPath(Path.Combine(tfPath, "custom"));
            if (!Directory.Exists(custom))
            {
                StatusMessage = "TF2 custom folder not found. Check your path in Settings.";
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{custom}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Logger.LogError("[Mods] Failed to open custom folder", ex);
            StatusMessage = "Could not open the custom folder.";
        }
    }
}
