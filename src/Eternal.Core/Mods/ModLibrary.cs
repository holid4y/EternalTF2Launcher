using System.Text;
using Eternal.Core.Engine;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;

namespace Eternal.Core.Mods;

public sealed record ModItem(
    string Name,
    string DisplayName,
    ModKind Kind,
    string Type,
    string Description,
    int FileCount,
    bool Enabled,
    int Priority,
    IReadOnlyList<string> Particles,
    IReadOnlyList<ModOverlap> Wins,
    IReadOnlyList<ModOverlap> Loses,
    IReadOnlyList<CompatIssue> Issues,
    string? Thumbnail,
    string? Source);

public sealed record ImportProgress(string Message, double Fraction);

public sealed record ImportOutcome(IReadOnlyList<ImportItemResult> Items, IReadOnlyList<string> Failures)
{
    public int Imported => Items.Sum(i => i.Mods.Count(m => m.IsNew)) + Items.Sum(i => i.Mods.Count(m => !m.IsNew));
    public bool AnyChanged => Items.Any(i => i.Mods.Count > 0);
}

/// <summary>
/// The player's mod list: the engine's library merged with their choices
/// (enabled, priority). Every change that affects the game raises
/// <see cref="DesiredChanged"/> so the coordinator can rebuild.
/// </summary>
public sealed class ModLibrary
{
    private readonly PreloaderEngine _engine;
    private readonly AppPaths _paths;
    private readonly ThumbnailStore _thumbnails;
    private readonly object _gate = new();
    private IReadOnlyList<LibraryMod> _library = [];
    private IReadOnlyList<ModOverlap> _overlaps = [];
    private IReadOnlyList<CompatIssue> _issues = [];
    private CancellationTokenSource? _analysis;

    public ModLibrary(PreloaderEngine engine, AppPaths paths, ThumbnailStore thumbnails)
    {
        _engine = engine;
        _paths = paths;
        _thumbnails = thumbnails;
        State = JsonFile.Load(paths.ModStateFile, ModJson.Default.ModState, () => new ModState(), out _);
    }

    public ModState State { get; }
    public bool Loaded { get; private set; }
    public IReadOnlyList<ModItem> Items { get; private set; } = [];

    /// <summary>Something visible changed (list, flags, conflicts).</summary>
    public event Action? Changed;

    /// <summary>Something that affects the game files changed.</summary>
    public event Action? DesiredChanged;

    public ThumbnailStore Thumbnails => _thumbnails;

    public DesiredPreload? Desired()
    {
        if (!Loaded)
            return null;
        lock (_gate)
        {
            var enabled = State.Order.Where(e => e.Enabled && _library.Any(l => l.Name == e.Name && l.Kind == e.Kind)).ToList();
            return new DesiredPreload(
                enabled.Where(e => e.Kind == ModKind.Addon).Select(e => e.Name).ToArray(),
                enabled.Where(e => e.Kind == ModKind.Particle).Select(e => e.Name).ToArray(),
                State.Options,
                Signature(enabled));
        }
    }

    private string Signature(IEnumerable<ModEntry> enabled)
    {
        var sb = new StringBuilder();
        foreach (var entry in enabled)
        {
            var mod = _library.FirstOrDefault(l => l.Name == entry.Name && l.Kind == entry.Kind);
            sb.Append(entry.Name).Append('|').Append(mod?.FileCount).Append('|').Append(mod?.Modified).Append('\n');
        }
        return sb.ToString();
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellation = default)
    {
        var library = await _engine.LibraryAsync(cancellation).ConfigureAwait(false);
        if (library is null)
            return false;

        lock (_gate)
        {
            _library = library;
            // Mods that showed up without going through the launcher start disabled at the bottom.
            foreach (var mod in library.Where(m => !State.Order.Any(e => e.Name == m.Name && e.Kind == m.Kind)))
                State.Order.Add(new ModEntry { Name = mod.Name, Kind = mod.Kind, Enabled = false });
            State.Order.RemoveAll(e => !library.Any(m => m.Name == e.Name && m.Kind == e.Kind));
        }
        Loaded = true;
        SaveState();
        Rebuild();
        Log.Info($"Mod library: {Items.Count} mods, {Items.Count(i => i.Enabled)} enabled");
        DesiredChanged?.Invoke();
        ScheduleAnalysis();
        return true;
    }

