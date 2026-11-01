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
