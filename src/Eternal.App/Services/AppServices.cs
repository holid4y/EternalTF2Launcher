using Eternal.Core;
using Eternal.Core.Engine;
using Eternal.Core.Inventory;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Profiles;
using Eternal.Core.Servers;
using Eternal.Core.Settings;
using Eternal.Core.Sounds;
using Eternal.Core.Steam;

namespace Eternal.App.Services;

public sealed class AppServices : IAsyncDisposable
{
    private readonly List<string> _notices = [];
    private readonly Func<string?> _findSteamUser;
    private string? _userDir;
    private DateTime _userDirAt = DateTime.MinValue;

    public AppServices(AppPaths paths, Func<string?>? steamUser = null)
    {
        Paths = paths;
        _findSteamUser = steamUser ?? (() => SteamLocator.UserDataDir());
        ArchiveIngest.TryDelete(paths.ImportStagingDir);
        Launcher = new LauncherSettingsStore(paths);
        if (!SteamLocator.IsTf2Folder(Launcher.Settings.TfPath) && SteamLocator.FindTf2() is { } detected)
            Launcher.Update(s => s.TfPath = detected);

        Game = new GameConfigStore(paths.GameConfigFile, () => TfPath, () => Launcher.Settings.ManageAutoexec && !GameFolderReviewPending);
        if (!Game.IsNew)
            Launcher.Settings.ReviewedGameFolder = true;
        Profiles = new ProfileStore(paths.ProfilesDir);
        Profiles.Load();
        FirstRunImport();
        RefreshObserved();

        Runtime = new EngineRuntime(paths, () => Launcher.Settings.PythonOverride);
        EngineClient = new EngineClient(Runtime);
        Engine = new PreloaderEngine(EngineClient);
        Mods = new ModLibrary(Engine, paths, new ThumbnailStore(paths.ThumbnailsDir), () => TfPath);
        CustomSounds = new CustomSoundStore(paths);
        Preload = new PreloadCoordinator(new EnginePreloadBuilder(Engine), Mods.Desired, () => TfPath, GameProcess.IsRunning, paths.PreloadStateFile);
        Mods.DesiredChanged += Preload.RequestBuild;
        Preload.Recovered += result => Notice?.Invoke(result.Success
            ? "The last mod build was interrupted, so the game files were restored and your mods will be applied again."
            : $"The last mod build was interrupted and restoring the game files failed: {result.ErrorSummary}");

        Inventory = new InventoryService(Path.Combine(paths.CacheDir, "inventory"));
        Prices = new PriceService(Path.Combine(paths.CacheDir, "pricing"));
        Images = new ImageCache(paths.ImageCacheDir);
        Pictures = new SourcePictures(Mods, Images, Eternal.Core.GameBanana.GameBananaClient.Shared);
        Mods.Changed += Pictures.Refresh;

        SteamWrites = new SteamWriteQueue(paths.SteamWritesFile, paths.SteamBackupsDir);
        Servers = new ServerBook(paths.ServersFile, SteamWrites, () => SteamUserDir);

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
    public CustomSoundStore CustomSounds { get; }
    public PreloadCoordinator Preload { get; }
    public InventoryService Inventory { get; }
    public PriceService Prices { get; }
    public ImageCache Images { get; }
    public SourcePictures Pictures { get; }
    public SteamWriteQueue SteamWrites { get; }
    public ServerBook Servers { get; }

    public string? SteamUserDir
    {
        get
        {
            if (DateTime.UtcNow - _userDirAt > TimeSpan.FromSeconds(30))
            {
                _userDir = _findSteamUser();
                _userDirAt = DateTime.UtcNow;
            }
            return _userDir;
        }
    }

    public IReadOnlyList<string> Notices => _notices;
    public string? TfPath => Launcher.Settings.TfPath;

    /// <summary>Until the player has seen what is already in the game folder, nothing gets written there.</summary>
    public bool GameFolderReviewPending => !Launcher.Settings.ReviewedGameFolder && SteamLocator.IsTf2Folder(TfPath);

    public event Action<string>? Notice;

    private void FirstRunImport()
    {
        if (!Game.IsNew)
            return;
        var legacy = LegacyImporter.Run(Paths, Game, Launcher.Settings, Profiles);
        _notices.AddRange(legacy.Notes);
        if (legacy.Imported)
            Launcher.Settings.ReviewedGameFolder = true;
        Launcher.Save();
    }

    public int ReadAutoexecIntoConfig()
    {
        if (TfPath is null || AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(TfPath)) is not { } autoexec)
            return 0;
        var values = ObservedSettings.ImportFromAutoexec(autoexec);
        Game.Settings.Apply(values.ToDictionary(kv => kv.Key, kv => (System.Text.Json.Nodes.JsonNode?)kv.Value));
        return values.Count;
    }

    private string AddonsDir => Path.Combine(Paths.EngineDataDir, "mods", "addons");
    private string CustomSoundsFolder => Path.Combine(AddonsDir, CustomSoundMod.Folder);

    public async Task RebuildCustomSoundsAsync()
    {
        var staging = Path.Combine(Paths.EngineDataDir, "mods", ".custom-sounds-staging");
        await Preload.WithLibraryHeldAsync(() => Task.Run(() => CustomSounds.WriteMod(AddonsDir, staging))).ConfigureAwait(false);
        await Mods.SyncManagedAsync(CustomSounds.Enabled).ConfigureAwait(false);
    }

    public void RefreshObserved() => Game.Settings.UpdateObserved(ObservedSettings.Read(TfPath));

    public async Task StartAsync()
    {
        try
        {
            var status = await Runtime.CheckAsync(EngineClient, TfPath).ConfigureAwait(false);
            if (!status.IsReady)
                return;

            if (status.Interrupted)
            {
                Log.Warn("A previous mod build was interrupted; the game files will be restored.");
                Preload.RecoverInterrupted();
            }
            else if (status.PreloadPresent != Preload.Record.Installed)
            {
                Preload.Invalidate();
            }

            await Step("Importing the 1.x mod library", MigrateLegacyLibraryAsync).ConfigureAwait(false);
            await Step("Loading the mod library", async () =>
            {
                if (!await Mods.RefreshAsync().ConfigureAwait(false))
                    Notice?.Invoke("The mod library couldn't be read. Open the Mods tab and press Check again.");
                if (CustomSounds.Sounds.Count > 0 && !Directory.Exists(CustomSoundsFolder))
                    await RebuildCustomSoundsAsync().ConfigureAwait(false);
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
