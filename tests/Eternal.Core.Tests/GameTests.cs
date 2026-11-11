using System.Text.Json;
using Eternal.Core.Games;
using Eternal.Core.Platform;
using Eternal.Core.Steam;
using Xunit;

namespace Eternal.Core.Tests;

public sealed class GameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eternal-games-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Install(GameDefinition game, string installDir, string modDir)
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var folder = Path.Combine(steamapps, "common", installDir, modDir);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "gameinfo.txt"), "\"GameInfo\" { }");
        File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{game.AppId}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\"{game.AppId}\"\n\t\"installdir\"\t\"{installDir}\"\n}}\n");
        return folder;
    }

    [Fact]
    public void Every_game_has_its_own_id_app_and_data_folder()
    {
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(g => g.Id).Distinct().Count());
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(g => g.AppId).Distinct().Count());
        Assert.All(GameCatalog.All, g => Assert.Matches("^[a-z0-9]+$", g.Id));
        Assert.Same(GameCatalog.Tf2, GameCatalog.Find("no-such-game"));
        Assert.Same(GameCatalog.GoldRush, GameCatalog.Find("tf2goldrush"));
    }

    [Fact]
    public void Team_Fortress_2_offers_everything_and_other_games_only_what_was_checked()
    {
        Assert.Equal(GameFeatures.All, GameCatalog.Tf2.Features);
        Assert.True(GameCatalog.Tf2.Has(GameFeatures.Backpack | GameFeatures.Mods));
        Assert.All(GameCatalog.All.Where(g => g != GameCatalog.Tf2), g =>
        {
            Assert.True(g.Has(GameFeatures.Launch | GameFeatures.News | GameFeatures.Settings | GameFeatures.Servers | GameFeatures.Screenshots
                | GameFeatures.Mods | GameFeatures.GameBanana));
            Assert.False(g.Has(GameFeatures.SeeThroughWeapons));
            Assert.False(g.Has(GameFeatures.HitKillSounds));
        });
        Assert.All(GameCatalog.All.Where(g => g != GameCatalog.Tf2), g => Assert.False(g.Has(GameFeatures.Backpack)));
    }

    [Fact]
    public void News_without_a_picture_show_the_art_of_the_game_they_are_about()
    {
        const string json = """{"appnews":{"newsitems":[{"title":"Update","url":"https://example.com/1","contents":"Fixed things.","date":1760000000,"feedlabel":"Community Announcements"}]}}""";

        var item = Assert.Single(Eternal.Core.Feed.FeedService.ParseNews(json, 5));

        Assert.Equal(GameCatalog.Tf2.HeaderArt, item.Picture);
        Assert.Equal(GameCatalog.GoldRush.HeaderArt, (item with { GameArt = GameCatalog.GoldRush.HeaderArt }).Picture);
    }

    [Fact]
    public void Settings_a_game_lacks_are_never_written_even_when_a_profile_sets_them()
    {
        var floating = Eternal.Core.Settings.SettingsCatalog.ById["HideFloatingHealth"];
        var fps = Eternal.Core.Settings.SettingsCatalog.ById["FpsMax"];
        var seeThrough = Eternal.Core.Settings.SettingsCatalog.ById[Eternal.Core.Settings.SettingsCatalog.TransparentViewmodelsId];
        foreach (var game in new[] { GameCatalog.Classified, GameCatalog.GoldRush })
        {
            var settings = new Eternal.Core.Settings.GameSettings(new Eternal.Core.Settings.GameConfig(), game: game);
            settings.Apply(new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { [floating.Id] = true, [fps.Id] = 240, [seeThrough.Id] = true });

            var lines = string.Join('\n', Eternal.Core.Settings.ManagedBlock.Build(settings));
            Assert.Contains("fps_max 240", lines);
            Assert.DoesNotContain("tf_hud_target_id_disable_floating_health", lines);
            Assert.DoesNotContain("See-through", lines);
            Assert.False(game.Supports(seeThrough));
        }
        Assert.True(GameCatalog.Tf2.Supports(floating) && GameCatalog.Tf2.Supports(seeThrough));
    }

    [Fact]
    public void Screenshots_are_read_and_rewritten_under_their_own_game()
    {
        var userDir = Path.Combine(_root, "userdata", "123");
        var folder = SteamScreenshots.Folder(userDir, GameCatalog.GoldRush.AppId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.jpg"), [1]);

        var shots = SteamScreenshots.Load(userDir, GameCatalog.GoldRush.AppId);

        Assert.Equal("3826520/screenshots/a.jpg", Assert.Single(shots).RecordedName);
        Assert.Empty(SteamScreenshots.Load(userDir, 440));
        Assert.Equal("3826520", SteamScreenshots.AppOf("3826520/screenshots/a.jpg"));
        Assert.Null(SteamScreenshots.AppOf("3826520/screenshots/../a.jpg"));
    }

    [Fact]
    public void Only_team_fortress_2_starts_with_the_preloader_config()
    {
        foreach (var game in GameCatalog.All)
        {
            var settings = new Eternal.Core.Settings.GameSettings(new Eternal.Core.Settings.GameConfig(), game: game);
            var line = Eternal.Core.Settings.LaunchOptions.Build(settings, preloadInstalled: true);
            Assert.Equal(game == GameCatalog.Tf2, line.Contains(Eternal.Core.Settings.LaunchOptions.PreloadExec));
        }
    }

    [Fact]
    public void Each_game_browses_its_own_gamebanana_section()
    {
        var query = new Eternal.Core.GameBanana.BrowseQuery(Eternal.Core.GameBanana.Section.Mods);

        Assert.Contains("Generic_Game%5D=5418", Eternal.Core.GameBanana.GameBananaClient.IndexUrl(query, GameCatalog.Classified.GameBananaId));
        Assert.Contains("Generic_Game%5D=297", Eternal.Core.GameBanana.GameBananaClient.IndexUrl(query));
        Assert.NotSame(Eternal.Core.GameBanana.GameBananaClient.For(GameCatalog.GoldRush), Eternal.Core.GameBanana.GameBananaClient.Shared);
        Assert.Same(Eternal.Core.GameBanana.GameBananaClient.For(GameCatalog.Tf2), Eternal.Core.GameBanana.GameBananaClient.Shared);
    }

    [Fact]
    public void A_setup_says_which_game_it_was_made_in_and_tf2_setups_stay_as_they_were()
    {
        var goldRush = new Eternal.Core.Settings.GameSettings(new Eternal.Core.Settings.GameConfig(), game: GameCatalog.GoldRush);
        var tf2 = new Eternal.Core.Settings.GameSettings(new Eternal.Core.Settings.GameConfig());

        var fromGoldRush = Eternal.Core.Profiles.ShareCode.Decode(Eternal.Core.Profiles.ShareCode.Encode(
            Eternal.Core.Profiles.ProfileStore.Capture(goldRush, "Mine", null, null, null))).Profile;
        var fromTf2 = Eternal.Core.Profiles.ProfileStore.Capture(tf2, "Mine", null, null, null);

        Assert.Same(GameCatalog.GoldRush, fromGoldRush.MadeFor);
        Assert.Null(fromTf2.Game);
        Assert.DoesNotContain("\"game\"", Eternal.Core.Profiles.ProfileStore.ToJson(fromTf2));
        Assert.Same(GameCatalog.Tf2, Eternal.Core.Profiles.ProfileStore.FromText("""{"format":"eternal-profile","version":2,"name":"x","values":{},"game":"half-life"}""", "x").Profile.MadeFor);
    }

    [Fact]
    public void Tags_with_attributes_are_left_out_of_a_news_summary()
    {
        const string json = """{"appnews":{"newsitems":[{"title":"Halloween","url":"https://example.com/2","contents":"[img src=\"{STEAM_CLAN_IMAGE}/1/a.png\"][/img] Version 3.2.0 is live.","date":1760000000,"feedlabel":"Community Announcements"}]}}""";

        Assert.Equal("Version 3.2.0 is live.", Assert.Single(Eternal.Core.Feed.FeedService.ParseNews(json, 5)).Summary);
    }

    [Fact]
    public void A_game_is_found_through_its_steam_manifest_whatever_its_folder_is_called()
    {
        var folder = Install(GameCatalog.GoldRush, "Somewhere Else", "tf_goldrush");

        Assert.Equal(folder, SteamLocator.FindGame(GameCatalog.GoldRush, [_root]));
        Assert.Null(SteamLocator.FindGame(GameCatalog.Tf2, [_root]));
    }

    [Fact]
    public void A_steam_manifest_without_the_game_files_is_not_an_install()
    {
        Install(GameCatalog.Classified, GameCatalog.Classified.InstallDir, "some-other-folder");

        Assert.Null(SteamLocator.FindGame(GameCatalog.Classified, [_root]));
    }

    [Fact]
    public void Team_Fortress_2_keeps_its_folders_and_other_games_get_their_own()
    {
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

        Assert.Same(paths, paths.ForGame(GameCatalog.Tf2));

        var other = paths.ForGame(GameCatalog.GoldRush);
        Assert.Equal(Path.Combine(_root, "data", "games", "tf2goldrush", "mods.json"), other.ModStateFile);
        Assert.Equal(Path.Combine(_root, "config", "games", "tf2goldrush", "game.json"), other.GameConfigFile);
        Assert.Equal(Path.Combine(_root, "config", "games", "tf2goldrush", "profiles"), other.ProfilesDir);
        Assert.Equal(Path.Combine(_root, "data", "games", "tf2goldrush", "engine"), other.EngineDataDir);
        Assert.Equal(paths.LauncherSettingsFile, other.LauncherSettingsFile);
        Assert.Equal(paths.LogsDir, other.LogsDir);
        Assert.Equal(paths.ImageCacheDir, other.ImageCacheDir);
        Assert.Equal(paths.UpdatesDir, other.UpdatesDir);
        Assert.Equal(paths.SteamWritesFile, other.SteamWritesFile);
    }

    [Fact]
    public void Settings_written_by_an_earlier_version_belong_to_team_fortress_2()
    {
        var settings = JsonSerializer.Deserialize("""
            {"tfPath": "/games/tf", "themeId": "blu", "accentOverride": "#ff0000", "reviewedGameFolder": true, "manageAutoexec": false}
            """, CoreJson.Default.LauncherSettings)!;

        Assert.Equal("tf2", settings.SelectedGame);
        Assert.Equal("/games/tf", settings.Game.Path);
        Assert.Equal("blu", settings.Game.ThemeId);
        Assert.Equal("#ff0000", settings.Game.AccentOverride);
        Assert.True(settings.Game.ReviewedGameFolder);
        Assert.False(settings.Game.ManageAutoexec);
    }

    [Fact]
    public void Each_game_remembers_its_own_folder_and_theme()
    {
        var settings = new LauncherSettings { TfPath = "/games/tf", ThemeId = "blu" };
        settings.SelectedGame = GameCatalog.GoldRush.Id;
        Assert.Equal("blu", settings.Game.ThemeId);
        Assert.Null(settings.Game.Path);

        settings.Game.ThemeId = "toxic";
        settings.Game.Path = "/games/tf_goldrush";
        var json = JsonSerializer.Serialize(settings, CoreJson.Default.LauncherSettings);
        var loaded = JsonSerializer.Deserialize(json, CoreJson.Default.LauncherSettings)!;

        Assert.Equal("tf2goldrush", loaded.SelectedGame);
        Assert.Equal("toxic", loaded.Game.ThemeId);
        Assert.Equal("/games/tf_goldrush", loaded.Game.Path);
        Assert.Equal("blu", loaded.ThemeId);
        Assert.Equal("/games/tf", loaded.TfPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("/games/tf", doc.RootElement.GetProperty("tfPath").GetString());
        Assert.False(doc.RootElement.GetProperty("otherGames").TryGetProperty("tf2", out _));
    }

    [Fact]
    public void A_selected_game_the_launcher_does_not_know_falls_back_to_team_fortress_2()
    {
        var settings = JsonSerializer.Deserialize("""
            {"selectedGame": "half-life", "otherGames": {"half-life": {"path": "/x"}, "tf2": {"path": "/y"}, "tf2goldrush": null}}
            """, CoreJson.Default.LauncherSettings)!;

        Assert.Equal("tf2", settings.SelectedGame);
        Assert.Empty(settings.OtherGames);
        Assert.Null(settings.Game.Path);
    }
}
