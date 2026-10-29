using System.Text.Json.Nodes;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public class SettingsCatalogTests
{
    [Fact]
    public void Ids_are_unique()
    {
        var duplicates = SettingsCatalog.All.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Autoexec_cvars_are_unique_so_parsing_is_unambiguous()
    {
        var duplicates = SettingsCatalog.All
            .Where(s => s.Cvar is not null && !s.IsLaunchOption)
            .GroupBy(s => s.Cvar!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Dependencies_point_at_existing_settings()
    {
        foreach (var setting in SettingsCatalog.All.Where(s => s.DependsOn is not null))
            Assert.NotNull(SettingsCatalog.Find(setting.DependsOn!));
    }

    [Theory]
    [InlineData("tf_dingalingaling_volume")]
    [InlineData("tf_dingalingaling_pitch_min")]
    [InlineData("cl_playerspraydisable")]
    [InlineData("tf_pyrovision_override")]
    [InlineData("tf_viewmodels_offset_override_x")]
    public void Cvars_that_do_not_exist_in_tf2_are_never_written(string bogus)
    {
        var config = new GameConfig();
        var settings = new GameSettings(config);
        settings.Apply(SettingsCatalog.All.ToDictionary(s => s.Id, s => (JsonNode?)s.DefaultNode), complete: true);

        var lines = string.Join('\n', ManagedBlock.Build(settings));

        Assert.DoesNotContain(bogus, lines);
    }

    public static TheoryData<string> AutoexecSettingIds()
    {
        var data = new TheoryData<string>();
        foreach (var setting in SettingsCatalog.All.Where(s => !s.IsLaunchOption && s.Cvar is not null))
            data.Add(setting.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(AutoexecSettingIds))]
    public void Every_autoexec_setting_reads_back_what_it_writes(string id)
    {
        var setting = SettingsCatalog.ById[id];
        foreach (var value in SampleValues(setting))
        {
            var lines = setting.Emit(setting.Coerce(value)).ToList();
            var assignments = CfgText.Assignments(string.Join('\n', lines));

            Assert.True(assignments.TryGetValue(setting.Cvar!, out var raw), $"{id} did not emit {setting.Cvar}");
            var parsed = setting.Parse(raw);
            Assert.True(setting.ValueEquals(value, parsed), $"{id}: wrote {value}, read back {parsed}");
        }
    }

    private static IEnumerable<JsonNode> SampleValues(SettingDefinition setting) => setting switch
    {
        ToggleSetting => [JsonValue.Create(true), JsonValue.Create(false)],
        SliderSetting s => [JsonValue.Create(s.Min), JsonValue.Create(s.Max), JsonValue.Create(s.Default)],
        ChoiceSetting c => c.Options.Select(o => (JsonNode)JsonValue.Create(o.Value)),
        TextSetting => [JsonValue.Create("Demos"), JsonValue.Create("my demos")],
        _ => [],
    };

    [Fact]
    public void Text_values_cannot_break_out_of_their_cfg_line()
    {
        var demoDir = SettingsCatalog.Get<TextSetting>("DemoDir");

        var line = demoDir.Emit(JsonValue.Create("x\"; unbindall; echo \"pwned")).Single();

        Assert.Equal("ds_dir \"x unbindall echo pwned\"", line);
    }

    [Fact]
    public void Slider_values_are_clamped_and_snapped_to_their_step()
    {
        var fov = SettingsCatalog.Get<SliderSetting>("Fov");
        var sensitivity = SettingsCatalog.Get<SliderSetting>("MouseSensitivity");

        Assert.Equal(90, fov.Read(JsonValue.Create(130)));
        Assert.Equal(75, fov.Read(JsonValue.Create(10)));
        Assert.Equal(2.35, sensitivity.Read(JsonValue.Create(2.3512)));
    }

    [Fact]
    public void Invalid_stored_values_fall_back_to_the_default()
    {
        var fps = SettingsCatalog.Get<SliderSetting>("FpsMax");
        var mode = SettingsCatalog.Get<ChoiceSetting>("DemoSupport");

        Assert.Equal(300, fps.Read(JsonValue.Create("lots")));
        Assert.Equal("0", mode.Read(JsonValue.Create("bogus")).Value);
    }

    [Fact]
    public void Legacy_typed_values_from_1x_profiles_are_understood()
    {
        var demoNotify = SettingsCatalog.Get<ChoiceSetting>("DemoNotify");
        var customAccel = SettingsCatalog.Get<ChoiceSetting>("CustomAccel");

        Assert.Equal("1", demoNotify.Read(JsonValue.Create(true)).Value);
        Assert.Equal("0", customAccel.Read(JsonValue.Create(0)).Value);
    }
}