    /// <summary>
    /// Imports files, folders and archives. Archives are unpacked off the UI
    /// thread; mods that imported cleanly are kept and enabled even when other
    /// items in the same batch fail.
    /// </summary>
    public async Task<ImportOutcome> ImportAsync(IReadOnlyList<string> paths, IProgress<ImportProgress>? progress, CancellationToken cancellation = default)
    {
        var failures = new List<string>();
        var direct = new List<string>();
        var extracted = new List<string>();
        var staging = new List<string>();

        try
        {
            var archives = paths.Where(ArchiveIngest.IsArchive).ToList();
            for (var i = 0; i < archives.Count; i++)
            {
                var archive = archives[i];
                progress?.Report(new ImportProgress($"Unpacking {Path.GetFileName(archive)}…", (double)i / Math.Max(1, archives.Count) * 0.4));
                try
                {
                    var folder = await Task.Run(() => ArchiveIngest.Extract(archive, _paths.ImportStagingDir, null, cancellation), cancellation).ConfigureAwait(false);
                    extracted.Add(folder);
                    staging.Add(Path.GetDirectoryName(folder)!);
                }
                catch (ArchiveException ex)
                {
                    failures.Add(ex.Message);
                }
            }
            direct.AddRange(paths.Where(p => !ArchiveIngest.IsArchive(p)));

            if (direct.Count == 0 && extracted.Count == 0)
                return new ImportOutcome([], failures);

            var engineProgress = new InlineProgress<EngineProgress>(p => progress?.Report(new ImportProgress(p.Message, 0.4 + p.Percent / 100.0 * 0.6)));
            var (result, items) = await _engine.ImportAsync(direct, extracted, engineProgress, cancellation).ConfigureAwait(false);
            failures.AddRange(items.Where(i => !i.Ok).Select(i => $"{i.Source}: {i.Message}"));
            if (items.Count == 0 && !result.Success)
                failures.Add(result.ErrorSummary);

            var imported = items.SelectMany(i => i.Mods).ToList();
            await RefreshAsync(cancellation).ConfigureAwait(false);
            if (imported.Count > 0)
            {
                lock (_gate)
                {
                    // Fresh imports go to the top (highest priority) and are switched on.
                    foreach (var mod in imported.Where(m => m.IsNew).Reverse())
                    {
                        State.Order.RemoveAll(e => e.Name == mod.Name && e.Kind == mod.Kind);
                        State.Order.Insert(0, new ModEntry { Name = mod.Name, Kind = mod.Kind, Enabled = true });
                    }
                }
                SaveState();
                Rebuild();
                DesiredChanged?.Invoke();
                ScheduleAnalysis();
            }
            return new ImportOutcome(items, failures);
        }
        finally
        {
            foreach (var dir in staging)
                ArchiveIngest.TryDelete(dir);
        }
    }

    public void SetEnabled(string name, ModKind kind, bool enabled)
    {
        lock (_gate)
        {
            var entry = State.Order.FirstOrDefault(e => e.Name == name && e.Kind == kind);
            if (entry is null || entry.Enabled == enabled)
                return;
            entry.Enabled = enabled;
        }
        Commit();
    }

    /// <summary>Moves a mod to <paramref name="index"/> in priority order (0 = wins every conflict).</summary>
    public void Move(string name, ModKind kind, int index)
    {
        lock (_gate)
        {
            var entry = State.Order.FirstOrDefault(e => e.Name == name && e.Kind == kind);
            if (entry is null)
                return;
            State.Order.Remove(entry);
            State.Order.Insert(Math.Clamp(index, 0, State.Order.Count), entry);
        }
        Commit();
    }

    public void SetAllEnabled(bool enabled)
    {
        lock (_gate)
        {
            foreach (var entry in State.Order)
                entry.Enabled = enabled;
        }
        Commit();
    }

