using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Engine;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Platform;

namespace Eternal.App.ViewModels;

public sealed record ImportResult(IReadOnlyList<string> Imported, IReadOnlyList<string> Problems, bool WasCancelled)
{
    public static ImportResult Cancelled { get; } = new([], [], true);

    public static ImportResult Failed(string problem) => new([], [problem], false);
}

public sealed partial class ModRowViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;

    private bool _syncing;

    public ModRowViewModel(ModItem item, ModsViewModel owner)
    {
        _owner = owner;
        Item = item;
        _syncing = true;
        Enabled = item.Enabled;
        _syncing = false;
        LoadThumbnail();
    }

    public ModItem Item { get; private set; }
    public string Name => Item.Name;
    public string DisplayName => Item.DisplayName;
    // The preloader labels nearly every mod "Unknown"; only say something when there is something to say.
    public string? Badge => Item.Kind == ModKind.Particle ? "Particles"
        : Item.Type.Equals("hud", StringComparison.OrdinalIgnoreCase) ? "HUD"
        : Item.Type is "" or "Unknown" or "Vpk_Container" ? null : Item.Type;
    public string Meta => Item.Kind == ModKind.Particle || Badge is null ? Files : $"{Badge} · {Files}";
    public string Initials => string.Concat(DisplayName.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    // The preloader fills in "Content from folder: x" or "Content extracted from x.vpk" when a mod has no description.
    public string Description => string.IsNullOrWhiteSpace(Item.Description)
        || Item.Description.StartsWith("Content from", StringComparison.Ordinal)
        || Item.Description.StartsWith("Content extracted from", StringComparison.Ordinal)
        ? "" : Item.Description;
    public string Files => Item.Kind == ModKind.Particle ? $"{Item.Particles.Count} particle file{(Item.Particles.Count == 1 ? "" : "s")}" : $"{Item.FileCount} file{(Item.FileCount == 1 ? "" : "s")}";
    public string Rank => Item.Priority >= 0 ? (Item.Priority + 1).ToString() : "";
    public bool HasIssues => Item.Issues.Count > 0;
    public string IssueText => string.Join("\n\n", Item.Issues.Select(i => i.Message));
    public bool HasConflicts => Item.Wins.Count > 0 || Item.Loses.Count > 0;

    public string ConflictSummary
    {
        get
        {
            var parts = new List<string>();
            var won = Item.Wins.Sum(w => w.Files.Count);
            var lost = Item.Loses.Sum(l => l.Files.Count);
            if (won > 0)
                parts.Add($"wins {won} shared file{(won == 1 ? "" : "s")}");
            if (lost > 0)
                parts.Add($"loses {lost} to {string.Join(", ", Item.Loses.Select(l => _owner.DisplayNameOf(l.Winner)).Distinct())}");
            return string.Join(" · ", parts);
        }
    }

    public IEnumerable<string> ConflictLines =>
        Item.Wins.Select(w => $"Overrides {w.Files.Count} from {_owner.DisplayNameOf(w.Loser)}: {Preview(w.Files)}")
            .Concat(Item.Loses.Select(l => $"{_owner.DisplayNameOf(l.Winner)} overrides {l.Files.Count}: {Preview(l.Files)}"));

    private static string Preview(IReadOnlyList<string> files) =>
        string.Join(", ", files.Take(4)) + (files.Count > 4 ? $" and {files.Count - 4} more" : "");

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private Bitmap? _thumbnail;

    public string PictureAction => Thumbnail is null ? "Add a picture" : "Change picture";

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(PictureAction));

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing && value != Item.Enabled)
            _owner.SetEnabled(this, value);
    }

    public void Update(ModItem item)
    {
        var thumbChanged = item.Thumbnail != Item.Thumbnail;
        Item = item;
        _syncing = true;
        Enabled = item.Enabled;
        _syncing = false;
        OnPropertyChanged(string.Empty);
        if (thumbChanged)
            LoadThumbnail();
    }

    private void LoadThumbnail()
    {
        if (Item.Thumbnail is null || !File.Exists(Item.Thumbnail))
        {
            Thumbnail = null;
            return;
        }
        try
        {
            using var stream = File.OpenRead(Item.Thumbnail);
            Thumbnail = Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            Thumbnail = null;
        }
    }
}

