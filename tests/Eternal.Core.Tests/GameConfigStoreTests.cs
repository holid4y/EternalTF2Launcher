using System.Reflection;
using Eternal.Core.Persistence;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public sealed class GameConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-store-").FullName;
    private readonly string _tf;

    public GameConfigStoreTests()
    {
        _tf = Path.Combine(_dir, "tf");
        Directory.CreateDirectory(Path.Combine(_tf, "cfg"));
        File.WriteAllText(Path.Combine(_tf, "gameinfo.txt"), "\"GameInfo\" {}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int Subscribers(GameSettings settings) =>
        (typeof(GameSettings).GetField(nameof(GameSettings.Changed), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(settings) as Delegate)?.GetInvocationList().Length ?? 0;

    [Fact]
    public async Task Hundreds_of_saves_leave_the_subscriber_count_alone()
    {
        // 1.x built 68 observable wrappers on every autoexec save and never released them.
        using var store = new GameConfigStore(Path.Combine(_dir, "game.json"), () => _tf);
        var fps = SettingsCatalog.Get<SliderSetting>("FpsMax");
        var before = Subscribers(store.Settings);

        for (var i = 0; i < 300; i++)
        {
            store.Settings.Set(fps, 120 + i % 60);
            var report = await store.FlushAsync();
            Assert.True(report.FullyApplied);
        }

        Assert.Equal(before, Subscribers(store.Settings));
        Assert.Contains($"fps_max {120 + 299 % 60}", File.ReadAllText(Path.Combine(_tf, "cfg", "autoexec.cfg")));
    }

    [Fact]
    public async Task A_save_that_cannot_reach_the_game_is_reported_and_retried()
    {
        var autoexec = Path.Combine(_tf, "cfg", "autoexec.cfg");
        Directory.CreateDirectory(autoexec);
        using var store = new GameConfigStore(Path.Combine(_dir, "game.json"), () => _tf);
        store.Settings.Set(SettingsCatalog.Get<SliderSetting>("FpsMax"), 144);

        var failed = await store.FlushAsync();
        Directory.Delete(autoexec);
        var retried = await store.FlushAsync();

        Assert.True(failed.ConfigSaved);
        Assert.False(failed.FullyApplied);
        Assert.True(retried.FullyApplied);
        Assert.Contains("fps_max 144", File.ReadAllText(autoexec));
    }

    [Fact]
    public void Settings_with_a_null_where_a_list_belongs_count_as_damaged()
    {
        var file = Path.Combine(_dir, "game.json");
        File.WriteAllText(file, """{"version": 2, "values": null}""");

        using var store = new GameConfigStore(file, () => _tf);

        Assert.Equal(LoadState.Reset, store.LoadOutcome.State);
        Assert.False(store.Settings.IsTouched(SettingsCatalog.Get<SliderSetting>("FpsMax")));
    }
}