    /// <summary>
    /// Applies a profile's mod set: listed mods on in the listed order, everything
    /// else off. Returns the listed mods that are not in the library.
    /// </summary>
    public IReadOnlyList<string> ApplySelection(IReadOnlyList<string> addons, IReadOnlyList<string> particles)
    {
        List<string> missing;
        lock (_gate)
        {
            var wanted = addons.Select(a => (Name: a, Kind: ModKind.Addon)).Concat(particles.Select(p => (Name: p, Kind: ModKind.Particle))).ToList();
            foreach (var entry in State.Order)
                entry.Enabled = wanted.Contains((entry.Name, entry.Kind));
            var found = wanted.Select(w => (w.Name, Entry: State.Order.FirstOrDefault(e => e.Name == w.Name && e.Kind == w.Kind))).ToList();
            missing = found.Where(f => f.Entry is null).Select(f => f.Name).ToList();
            var ordered = found.Select(f => f.Entry).OfType<ModEntry>().ToList();
            State.Order.RemoveAll(ordered.Contains);
            State.Order.InsertRange(0, ordered);
        }
        Commit();
        return missing;
    }

    public void SetOptions(ModBuildOptions options)
    {
        lock (_gate)
            State.Options = options;
        Commit();
    }

    public async Task<EngineResult> RemoveAsync(IReadOnlyList<ModItem> mods)
    {
        var result = await _engine.RemoveAsync(mods.Select(m => m.Name).ToArray()).ConfigureAwait(false);
        foreach (var mod in mods)
            _thumbnails.Delete(mod.Name);
        await RefreshAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Records the enabled list from a migrated 1.x library.</summary>
    public void AdoptLegacyOrder(IReadOnlyList<string> enabledAddons)
    {
        lock (_gate)
        {
            for (var i = enabledAddons.Count - 1; i >= 0; i--)
            {
                var entry = State.Order.FirstOrDefault(e => e.Name == enabledAddons[i] && e.Kind == ModKind.Addon);
                if (entry is null)
                    continue;
                entry.Enabled = true;
                State.Order.Remove(entry);
                State.Order.Insert(0, entry);
            }
        }
        Commit();
    }

    public void ThumbnailChanged() => Rebuild();

    /// <summary>Remembers where mods came from (e.g. a GameBanana file) for update checks.</summary>
    public void SetSource(IEnumerable<string> names, string source)
    {
        var set = names.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var entry in State.Order.Where(e => set.Contains(e.Name)))
                entry.Source = source;
        }
        SaveState();
        Rebuild();
    }

    private void Commit()
    {
        SaveState();
        Rebuild();
        DesiredChanged?.Invoke();
        ScheduleAnalysis();
    }

    private void SaveState()
    {
        try
        {
            lock (_gate)
                JsonFile.Save(_paths.ModStateFile, State, ModJson.Default.ModState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the mod list: {ex.Message}");
        }
    }

    private void Rebuild()
    {
        lock (_gate)
        {
            var items = new List<ModItem>();
            var priority = 0;
            foreach (var entry in State.Order)
            {
                var mod = _library.FirstOrDefault(l => l.Name == entry.Name && l.Kind == entry.Kind);
                if (mod is null)
                    continue;
                items.Add(new ModItem(
                    mod.Name, mod.DisplayName, mod.Kind, mod.Type, mod.Description, mod.FileCount, entry.Enabled,
                    entry.Enabled ? priority++ : -1,
                    mod.Particles,
                    entry.Enabled ? _overlaps.Where(o => o.Winner == mod.Name).ToArray() : [],
                    entry.Enabled ? _overlaps.Where(o => o.Loser == mod.Name).ToArray() : [],
                    _issues.Where(i => i.Addon == mod.Name).ToArray(),
                    _thumbnails.Find(mod.Name),
                    entry.Source));
            }
            Items = items;
        }
        Changed?.Invoke();
    }

    /// <summary>Conflicts and model checks run after edits settle and never block the list.</summary>
    private void ScheduleAnalysis()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _analysis?.Cancel();
            _analysis = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, cts.Token).ConfigureAwait(false);
                var desired = Desired();
                if (desired is null)
                    return;
                var (addonOverlaps, particleOverlaps) = await _engine.ConflictsAsync(desired.Addons, desired.Particles, cts.Token).ConfigureAwait(false);
                var issues = await _engine.CompatAsync(desired.Addons, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                    return;
                lock (_gate)
                {
                    _overlaps = [.. addonOverlaps, .. particleOverlaps];
                    _issues = issues;
                }
                Rebuild();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Mod analysis failed: {ex.Message}");
            }
        });
    }
}
