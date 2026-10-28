using Eternal.Core;
using Eternal.Core.Engine;
using Eternal.Core.Inventory;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Profiles;
using Eternal.Core.Settings;
using Eternal.Core.Steam;

namespace Eternal.App.Services;

/// <summary>
/// Composition root. Built once, handed to the view models explicitly.
/// Construction only touches local files and never blocks on the network or
/// on Python; slow checks run in <see cref="StartAsync"/> after the window is up.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly List<string> _notices = [];

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Launcher = new LauncherSettingsStore(paths);
        if (!SteamLocator.IsTf2Folder(Launcher.Settings.TfPath) && SteamLocator.FindTf2() is { } detected)
            Launcher.Update(s => s.TfPath = detected);

        Game = new GameConfigStore(paths.GameConfigFile, () => TfPath);
        Profiles = new ProfileStore(paths.ProfilesDir);
        Profiles.Load();
        FirstRunImport();
        RefreshObserved();

        Runtime = new EngineRuntime(paths, () => Launcher.Settings.PythonOverride);
        EngineClient = new EngineClient(Runtime);
        Engine = new PreloaderEngine(EngineClient);
        Mods = new ModLibrary(Engine, paths, new ThumbnailStore(paths.ThumbnailsDir));
        Preload = new PreloadCoordinator(new EnginePreloadBuilder(Engine), Mods.Desired, () => TfPath, GameProcess.IsRunning, paths.PreloadStateFile);
        Mods.DesiredChanged += Preload.RequestBuild;

        Inventory = new InventoryService(Path.Combine(paths.CacheDir, "inventory"));
        Prices = new PriceService(Path.Combine(paths.CacheDir, "pricing"));
        Images = new ImageCache(paths.ImageCacheDir);

        foreach (var (store, name) in new (LoadOutcome Outcome, string Name)[] { (Game.LoadOutcome, "TF2 settings"), (Launcher.LoadOutcome, "Launcher settings") })
        {
            if (store.State == LoadState.RestoredFromBackup)
                _notices.Add($"{name} were damaged and the previous version was restored.");
            else if (store.State == LoadState.Reset)
                _notices.Add($"{name} were damaged and reset to defaults. The old file was kept next to the new one.");
            else if (store.State == LoadState.Unreadable)
                _notices.Add($"{name} couldn't be read, so changes won't be saved until the launcher restarts.");
        }
    }

    public AppPaths Paths { get; }
    public LauncherSettingsStore Launcher { get; }
    public GameConfigStore Game { get; }
    public ProfileStore Profiles { get; }
    public EngineRuntime Runtime { get; }
    public EngineClient EngineClient { get; }
    public PreloaderEngine Engine { get; }
    public ModLibrary Mods { get; }
    public PreloadCoordinator Preload { get; }
    public InventoryService Inventory { get; }
    public PriceService Prices { get; }
    public ImageCache Images { get; }

    public IReadOnlyList<string> Notices => _notices;
    public string? TfPath => Launcher.Settings.TfPath;

    public event Action<string>? Notice;

    private void FirstRunImport()
    {
        if (!Game.IsNew)
            return;
        var legacy = LegacyImporter.Run(Paths, Game, Launcher.Settings, Profiles);
        _notices.AddRange(legacy.Notes);
        if (!legacy.Imported && TfPath is not null
            && AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(TfPath)) is { } autoexec)
        {
            var values = ObservedSettings.ImportFromAutoexec(autoexec);
            if (values.Count > 0)
            {
                Game.Settings.Apply(values.ToDictionary(kv => kv.Key, kv => (System.Text.Json.Nodes.JsonNode?)kv.Value));
                _notices.Add($"Read {values.Count} settings from your existing autoexec.cfg.");
            }
        }
        Launcher.Save();
    }

    public void RefreshObserved() => Game.Settings.UpdateObserved(ObservedSettings.Read(TfPath));

    /// <summary>
    /// Engine check, crash recovery, library load. Each step fails on its own
    /// so one problem never leaves the Mods tab half started. Safe to call again.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            var status = await Runtime.CheckAsync(EngineClient, TfPath).ConfigureAwait(false);
            if (!status.IsReady)
                return;

            await Step("Recovering an interrupted build", async () =>
            {
                if (status.Interrupted)
                {
                    Log.Warn("A previous mod build was interrupted; restoring the game files.");
                    var recovered = await Engine.RecoverAsync(null).ConfigureAwait(false);
                    Preload.Invalidate();
                    Notice?.Invoke(recovered.Success
                        ? "The last mod build was interrupted, so the game files were restored and your mods will be applied again."
                        : $"The last mod build was interrupted and restoring the game files failed: {recovered.ErrorSummary}");
                }
                else if (status.PreloadPresent != Preload.Record.Installed)
                {
                    // Steam verified the files, or another tool changed them.
                    Preload.Invalidate();
                }
            }).ConfigureAwait(false);

            await Step("Importing the 1.x mod library", MigrateLegacyLibraryAsync).ConfigureAwait(false);
            await Step("Loading the mod library", async () =>
            {
                if (!await Mods.RefreshAsync().ConfigureAwait(false))
                    Notice?.Invoke("The mod library couldn't be read. Open the Mods tab and press Check again.");
            }).ConfigureAwait(false);
        }
        finally
        {
            Preload.Start();
        }
    }

    private async Task Step(string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
            Notice?.Invoke($"{what} failed: {ex.Message}");
        }
    }

    private async Task MigrateLegacyLibraryAsync()
    {
        if (Launcher.Settings.LegacyLibraryChecked)
            return;
        Launcher.Update(s => s.LegacyLibraryChecked = true);

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "preloader", "userdata") };
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, "Programs", "Eternal TF2 Launcher", "preloader", "userdata"));
        }
        var source = candidates.FirstOrDefault(dir => Directory.Exists(Path.Combine(dir, "data", "mods")));
        if (source is null)
            return;

        var (result, enabled) = await Engine.MigrateUserdataAsync(source).ConfigureAwait(false);
        if (!result.Success)
        {
            Log.Warn($"Importing the 1.x mod library failed: {result.ErrorSummary}");
            return;
        }
        await Mods.RefreshAsync().ConfigureAwait(false);
        Mods.AdoptLegacyOrder(enabled);
        Notice?.Invoke($"Imported your mod library from Eternal Launcher 1.x ({enabled.Count} enabled).");
    }

    public async ValueTask DisposeAsync()
    {
        await Game.FlushAsync().ConfigureAwait(false);
        Prices.Save();
        Launcher.Dispose();
        Game.Dispose();
        await Preload.DisposeAsync().ConfigureAwait(false);
    }
}
