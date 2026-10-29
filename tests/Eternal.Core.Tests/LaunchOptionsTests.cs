using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void Building_twice_gives_the_same_line()
    {
        var settings = new GameSettings(new GameConfig { ExtraLaunchArgs = "+exec other.cfg -console" });
        settings.Set(SettingsCatalog.Get<ToggleSetting>("SkipIntro"), true);

        var first = LaunchOptions.Build(settings, preloadInstalled: true);
        settings.ExtraLaunchArgs = LaunchOptions.Join(first);
        var second = LaunchOptions.Build(settings, preloadInstalled: true);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Exec_of_another_file_does_not_hide_or_duplicate_the_preload_exec()
    {
        var settings = new GameSettings(new GameConfig { ExtraLaunchArgs = "+exec other.cfg +exec w/config.cfg" });

        var tokens = LaunchOptions.Build(settings, preloadInstalled: true);

        Assert.Equal(["+exec", "other.cfg", "+exec", "w/config.cfg"], tokens);
    }

    [Fact]
    public void Autoexec_is_never_passed_on_the_command_line()
    {
        var settings = new GameSettings(new GameConfig { ExtraLaunchArgs = "+exec autoexec.cfg -console" });

        var tokens = LaunchOptions.Build(settings, preloadInstalled: false);

        Assert.Equal(["-console"], tokens);
    }

    [Fact]
    public void Preload_exec_is_only_added_while_a_preload_is_installed()
    {
        var settings = new GameSettings(new GameConfig());

        Assert.DoesNotContain("+exec", LaunchOptions.Build(settings, preloadInstalled: false));
        Assert.Contains(LaunchOptions.PreloadExec, LaunchOptions.Build(settings, preloadInstalled: true));
    }

    [Fact]
    public void Untouched_display_settings_leave_the_game_in_charge()
    {
        var settings = new GameSettings(new GameConfig());

        Assert.Empty(LaunchOptions.Build(settings, preloadInstalled: false));
    }

    [Fact]
    public void Borderless_mode_emits_both_flags()
    {
        var settings = new GameSettings(new GameConfig());
        var mode = SettingsCatalog.Get<ChoiceSetting>("DisplayMode");
        settings.Set(mode, mode.Find("borderless")!);

        Assert.Equal(["-windowed", "-noborder"], LaunchOptions.Build(settings, preloadInstalled: false));
    }

    [Fact]
    public void Quoted_arguments_stay_together()
    {
        Assert.Equal(["+map", "cp gorge", "-console"], LaunchOptions.Tokenize("+map \"cp gorge\" -console"));
        Assert.Equal("+map \"cp gorge\" -console", LaunchOptions.Join(["+map", "cp gorge", "-console"]));
    }

    [Fact]
    public void Legacy_launch_strings_become_settings_and_leftovers()
    {
        var (values, extra) = LaunchOptions.FromLegacy("+exec autoexec.cfg -novid -windowed -noborder -w 2560 -h 1440 -dxlevel 90 -threads 4 -console +exec w/config.cfg");

        Assert.True(values["SkipIntro"]!.GetValue<bool>());
        Assert.Equal("borderless", values["DisplayMode"]!.GetValue<string>());
        Assert.Equal(2560, values["Width"]!.GetValue<int>());
        Assert.False(values.ContainsKey("DxLevel"));
        Assert.Equal("-console", extra);
    }

    [Fact]
    public void Managed_flags_typed_by_the_player_are_reported()
    {
        Assert.Equal(["-novid", "-w"], LaunchOptions.ManagedFlagsIn("-novid -w 1920 -console"));
    }
}
