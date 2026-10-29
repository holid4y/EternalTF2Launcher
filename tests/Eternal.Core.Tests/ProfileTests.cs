using System.Text.Json;
using System.Text.Json.Nodes;
using Eternal.Core.Profiles;
using Eternal.Core.Settings;

namespace Eternal.Core.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eternal-profiles-").FullName;
    private string ProfilesDir => Path.Combine(_root, "config", "profiles");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ProfileStore NewStore()
    {
        Directory.CreateDirectory(ProfilesDir);
        var store = new ProfileStore(ProfilesDir);
        store.Load();
        return store;
    }

    [Theory]
    [InlineData("../../settings")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win")]
    [InlineData("abc")]
    [InlineData("")]
    public void Ids_that_could_leave_the_profiles_folder_are_rejected(string id)
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.PathFor(id));
        Assert.Throws<ArgumentException>(() => store.Delete(id));
    }

    [Fact]
    public void Imported_profiles_get_a_fresh_id_whatever_the_file_says()
    {
        var store = NewStore();
        var settingsFile = Path.Combine(_root, "config", "game.json");
        File.WriteAllText(settingsFile, "{}");
        var shared = Path.Combine(_root, "shared.json");
        File.WriteAllText(shared, """{"format":"eternal-profile","version":2,"id":"../../game","name":"Evil","values":{"FpsMax":144}}""");

        var imported = store.Import(shared);

        Assert.True(ProfileStore.IsValidId(imported.Profile.Id));
        Assert.Equal("{}", File.ReadAllText(settingsFile));
        Assert.True(File.Exists(Path.Combine(ProfilesDir, imported.Profile.Id + ".json")));
    }

    [Fact]
    public void Legacy_1x_profiles_are_converted_on_import()
    {
        var store = NewStore();
        var legacy = Path.Combine(_root, "legacy.json");
        File.WriteAllText(legacy, """
            {"id":"../x","name":"Old comp","isUserCreated":true,
             "settings":{"FpsMax":240,"HudFastSwitch":true,"DisablePyroland":true},
             "launchOptions":"-novid -dxlevel 81 -console","userContent":"echo hi"}
            """);

        var result = store.Import(legacy);

        Assert.Equal(240, result.Profile.Values["FpsMax"]!.GetValue<long>());
        Assert.Equal("81", result.Profile.Values["DxLevel"]!.GetValue<long>().ToString());
        Assert.Equal("-console", result.Profile.ExtraLaunchArgs);
        Assert.Equal(1, result.UnknownSettings);
        Assert.True(result.HasAutoexecText);
    }

    [Fact]
    public void A_saved_profile_restores_everything_it_captured()
    {
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.Get<SliderSetting>("Fov"), 90);
        settings.Set(SettingsCatalog.Get<ChoiceSetting>("DxLevel"), SettingsCatalog.Get<ChoiceSetting>("DxLevel").Find("95")!);
        settings.Set(SettingsCatalog.Get<SliderSetting>("Width"), 2560);
        settings.SetBinds([new KeyBind { Key = "f", Command = "voicemenu 0 0" }]);
        settings.ExtraLaunchArgs = "-console";
        var profile = ProfileStore.Capture(settings, "Mine", null, null, null);

        settings.Set(SettingsCatalog.Get<SliderSetting>("Fov"), 75);
        settings.Set(SettingsCatalog.Get<ChoiceSetting>("DxLevel"), SettingsCatalog.Get<ChoiceSetting>("DxLevel").Find("81")!);
        settings.Set(SettingsCatalog.Get<SliderSetting>("Width"), 1280);
        settings.Set(SettingsCatalog.Get<SliderSetting>("Height"), 720);
        settings.SetBinds([]);
        settings.ExtraLaunchArgs = "";
        ProfileStore.Apply(profile, settings);

        Assert.Equal(90, settings.Get(SettingsCatalog.Get<SliderSetting>("Fov")));
        Assert.Equal("95", settings.Get(SettingsCatalog.Get<ChoiceSetting>("DxLevel")).Value);
        Assert.Equal(2560, settings.Get(SettingsCatalog.Get<SliderSetting>("Width")));
        Assert.False(settings.IsTouched(SettingsCatalog.Get<SliderSetting>("Height")));
        Assert.Equal("voicemenu 0 0", Assert.Single(settings.Binds).Command);
        Assert.Equal("-console", settings.ExtraLaunchArgs);
    }

    [Fact]
    public void Captured_binds_are_copies_not_shared_references()
    {
        var settings = new GameSettings(new GameConfig());
        settings.SetBinds([new KeyBind { Key = "f", Command = "say a" }]);
        var profile = ProfileStore.Capture(settings, "Mine", null, null, null);

        profile.Binds![0].Command = "say b";

        Assert.Equal("say a", settings.Binds[0].Command);
    }

    [Fact]
    public void Presets_only_change_what_they_list()
    {
        var store = NewStore();
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.Get<ToggleSetting>("AutoRezoom"), false);
        var competitive = store.BuiltIns.Single(p => p.Name == "Competitive");

        ProfileStore.Apply(competitive, settings);

        Assert.False(settings.Get(SettingsCatalog.Get<ToggleSetting>("AutoRezoom")));
        Assert.True(settings.Get(SettingsCatalog.Get<ToggleSetting>("NullMovement")));
        Assert.True(ProfileStore.Matches(competitive, settings));
    }

    [Fact]
    public void Built_in_presets_only_use_known_settings()
    {
        foreach (var preset in NewStore().BuiltIns)
            Assert.All(preset.Values.Keys, key => Assert.NotNull(SettingsCatalog.Find(key)));
    }

    [Fact]
    public void Files_with_unexpected_names_are_ignored_on_load()
    {
        Directory.CreateDirectory(ProfilesDir);
        File.WriteAllText(Path.Combine(ProfilesDir, "..evil.json"), """{"format":"eternal-profile","name":"x","values":{}}""");
        var store = new ProfileStore(ProfilesDir);

        store.Load();

        Assert.Empty(store.UserProfiles);
    }

    [Fact]
    public void Exported_profile_reimports_with_the_same_values()
    {
        var store = NewStore();
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.Get<SliderSetting>("FpsMax"), 165);
        var saved = store.Save(ProfileStore.Capture(settings, "Round trip", "desc", "echo mine", null));
        var exported = Path.Combine(_root, "out.json");

        store.Export(saved, exported);
        var back = store.Import(exported).Profile;

        Assert.NotEqual(saved.Id, back.Id);
        Assert.True(JsonNode.DeepEquals(JsonValue.Create(165L), back.Values["FpsMax"]));
        Assert.Equal("echo mine", back.UserAutoexec);
    }

    [Theory]
    [InlineData("""{"format": "eternal-profile", "name": "Broken", "values": null}""")]
    [InlineData("""{"name": 5, "settings": {}}""")]
    [InlineData("""{"format": 7}""")]
    public void A_profile_with_the_wrong_shape_is_refused_as_not_a_profile(string json)
    {
        var file = Path.Combine(_root, "broken.json");
        File.WriteAllText(file, json);
        var store = new ProfileStore(Path.Combine(_root, "profiles"));

        var refused = Record.Exception(() => store.Import(file));

        Assert.True(refused is JsonException or InvalidDataException, refused?.GetType().Name);
    }
}
