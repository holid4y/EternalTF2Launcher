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

        var missing = library.ApplySelection(["Skybox", "Friend's HUD"], ["Unusuals"]);

        Assert.Equal(["Friend's HUD"], missing);
        Assert.Equal(["Skybox", "Unusuals", "Hitsound"], library.State.Order.Select(e => e.Name));
        Assert.Equal([true, true, false], library.State.Order.Select(e => e.Enabled));
    }
}
