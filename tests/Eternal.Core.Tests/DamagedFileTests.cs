using Eternal.Core.Inventory;
using Eternal.Core.Mods;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Servers;
using Eternal.Core.Steam;

namespace Eternal.Core.Tests;

// System.Text.Json refuses a null list but not a null inside one.
public sealed class DamagedFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-damaged-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Launcher_settings_with_an_empty_tab_entry_fall_back_to_the_backup()
    {
        var paths = new AppPaths(_dir, _dir, _dir);
        File.WriteAllText(paths.LauncherSettingsFile + ".bak", """{"themeId": "blu"}""");
        File.WriteAllText(paths.LauncherSettingsFile, """{"themeId": "red", "tabs": [null]}""");

        using var store = new LauncherSettingsStore(paths);

        Assert.Equal(LoadState.RestoredFromBackup, store.LoadOutcome.State);
        Assert.Equal("blu", store.Settings.ThemeId);
        Assert.Equal(LauncherSettings.DefaultTabOrder, store.Settings.NormalizedTabs().Select(t => t.Id));
    }

    [Fact]
    public void Nonsense_background_settings_are_clamped_on_load()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Eternal.Core.LauncherSettings>(
            """{"Background": 99, "BackgroundRotateMinutes": 99999, "BackgroundScreenshot": "x"}""")!;

        Assert.Equal(Eternal.Core.BackgroundMode.Screenshots, settings.Background);
        Assert.Equal(120, settings.BackgroundRotateMinutes);
        Assert.Equal("x", settings.BackgroundScreenshot);
    }

    [Fact]
    public void Look_settings_outside_what_the_sliders_allow_are_clamped_on_load()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Eternal.Core.LauncherSettings>(
            """{"BackgroundBlur": 1e9, "BackgroundDim": 7, "UiScale": 0, "Density": 42}""")!;

        Assert.Equal(40, settings.BackgroundBlur);
        Assert.Equal(0.95, settings.BackgroundDim);
        Assert.Equal(0.8, settings.UiScale);
        Assert.Equal(Eternal.Core.UiDensity.Comfortable, settings.Density);
    }

    [Theory]
    [InlineData("""{"order": [null]}""")]
    [InlineData("""{"order": [{"name": "Hitsound", "kind": "Addon", "enabled": true}, null]}""")]
    public void Mod_state_with_an_empty_entry_is_damaged(string json)
    {
        var file = Path.Combine(_dir, "mods.json");
        File.WriteAllText(file, json);

        var state = JsonFile.Load(file, ModJson.Default.ModState, () => new ModState(), out var outcome);

        Assert.Equal(LoadState.Reset, outcome.State);
        Assert.Empty(state.Order);
    }

    [Fact]
    public void A_preload_record_with_an_empty_mod_name_is_damaged()
    {
        var file = Path.Combine(_dir, "preload.json");
        File.WriteAllText(file, """{"installed": true, "addons": [null]}""");

        JsonFile.Load(file, ModJson.Default.PreloadRecord, () => new PreloadRecord(), out var outcome);

        Assert.Equal(LoadState.Reset, outcome.State);
    }

    [Theory]
    [InlineData("""{"steamId": "1", "items": [null]}""")]
    [InlineData("""{"steamId": "1", "items": [{"name": "Rocket Launcher", "classes": [null]}]}""")]
    public void A_backpack_cache_with_empty_entries_is_damaged(string json)
    {
        var file = Path.Combine(_dir, "backpack.json");
        File.WriteAllText(file, json);

        JsonFile.Load(file, InventoryJson.Default.InventoryCacheFile, () => new InventoryCacheFile(), out var outcome);

        Assert.Equal(LoadState.Reset, outcome.State);
    }

    [Theory]
    [InlineData("""{"favorites": null}""")]
    [InlineData("""{"favorites": [null]}""")]
    [InlineData("""{"recent": [null]}""")]
    public void A_server_list_with_an_empty_entry_falls_back_to_the_backup(string json)
    {
        var file = Path.Combine(_dir, "servers.json");
        File.WriteAllText(file + ".bak", """{"favorites": [{"address": "1.2.3.4:27015", "name": "Kept", "at": 1}]}""");
        File.WriteAllText(file, json);
        var queue = new SteamWriteQueue(Path.Combine(_dir, "writes.json"), Path.Combine(_dir, "backups"), () => true);

        var book = new ServerBook(file, queue, () => null);

        Assert.Equal(LoadState.RestoredFromBackup, book.LoadOutcome.State);
        Assert.Equal("Kept", Assert.Single(book.Favorites()).Name);
    }

    [Theory]
    [InlineData("""{"writes": null}""")]
    [InlineData("""{"writes": [null]}""")]
    public void A_steam_write_queue_with_an_empty_entry_starts_empty(string json)
    {
        var file = Path.Combine(_dir, "writes.json");
        File.WriteAllText(file, json);

        Assert.Equal(0, new SteamWriteQueue(file, Path.Combine(_dir, "backups"), () => false).Count);
    }

    [Fact]
    public void A_price_cache_with_an_empty_price_is_damaged()
    {
        var file = Path.Combine(_dir, "prices.json");
        File.WriteAllText(file, """{"prices": {"5021;6": null}}""");

        JsonFile.Load(file, InventoryJson.Default.PriceCacheFile, () => new PriceCacheFile(), out var outcome);

        Assert.Equal(LoadState.Reset, outcome.State);
    }
}
