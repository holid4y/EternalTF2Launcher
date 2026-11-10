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
    public void Launch_options_from_someone_else_lose_their_control_characters()
    {
        var import = ProfileStore.FromText("""
            {"format":"eternal-profile","version":2,"name":"Shared","values":{},"extraLaunchArgs":"-console\n+connect\u0000 1.2.3.4\t"}
            """, "Shared setup");

        Assert.Equal("-console +connect  1.2.3.4", import.Profile.ExtraLaunchArgs);
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
    public void A_saved_profile_stops_matching_once_binds_or_launch_options_change()
    {
        var settings = new GameSettings(new GameConfig());
        settings.SetBinds([new KeyBind { Key = "f", Command = "voicemenu 0 0" }]);
        var profile = ProfileStore.Capture(settings, "Mine", null, null, null);
        Assert.True(ProfileStore.Matches(profile, settings));

        settings.SetBinds([new KeyBind { Key = "f", Command = "voicemenu 0 0" }, new KeyBind { Key = "F8", Command = "say gg" }]);
        Assert.False(ProfileStore.Matches(profile, settings));

        settings.SetBinds([new KeyBind { Key = "f", Command = "voicemenu 1 1" }]);
        Assert.False(ProfileStore.Matches(profile, settings));

        settings.SetBinds([new KeyBind { Key = "F", Command = "voicemenu 0 0", Label = "renamed" }]);
        Assert.True(ProfileStore.Matches(profile, settings));

        settings.ExtraLaunchArgs = "-console";
        Assert.False(ProfileStore.Matches(profile, settings));

        settings.ExtraLaunchArgs = "";
        settings.Set(SettingsCatalog.All.First(s => s.IsLaunchOption), SettingsCatalog.All.First(s => s.IsLaunchOption).DefaultNode);
        Assert.False(ProfileStore.Matches(profile, settings));
    }

    [Fact]
    public void Presets_ignore_binds_and_launch_options()
    {
        var store = NewStore();
        var settings = new GameSettings(new GameConfig());
        var competitive = store.BuiltIns.Single(p => p.Name == "Competitive");
        ProfileStore.Apply(competitive, settings);

        settings.SetBinds([new KeyBind { Key = "F8", Command = "say gg" }]);
        settings.ExtraLaunchArgs = "-console";

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
    public void A_shared_code_carries_settings_binds_autoexec_and_mods()
    {
        var settings = new GameSettings(new GameConfig());
        settings.Set(SettingsCatalog.Get<SliderSetting>("FpsMax"), 165);
        settings.SetBinds([new KeyBind { Key = "F", Command = "voicemenu 0 0" }]);
        var mods = new ProfileMods
        {
            Addons = ["Femme Pyro", "My HUD"],
            Particles = ["Clean explosions"],
            Sources = new() { ["Femme Pyro"] = "gamebanana:516266:1771624" },
        };
        var shared = ProfileStore.Capture(settings, "Round trip", "desc", "echo mine", mods);

        var code = ShareCode.Encode(shared);
        var back = ShareCode.Decode("  " + code[..20] + "\n" + code[20..] + " ").Profile;

        Assert.StartsWith(ShareCode.Prefix, code);
        Assert.NotEqual(shared.Id, back.Id);
        Assert.True(JsonNode.DeepEquals(JsonValue.Create(165L), back.Values["FpsMax"]));
        Assert.Equal("voicemenu 0 0", Assert.Single(back.Binds!).Command);
        Assert.Equal("echo mine", back.UserAutoexec);
        Assert.Equal(["Femme Pyro", "My HUD"], back.Mods!.Addons);
        Assert.Equal(["Clean explosions"], back.Mods.Particles);
        Assert.Equal("gamebanana:516266:1771624", back.Mods.Sources!["Femme Pyro"]);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("ETERNAL1:!!!!")]
    [InlineData("ETERNAL1:AAAA")]
    public void Broken_codes_are_refused(string code) =>
        Assert.Throws<InvalidDataException>(() => ShareCode.Decode(code));

    [Fact]
    public void A_code_that_unpacks_to_gigabytes_is_refused()
    {
        using var packed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(new byte[8 * 1024 * 1024]);
        var code = ShareCode.Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains("far more", Assert.Throws<InvalidDataException>(() => ShareCode.Decode(code)).Message);
    }

    [Fact]
    public void Mod_sources_that_are_not_gamebanana_files_are_dropped()
    {
        var json = """
            {"format":"eternal-profile","name":"x","values":{},"mods":{"addons":["a","b"],"particles":[],
             "sources":{"a":"gamebanana:1:2","b":"https://evil.example/x.zip"}}}
            """;
        var mods = ProfileStore.FromText(json, "x").Profile.Mods!;
        Assert.Equal(["a"], mods.Sources!.Keys);
    }

    [Fact]
    public void A_bundle_carries_mod_files_and_refuses_paths_outside_its_folders()
    {
        var mod = Path.Combine(_root, "mymod");
        Directory.CreateDirectory(Path.Combine(mod, "materials"));
        File.WriteAllText(Path.Combine(mod, "materials", "a.vmt"), "VertexLitGeneric {}");
        var settings = new GameSettings(new GameConfig());
        var profile = ProfileStore.Capture(settings, "Bundle", null, null, new ProfileMods { Addons = ["mymod"] });
        var bundle = Path.Combine(_root, "setup.eternal");

        ProfileBundle.Write(bundle, profile, [new BundledMod("mymod", Eternal.Core.Engine.ModKind.Addon, mod)]);
        var contents = ProfileBundle.Open(bundle, Path.Combine(_root, "staging"));
        var unpacked = Assert.Single(contents.Mods);
        Assert.Equal("VertexLitGeneric {}", File.ReadAllText(Path.Combine(unpacked.Folder, "materials", "a.vmt")));
        Assert.Equal(["mymod"], contents.Import.Profile.Mods!.Addons);

        var evil = Path.Combine(_root, "evil.eternal");
        using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("profile.json").Open()))
                w.Write(ProfileStore.ToJson(profile));
            using (var w = new StreamWriter(zip.CreateEntry("mods/addons/x/../../../../escape.txt").Open()))
                w.Write("no");
        }
        Assert.Throws<InvalidDataException>(() => ProfileBundle.Open(evil, Path.Combine(_root, "staging")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Equal(contents.Staging, Assert.Single(Directory.GetDirectories(Path.Combine(_root, "staging"))));
    }

    [Theory]
    [InlineData("""{"format": "eternal-profile", "name": "Broken", "values": null}""")]
    [InlineData("""{"name": 5, "settings": {}}""")]
    [InlineData("""{"format": 7}""")]
    [InlineData("""{"format": "eternal-profile", "name": "Holes", "values": {}, "binds": [null]}""")]
    [InlineData("""{"format": "eternal-profile", "name": "Holes", "values": {}, "mods": {"addons": [null]}}""")]
    public void A_profile_with_the_wrong_shape_is_refused_as_not_a_profile(string json)
    {
        var file = Path.Combine(_root, "broken.json");
        File.WriteAllText(file, json);
        var store = new ProfileStore(Path.Combine(_root, "profiles"));

        var refused = Record.Exception(() => store.Import(file));

        Assert.True(refused is JsonException or InvalidDataException, refused?.GetType().Name);
    }
}