public sealed partial class ModsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly IDialogs _dialogs;
    private readonly MainViewModel _main;

    public ModsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _dialogs = main;
        _main = main;
        services.Mods.Changed += () => Dispatcher.UIThread.Post(SyncRows);
        services.Preload.StatusChanged += _ => Dispatcher.UIThread.Post(RefreshStatus);
        services.Runtime.StatusChanged += _ => Dispatcher.UIThread.Post(RefreshEngine);
        RefreshEngine();
        RefreshStatus();
        LoadOptions();
        GameBanana = new GameBananaViewModel(services, this);
    }

    public GameBananaViewModel GameBanana { get; }

    /// <summary>True while the GameBanana browser is showing instead of the library.</summary>
    [ObservableProperty] private bool _browsing;

    public bool ShowingLibrary => !Browsing;

    partial void OnBrowsingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowingLibrary));
        if (value)
            GameBanana.Activate();
    }

    [RelayCommand]
    private void ShowLibrary() => Browsing = false;

    [RelayCommand]
    private void ShowGameBanana() => Browsing = true;

    public void Browse(Eternal.Core.GameBanana.Submission submission)
    {
        Browsing = true;
        GameBanana.Show(submission);
    }

    internal void Notify(string message, bool isError = false) => _main.Notify(message, isError);

    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ObservableCollection<ModRowViewModel> Visible { get; } = [];
    public string[] Filters { get; } = ["All mods", "Enabled", "Disabled", "Particles"];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _filter = "All mods";
    [ObservableProperty] private ModRowViewModel? _selected;
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private bool _engineNeedsSetup;
    [ObservableProperty] private bool _engineChecking;
    [ObservableProperty] private bool _engineBroken;
    [ObservableProperty] private string _engineMessage = "";
    [ObservableProperty] private string _setupLog = "";
    [ObservableProperty] private bool _settingUp;
    [ObservableProperty] private bool _wineMissing;
    [ObservableProperty] private bool _importing;
    [ObservableProperty] private string _importMessage = "";
    [ObservableProperty] private double _importProgress;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusProblem;
    [ObservableProperty] private bool _building;
    [ObservableProperty] private double _buildProgress;
    [ObservableProperty] private IReadOnlyList<string> _warnings = [];
    [ObservableProperty] private bool _disablePaintColors;
    [ObservableProperty] private bool _showConsole = true;
    [ObservableProperty] private bool _fixModelPaths = true;
    [ObservableProperty] private bool _skipQuickPrecache;
    [ObservableProperty] private string _upstreamVersion = "";

    public bool HasWarnings => Warnings.Count > 0;
    public bool IsEmpty => EngineReady && Rows.Count == 0;
    public bool NoMatches => Rows.Count > 0 && Visible.Count == 0;
    public bool HasSelection => Selected is not null;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedChanged(ModRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasWarnings));

    partial void OnDisablePaintColorsChanged(bool value) => SaveOptions();
    partial void OnShowConsoleChanged(bool value) => SaveOptions();
    partial void OnFixModelPathsChanged(bool value) => SaveOptions();
    partial void OnSkipQuickPrecacheChanged(bool value) => SaveOptions();

    private bool _loadingOptions;

    private void LoadOptions()
    {
        _loadingOptions = true;
        var o = _services.Mods.State.Options;
        DisablePaintColors = o.DisablePaintColors;
        ShowConsole = o.ShowConsole;
        FixModelPaths = o.FixModelPaths;
        SkipQuickPrecache = o.SkipQuickPrecache;
        _loadingOptions = false;
    }

    private void SaveOptions()
    {
        if (_loadingOptions)
            return;
        _services.Mods.SetOptions(new ModBuildOptions
        {
            DisablePaintColors = DisablePaintColors,
            ShowConsole = ShowConsole,
            FixModelPaths = FixModelPaths,
            SkipQuickPrecache = SkipQuickPrecache,
        });
    }

    public string DisplayNameOf(string name) => Rows.FirstOrDefault(r => r.Name == name)?.DisplayName ?? name;

    public void EngineStarted() => Dispatcher.UIThread.Post(() => { RefreshEngine(); SyncRows(); });

    private void RefreshEngine()
    {
        var status = _services.Runtime.Status;
        EngineReady = status.IsReady;
        EngineChecking = status.State is EngineState.Checking or EngineState.SettingUp;
        EngineNeedsSetup = status.State is EngineState.NeedsSetup;
        EngineBroken = status.State is EngineState.NoPython or EngineState.Broken or EngineState.Missing;
        EngineMessage = status.Message;
        WineMissing = status.WineAvailable == false;
        UpstreamVersion = status.UpstreamVersion is null ? "" : $"casual-pre-loader {status.UpstreamVersion}";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshStatus()
    {
        var status = _services.Preload.Status;
        Building = status.Phase == PreloadPhase.Building;
        BuildProgress = status.Percent;
        StatusProblem = status.Phase is PreloadPhase.Failed or PreloadPhase.Blocked;
        Warnings = status.Warnings;
        StatusText = status.Phase switch
        {
            PreloadPhase.Building => status.Message,
            PreloadPhase.Pending => "Waiting to apply your changes…",
            PreloadPhase.Failed or PreloadPhase.Blocked => status.Message,
            _ when status.Installed && status.AppliedAt is { } at => $"Applied to the game {MainViewModel.Ago(at)}",
            _ => "The game files are vanilla.",
        };
    }

    private void SyncRows()
    {
        var items = _services.Mods.Items;
        var byName = Rows.ToDictionary(r => (r.Name, r.Item.Kind));
        var next = new List<ModRowViewModel>();
        foreach (var item in items)
        {
            if (byName.TryGetValue((item.Name, item.Kind), out var row))
                row.Update(item);
            else
                row = new ModRowViewModel(item, this);
            next.Add(row);
        }

        var selected = Selected;
        Rows.Clear();
        foreach (var row in next)
            Rows.Add(row);
        ApplyFilter();
        Selected = selected is not null ? Rows.FirstOrDefault(r => r.Name == selected.Name && r.Item.Kind == selected.Item.Kind) : null;

        var enabled = items.Count(i => i.Enabled);
        Summary = items.Count == 0 ? "No mods yet" : $"{enabled} of {items.Count} enabled";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyFilter()
    {
        var query = Search.Trim();
        var keep = Rows.Where(r =>
            (Filter switch
            {
                "Enabled" => r.Enabled,
                "Disabled" => !r.Enabled,
                "Particles" => r.Item.Kind == ModKind.Particle,
                _ => true,
            })
            && (query.Length == 0 || r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Badge?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
        Visible.Clear();
        foreach (var row in keep)
            Visible.Add(row);
        OnPropertyChanged(nameof(NoMatches));
    }

    internal void SetEnabled(ModRowViewModel row, bool enabled) => _services.Mods.SetEnabled(row.Name, row.Item.Kind, enabled);

    [RelayCommand]
    private async Task SetupEngineAsync()
    {
        SettingUp = true;
        SetupLog = "";
        var log = new Progress<string>(line => SetupLog = (SetupLog + line + "\n")[^Math.Min(SetupLog.Length + line.Length + 1, 4000)..]);
        try
        {
            if (await _services.Runtime.SetupAsync(log))
                await _services.StartAsync();
        }
        finally
        {
            SettingUp = false;
            RefreshEngine();
        }
    }

    [RelayCommand]
    private async Task RecheckEngineAsync()
    {
        await _services.StartAsync();
        RefreshEngine();
    }

    [RelayCommand]
    private static void GetPython() => Shell.OpenUrl("https://www.python.org/downloads/");

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var files = await Pickers.OpenFilesAsync("Import mods", true, "Mods", "*.vpk", "*.zip", "*.rar", "*.7z");
        await ImportAsync(files);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await Pickers.OpenFolderAsync("Import a mod folder") is { } folder)
            await ImportAsync([folder]);
    }

    /// <summary>
    /// Imports files, folders and archives one at a time. When one holds several
    /// mods the player picks which (see <see cref="ImportPlan.DefaultChoice"/>).
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !await BeginImportAsync())
            return;
        var imported = new List<string>();
        var problems = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                var result = await ImportOneAsync(path, null, null, CancellationToken.None);
                imported.AddRange(result.Imported);
                problems.AddRange(result.Problems);
            }
        }
        finally
        {
            EndImport();
        }

        if (problems.Count > 0)
        {
            var head = imported.Count > 0 ? $"Imported {string.Join(", ", imported)}. These had problems:" : "Nothing was imported:";
            await _dialogs.AlertAsync("Import finished with problems", head + "\n\n" + string.Join("\n", problems.Select(p => "• " + p)));
        }
        else if (imported.Count > 0)
        {
            _main.Notify($"Imported {string.Join(", ", imported)}. {(imported.Count == 1 ? "It's" : "They're")} switched on, at the top of your list.");
        }
    }

    internal async Task<bool> BeginImportAsync()
    {
        if (Importing)
        {
            _main.Notify("Wait for the current import to finish.");
            return false;
        }
        if (!EngineReady)
        {
            await _dialogs.AlertAsync("Mod engine not ready", EngineMessage);
            return false;
        }
        Importing = true;
        ImportProgress = 0;
        ImportMessage = "Preparing…";
        return true;
    }

    internal void EndImport() => Importing = false;

    /// <summary>
    /// One file or folder, between <see cref="BeginImportAsync"/> and <see cref="EndImport"/>.
    /// <paramref name="modName"/> names the mod when the download holds just one.
    /// </summary>
    internal async Task<ImportResult> ImportOneAsync(string path, string? source, string? modName, CancellationToken cancellation)
    {
        var name = Path.GetFileName(path);
        var progress = new Progress<ImportProgress>(p =>
        {
            ImportMessage = p.Message;
            ImportProgress = p.Fraction * 100;
        });
        try
        {
            using var plan = await _services.Mods.PrepareAsync(path, progress, cancellation);
            if (plan.Parts.Count == 0)
                return ImportResult.Failed(NothingToImport(name, plan.Inspection));

            IReadOnlyList<string>? parts = null;
            if (plan.Parts.Count > 1)
            {
                parts = await _main.ChoosePartsAsync(plan);
                if (parts is null)
                    return ImportResult.Cancelled;
            }

            if (modName is not null && source is not null)
                modName = _services.Mods.FreeName(modName, source[..(source.LastIndexOf(':') + 1)]);
            var outcome = await _services.Mods.ImportAsync(plan, parts, progress, modName, cancellation);
            var names = outcome.Items.SelectMany(i => i.Mods).Select(m => m.Name).Distinct().ToList();
            if (source is not null && names.Count > 0)
                _services.Mods.SetSource(names, source);
            var problems = outcome.Failures.ToList();
            if (names.Count > 0 && plan.Inspection.Skipped.Count > 0)
                problems.Add($"Left out of {name}: {Preview(plan.Inspection.Skipped)}. The game has no file with those names, so there's nowhere to put them.");
            return new ImportResult(names, problems, false);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Import of {name} failed: {ex.Message}");
            return ImportResult.Failed($"{name}: {ex.Message}");
        }
    }

    private static string NothingToImport(string name, Inspection inspection)
    {
        if (inspection.Skipped.Count > 0)
            return $"{name} has files like {Preview(inspection.Skipped)}, but nothing says where in the game they go.";
        if (inspection.Documents.Count > 0)
            return $"{name} only holds {Preview(inspection.Documents)}. There are no game files in it.";
        return $"{name} has no game files in it.";
    }

    private static string Preview(IReadOnlyList<string> paths) =>
        string.Join(", ", paths.Take(3).Select(Path.GetFileName)) + (paths.Count > 3 ? $" and {paths.Count - 3} more" : "");

    /// <summary>Sources (see <see cref="ModEntry.Source"/>) of everything in the library.</summary>
    public IReadOnlySet<string> InstalledSources() =>
        _services.Mods.State.Order.Select(e => e.Source).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public void MoveTo(ModRowViewModel row, int index) => _services.Mods.Move(row.Name, row.Item.Kind, index);

    private int IndexOf(ModRowViewModel row) => _services.Mods.State.Order.FindIndex(e => e.Name == row.Name && e.Kind == row.Item.Kind);

    [RelayCommand]
    private void MoveUp(ModRowViewModel? row)
    {
        if ((row ?? Selected) is { } r)
            MoveTo(r, Math.Max(0, IndexOf(r) - 1));
    }

    [RelayCommand]
    private void MoveDown(ModRowViewModel? row)
    {
        if ((row ?? Selected) is { } r)
            MoveTo(r, IndexOf(r) + 1);
    }

    [RelayCommand]
    private void MoveTop(ModRowViewModel? row)
    {
        if ((row ?? Selected) is { } r)
            MoveTo(r, 0);
    }

    [RelayCommand]
    private void MoveBottom(ModRowViewModel? row)
    {
        if ((row ?? Selected) is { } r)
            MoveTo(r, int.MaxValue);
    }

    [RelayCommand]
    private async Task RemoveAsync(ModRowViewModel? row)
    {
        if ((row ?? Selected) is not { } r)
            return;
        if (!await _dialogs.ConfirmAsync("Remove mod", $"Delete {r.DisplayName} from your library? The original file you imported is not touched.", "Remove", danger: true))
            return;
        var result = await _services.Mods.RemoveAsync([r.Item]);
        if (!result.Success)
            await _dialogs.AlertAsync("Couldn't remove", result.ErrorSummary);
    }

    [RelayCommand]
    private async Task ChangeThumbnailAsync(ModRowViewModel? row)
    {
        if ((row ?? Selected) is not { } r)
            return;
        var files = await Pickers.OpenFilesAsync($"Picture for {r.DisplayName}", false, "Images", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp");
        if (files.Count == 0)
            return;
        var png = await Task.Run(() => ThumbnailMaker.FromFile(files[0]));
        if (png is null)
        {
            await _dialogs.AlertAsync("Unsupported picture", "That file couldn't be read as an image.");
            return;
        }
        _services.Mods.Thumbnails.Save(r.Name, png);
        _services.Mods.ThumbnailChanged();
    }

    [RelayCommand]
    private void RemoveThumbnail(ModRowViewModel? row)
    {
        if ((row ?? Selected) is not { } r)
            return;
        _services.Mods.Thumbnails.Delete(r.Name);
        _services.Mods.ThumbnailChanged();
    }

    [RelayCommand]
    private void EnableAll() => _services.Mods.SetAllEnabled(true);

    [RelayCommand]
    private async Task RestoreVanillaAsync()
    {
        if (!await _dialogs.ConfirmAsync("Restore vanilla",
                "Turn every mod off and remove the preload from the game folder? Your library stays as it is, so you can switch mods back on later.",
                "Restore vanilla"))
            return;
        _services.Mods.SetAllEnabled(false);
        _services.Preload.BuildNow();
    }

    [RelayCommand]
    private void SyncNow() => _services.Preload.BuildNow();

    [RelayCommand]
    private void OpenLibrary() => Shell.OpenFolder(Path.Combine(_services.Paths.EngineDataDir, "mods"));

    [RelayCommand]
    private void OpenCustomFolder()
    {
        if (_services.TfPath is { } tf)
            Shell.OpenFolder(Path.Combine(tf, "custom"));
    }

    [RelayCommand]
    private void OpenEngineLog() => Shell.OpenFolder(_services.Paths.LogsDir);
}
