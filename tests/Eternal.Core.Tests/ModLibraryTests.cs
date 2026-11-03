using Eternal.Core.Engine;
using Eternal.Core.Mods;
using Eternal.Core.Platform;

namespace Eternal.Core.Tests;

public sealed class ModLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-library-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModLibrary Create(string state)
    {
        var paths = new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"));
        paths.EnsureCreated();
        File.WriteAllText(paths.ModStateFile, state);
        var engine = new PreloaderEngine(new EngineClient(new EngineRuntime(paths, () => null)));
        return new ModLibrary(engine, paths, new ThumbnailStore(paths.ThumbnailsDir));
    }

    [Fact]
    public void A_profile_turns_on_its_mods_in_its_order_and_names_the_ones_you_lack()
    {
        var library = Create("""
            {"order": [
              {"name": "Hitsound", "kind": "Addon", "enabled": true},
              {"name": "Skybox", "kind": "Addon", "enabled": false},
              {"name": "Unusuals", "kind": "Particle", "enabled": false}
            ]}
            """);

        var missing = library.ApplySelection(new Eternal.Core.Profiles.ProfileMods { Addons = ["Skybox", "Friend's HUD"], Particles = ["Unusuals"] });

        Assert.Equal(["Friend's HUD"], missing);
        Assert.Equal(["Skybox", "Unusuals", "Hitsound"], library.State.Order.Select(e => e.Name));
        Assert.Equal([true, true, false], library.State.Order.Select(e => e.Enabled));
    }

    [Fact]
    public void A_shared_gamebanana_mod_is_found_by_its_file_even_under_another_name()
    {
        var library = Create("""
            {"order": [
              {"name": "Femme Pyro 2", "kind": "Addon", "enabled": false, "source": "gamebanana:516266:1771624"},
              {"name": "Femme Pyro", "kind": "Addon", "enabled": true}
            ]}
            """);

        var missing = library.ApplySelection(new Eternal.Core.Profiles.ProfileMods
        {
            Addons = ["Femme Pyro"],
            Sources = new() { ["Femme Pyro"] = "gamebanana:516266:1771624" },
        });

        Assert.Empty(missing);
        Assert.Equal([true, false], library.State.Order.Select(e => e.Enabled));
        Assert.Equal("Femme Pyro 2", library.State.Order[0].Name);
    }
}
