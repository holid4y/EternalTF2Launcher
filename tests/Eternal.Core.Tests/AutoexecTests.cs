using System.Text.Json.Nodes;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public sealed class AutoexecTests : IDisposable
{
    private readonly string _tf = Directory.CreateTempSubdirectory("eternal-tf-").FullName;

    public AutoexecTests()
    {
        File.WriteAllText(Path.Combine(_tf, "gameinfo.txt"), "\"GameInfo\" {}");
        Directory.CreateDirectory(Path.Combine(_tf, "cfg"));
    }

    public void Dispose() => Directory.Delete(_tf, recursive: true);

    private string AutoexecPath => Path.Combine(_tf, "cfg", "autoexec.cfg");

    [Fact]
    public void Player_lines_outside_the_block_survive_a_rewrite()
    {
        File.WriteAllText(AutoexecPath, "bind f \"say hi\"\nfps_max 144\n");

        var result = AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);

        var text = File.ReadAllText(AutoexecPath);
        Assert.Equal(AutoexecStatus.Written, result.Status);
        Assert.Contains("bind f \"say hi\"", text);
        Assert.Contains("fps_max 144", text);
        Assert.Contains("hud_fastswitch 1", text);
    }

    [Fact]
    public void Rewriting_replaces_the_old_block_instead_of_stacking_blocks()
    {
        AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);
        AutoexecFile.Write(_tf, ["hud_fastswitch 0"]);

        var text = File.ReadAllText(AutoexecPath);
        Assert.Single(text.Split('\n'), l => l.Trim() == AutoexecDocument.BeginMarker);
        Assert.DoesNotContain("hud_fastswitch 1", text);
    }

    [Fact]
    public void Unchanged_content_is_not_rewritten()
    {
        AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);

        var second = AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);

        Assert.Equal(AutoexecStatus.Unchanged, second.Status);
    }

    [Fact]
    public void Previous_version_is_kept_as_a_backup()
    {
        File.WriteAllText(AutoexecPath, "echo original\n");

        AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);

        Assert.Equal("echo original\n", File.ReadAllText(AutoexecPath + ".eternal-backup"));
    }

    [Fact]
    public void An_unreadable_autoexec_is_left_untouched()
    {
        Directory.CreateDirectory(AutoexecPath); // reading a directory as a file fails like a locked file

        var result = AutoexecFile.Write(_tf, ["hud_fastswitch 1"]);

        Assert.Equal(AutoexecStatus.Failed, result.Status);
        Assert.True(Directory.Exists(AutoexecPath));
    }

    [Fact]
    public void Missing_game_folder_is_reported_not_thrown()
    {
        var result = AutoexecFile.Write(Path.Combine(_tf, "nope"), ["x 1"]);

        Assert.Equal(AutoexecStatus.NoGameFolder, result.Status);
    }

    [Fact]
    public void Mastercomfig_users_get_the_overrides_autoexec()
    {
        Directory.CreateDirectory(Path.Combine(_tf, "custom"));
        File.WriteAllText(Path.Combine(_tf, "custom", "mastercomfig-medium-preset.vpk"), "");

        Assert.Equal(Path.Combine(_tf, "cfg", "overrides", "autoexec.cfg"), AutoexecFile.Locate(_tf));
    }

    [Fact]
    public void Half_deleted_block_keeps_all_text_as_the_players()
    {
        var doc = AutoexecDocument.Parse($"echo a\n{AutoexecDocument.BeginMarker}\nfps_max 1\n");

        Assert.False(doc.HasManagedBlock);
        Assert.Contains("fps_max 1", doc.UserContent);
    }

    [Fact]
    public void Only_touched_settings_are_written()
    {
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.Get<ToggleSetting>("HudFastSwitch"), true);

        var lines = ManagedBlock.Build(settings);

        Assert.Contains("hud_fastswitch 1", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("fps_max"));
    }

    [Fact]
    public void Resetting_a_setting_still_writes_the_default_so_archived_values_are_overridden()
    {
        var settings = new GameSettings(new GameConfig());
        var fps = SettingsCatalog.Get<SliderSetting>("FpsMax");
        settings.Set(fps, 144);

        settings.ResetToDefault(fps);

        Assert.Contains("fps_max 300", ManagedBlock.Build(settings));
    }

    [Fact]
    public void Turning_null_movement_off_restores_wasd()
    {
        var settings = new GameSettings(new GameConfig());
        var nullMovement = SettingsCatalog.Get<ToggleSetting>("NullMovement");
        settings.Set(nullMovement, true);
        settings.Set(nullMovement, false);

        var lines = ManagedBlock.Build(settings);

        Assert.Contains("bind w +forward", lines);
        Assert.DoesNotContain(lines, l => l.Contains("+mfwd"));
    }

    [Fact]
    public void Null_movement_script_defines_every_alias_it_calls()
    {
        var script = string.Join('\n', NullMovement.On);

        foreach (var alias in new[] { "checkfwd", "checkback", "checkleft", "checkright", "none" })
            Assert.Contains($"alias {alias} ", script);
    }

    [Fact]
    public void Binds_with_quotes_are_rejected_instead_of_corrupting_the_file()
    {
        Assert.False(ManagedBlock.IsValidBind(new KeyBind { Key = "f", Command = "say \"hi\"" }));
        Assert.True(ManagedBlock.IsValidBind(new KeyBind { Key = "f", Command = "say hi" }));
    }

    [Fact]
    public void First_run_import_reads_a_hand_written_autoexec()
    {
        var values = ObservedSettings.ImportFromAutoexec("fps_max 144; hud_fastswitch \"1\"\nalias +mfwd \"-back;+forward\"\n// sensitivity 9\n");

        Assert.Equal(144, values["FpsMax"].GetValue<long>());
        Assert.True(values["HudFastSwitch"].GetValue<bool>());
        Assert.True(values["NullMovement"].GetValue<bool>());
        Assert.False(values.ContainsKey("MouseSensitivity"));
    }

    [Fact]
    public void Observed_values_come_from_the_games_own_config()
    {
        File.WriteAllText(Path.Combine(_tf, "cfg", "config.cfg"), "fps_max \"240\"\nbind \"w\" \"+mfwd\"\n");
        File.WriteAllText(Path.Combine(_tf, "cfg", "video.txt"), "\"VideoConfig\"\n{\n\t\"setting.mat_antialias\"\t\t\"8\"\n}\n");

        var observed = ObservedSettings.Read(_tf);

        Assert.Equal(240, observed["FpsMax"].GetValue<long>());
        Assert.Equal(8, observed["AntiAliasing"].GetValue<long>());
        Assert.True(observed["NullMovement"].GetValue<bool>());
    }

    [Fact]
    public void Untouched_settings_show_the_observed_value()
    {
        var settings = new GameSettings(new GameConfig(), new Dictionary<string, JsonNode> { ["FpsMax"] = 240 });

        Assert.Equal(240, settings.Get(SettingsCatalog.Get<SliderSetting>("FpsMax")));
        Assert.False(settings.IsTouched(SettingsCatalog.Get<SliderSetting>("FpsMax")));
    }
}
