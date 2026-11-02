using Eternal.Core.Engine;
using Eternal.Core.Mods;

namespace Eternal.Core.Tests;

public sealed class ImportPlanTests
{
    private static ImportPart Part(string source, int files, params string[] conflicts) =>
        new(source, source, "folder", files, files * 100L, conflicts);

    [Fact]
    public void Parts_that_change_different_files_are_all_picked()
    {
        var parts = new[] { Part("Rocket Launcher", 4), Part("Shotgun", 3), Part("Minigun", 5) };

        Assert.Equal(["Rocket Launcher", "Shotgun", "Minigun"], ImportPlan.DefaultChoice(parts));
    }

    [Fact]
    public void Of_alternatives_only_the_largest_is_picked()
    {
        var parts = new[] { Part("54 FOV", 10, "70 FOV"), Part("70 FOV", 12, "54 FOV"), Part("Extras", 2) };

        Assert.Equal(["70 FOV", "Extras"], ImportPlan.DefaultChoice(parts));
    }

    [Fact]
    public void Alternatives_are_grouped_through_each_other()
    {
        // The HUD overlaps both customisations, which don't overlap each other.
        var parts = new[] { Part("4:3 fix", 3, "eve-hud"), Part("eve-hud", 900, "4:3 fix", "24p scoreboard"), Part("24p scoreboard", 2, "eve-hud") };

        Assert.Equal(["eve-hud"], ImportPlan.DefaultChoice(parts));
    }

    [Fact]
    public void Equal_alternatives_keep_the_first()
    {
        var parts = new[] { Part("NEW", 5, "OLD"), Part("OLD", 5, "NEW") };

        Assert.Equal(["NEW"], ImportPlan.DefaultChoice(parts));
    }
}

public sealed class InstallNameTests
{
    private static ModEntry Entry(string name, string? source = null) => new() { Name = name, Kind = ModKind.Addon, Source = source };

    [Fact]
    public void A_free_name_is_kept()
    {
        Assert.Equal("Female Sniper", ModLibrary.FreeName([Entry("Classic Hitsound")], "Female Sniper", "gamebanana:194725:"));
    }

    [Fact]
    public void Reinstalling_the_same_submission_reuses_its_name()
    {
        var order = new[] { Entry("Female Sniper", "gamebanana:194725:1100") };

        Assert.Equal("Female Sniper", ModLibrary.FreeName(order, "Female Sniper", "gamebanana:194725:"));
    }

    [Fact]
    public void Another_mods_name_is_never_taken_over()
    {
        var order = new[] { Entry("female sniper"), Entry("Female Sniper 2", "gamebanana:1:2") };

        Assert.Equal("Female Sniper 3", ModLibrary.FreeName(order, "Female Sniper", "gamebanana:194725:"));
    }

    [Fact]
    public void A_sound_with_a_mods_id_is_another_submission()
    {
        var order = new[] { Entry("Hitsound", "gamebanana:500:9") };

        Assert.Equal("Hitsound 2", ModLibrary.FreeName(order, "Hitsound", "gamebanana-sound:500:"));
    }
}
