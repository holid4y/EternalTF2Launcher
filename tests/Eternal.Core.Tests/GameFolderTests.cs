using Eternal.Core.GameFiles;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public sealed class GameFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eternal-folder-").FullName;
    private string Tf => Path.Combine(_root, "tf");
    private string Custom => Path.Combine(Tf, "custom");
    private string Cfg => Path.Combine(Tf, "cfg");
    private string Aside => Path.Combine(_root, "aside");

    public GameFolderTests()
    {
        Directory.CreateDirectory(Custom);
        Directory.CreateDirectory(Cfg);
        File.WriteAllText(Path.Combine(Tf, "gameinfo.txt"), "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text = "x")
    {
        var path = Path.Combine(Tf, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private FoundItem Find(string name) => GameFolderScan.Run(Tf).Single(i => i.Name == name);

    [Fact]
    public void An_untouched_game_has_nothing_to_ask_about()
    {
        Write("cfg/config.cfg");
        Write("custom/readme.txt");
        Directory.CreateDirectory(Path.Combine(Custom, "workshop"));

        Assert.Empty(GameFolderScan.Run(Tf));
    }

    [Fact]
    public void A_mod_folder_and_a_vpk_are_both_listed()
    {
        Write("custom/skins/materials/a.vtf");
        Write("custom/ToonHUD.vpk");

        var found = GameFolderScan.Run(Tf);

        Assert.Equal(["ToonHUD", "skins"], found.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.All(found, f => Assert.True(f.CanImport));
    }

    [Fact]
    public void An_empty_folder_is_not_listed()
    {
        Directory.CreateDirectory(Path.Combine(Custom, "crit"));

        Assert.Empty(GameFolderScan.Run(Tf));
    }

    [Fact]
    public void The_pieces_of_a_split_vpk_are_one_entry()
    {
        Write("custom/big_dir.vpk");
        Write("custom/big_000.vpk");
        Write("custom/big_001.vpk");
        Write("custom/pack_100.vpk");

        var found = GameFolderScan.Run(Tf).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(["big", "pack_100"], found.Select(f => f.Name));
        Assert.Equal(3, found[0].Files.Count);
    }

    [Fact]
    public void Sound_caches_are_not_mods()
    {
        Write("custom/a.vpk");
        Write("custom/a.vpk.sound.cache");

        Assert.Single(GameFolderScan.Run(Tf));
    }

    [Fact]
    public void A_hud_folder_is_recognised()
    {
        Write("custom/darhud/resource/clientscheme.res");

        Assert.Equal(FoundKind.Hud, Find("darhud").Kind);
    }

    [Fact]
    public void A_hitsound_folder_is_recognised()
    {
        Write("custom/ding/sound/ui/hitsound.wav");

        Assert.Equal(FoundKind.Sounds, Find("ding").Kind);
    }

    [Fact]
    public void Mastercomfig_is_its_own_kind_and_stays_out_of_the_mod_library()
    {
        Write("custom/mastercomfig-medium-preset.vpk");

        var item = Find("mastercomfig-medium-preset");

        Assert.Equal(FoundKind.Mastercomfig, item.Kind);
        Assert.False(item.CanImport);
    }

    [Fact]
    public void An_earlier_preloader_install_is_one_entry_that_cannot_be_imported()
    {
        Write("custom/_casual_preloader_dir.vpk");
        Write("custom/_casual_preloader_000.vpk");
        Write("custom/_QuickPrecache.vpk");

        var item = Assert.Single(GameFolderScan.Run(Tf));

        Assert.Equal(FoundKind.OldPreloader, item.Kind);
        Assert.False(item.CanImport);
        Assert.Equal(3, item.Files.Count);
    }

    [Fact]
    public void Written_config_files_are_listed_and_empty_ones_and_the_games_own_are_not()
    {
        Write("cfg/autoexec.cfg", "fov_desired 90\n");
        Write("cfg/scout.cfg", "// nothing yet\n\n");
        Write("cfg/soldier.cfg", "viewmodel_fov 70\n");
        Write("cfg/config.cfg", "bind w +forward\n");
        Write("cfg/autoexec.cfg.eternal-backup", "old\n");

        var found = GameFolderScan.Run(Tf);

        Assert.Equal(["autoexec.cfg", "soldier.cfg"], found.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.All(found, f => Assert.Equal(FoundKind.Config, f.Kind));
    }

    [Fact]
    public void A_config_folder_is_listed()
    {
        Write("cfg/overrides/custom.cfg", "cl_cmdrate 66\n");

        Assert.Equal(FoundKind.Config, Find("overrides").Kind);
    }

    [Fact]
    public void Putting_a_file_aside_moves_it_out_of_the_game_and_keeps_it()
    {
        Write("custom/a.vpk", "payload");

        GameFolderScan.SetAside(Find("a"), Tf, Aside);

        Assert.False(File.Exists(Path.Combine(Custom, "a.vpk")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(Aside, "custom", "a.vpk")));
    }

    [Fact]
    public void Putting_a_folder_aside_keeps_everything_in_it()
    {
        Write("custom/skins/materials/a.vtf", "t");

        GameFolderScan.SetAside(Find("skins"), Tf, Aside);

        Assert.False(Directory.Exists(Path.Combine(Custom, "skins")));
        Assert.Equal("t", File.ReadAllText(Path.Combine(Aside, "custom", "skins", "materials", "a.vtf")));
    }

    [Fact]
    public void Putting_aside_never_overwrites_an_earlier_copy()
    {
        Write("custom/a.vpk", "new");
        Directory.CreateDirectory(Path.Combine(Aside, "custom"));
        File.WriteAllText(Path.Combine(Aside, "custom", "a.vpk"), "earlier");

        GameFolderScan.SetAside(Find("a"), Tf, Aside);

        Assert.Equal("earlier", File.ReadAllText(Path.Combine(Aside, "custom", "a.vpk")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Aside, "custom", "a (2).vpk")));
    }

    [Fact]
    public void Nothing_outside_the_game_folder_can_be_moved()
    {
        var outside = Path.Combine(_root, "outside.vpk");
        File.WriteAllText(outside, "mine");
        var item = new FoundItem("x", FoundKind.Mod, 1, "custom", [outside], false, "");

        Assert.Throws<InvalidOperationException>(() => GameFolderScan.SetAside(item, Tf, Aside));
        Assert.True(File.Exists(outside));
    }
}

public sealed class AutoexecLeftAloneTests : IDisposable
{
    private readonly string _tf = Directory.CreateTempSubdirectory("eternal-autoexec-").FullName;

    public AutoexecLeftAloneTests()
    {
        Directory.CreateDirectory(Path.Combine(_tf, "cfg"));
        File.WriteAllText(Path.Combine(_tf, "gameinfo.txt"), "");
    }

    public void Dispose() => Directory.Delete(_tf, recursive: true);

    [Fact]
    public void With_nothing_to_write_a_players_autoexec_stays_byte_for_byte()
    {
        var path = Path.Combine(_tf, "cfg", "autoexec.cfg");
        const string mine = "fov_desired 90\r\n\r\n\r\n// my notes   \r\n";
        File.WriteAllText(path, mine);

        var result = AutoexecFile.Write(_tf, []);

        Assert.True(result.Applied);
        Assert.Equal(mine, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".eternal-backup"));
    }

    [Fact]
    public void With_nothing_to_write_no_autoexec_is_created()
    {
        AutoexecFile.Write(_tf, []);

        Assert.False(File.Exists(Path.Combine(_tf, "cfg", "autoexec.cfg")));
    }

    [Fact]
    public void A_block_left_from_before_is_still_emptied_when_nothing_is_set_anymore()
    {
        var path = Path.Combine(_tf, "cfg", "autoexec.cfg");
        File.WriteAllText(path, AutoexecDocument.Render("mine", ["fov_desired 90"]));

        AutoexecFile.Write(_tf, []);

        Assert.DoesNotContain("fov_desired", File.ReadAllText(path));
        Assert.Contains("mine", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_store_told_to_hold_off_leaves_the_game_folder_alone()
    {
        var hold = true;
        using var store = new GameConfigStore(Path.Combine(_tf, "game.json"), () => _tf, () => !hold);
        store.Settings.Set(SettingsCatalog.Get<ToggleSetting>("HitSound"), true);

        var report = await store.FlushAsync();

        Assert.True(report.ConfigSaved);
        Assert.Equal(AutoexecStatus.LeftAlone, report.Autoexec.Status);
        Assert.False(File.Exists(Path.Combine(_tf, "cfg", "autoexec.cfg")));

        hold = false;
        Assert.Equal(AutoexecStatus.Written, (await store.ReapplyAsync()).Autoexec.Status);
    }
}
