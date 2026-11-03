namespace Eternal.Core.Servers;

public static class GameModes
{
    private static readonly (string Prefix, string Mode)[] Prefixes =
    [
        ("plr_", "Payload Race"), ("pl_", "Payload"), ("cp_", "Control Points"), ("tc_", "Territorial Control"),
        ("ctf_", "Capture the Flag"), ("koth_", "King of the Hill"), ("arena_", "Arena"), ("mvm_", "Mann vs. Machine"),
        ("sd_", "Special Delivery"), ("pass_", "PASS Time"), ("pd_", "Player Destruction"), ("rd_", "Robot Destruction"),
        ("vsh_", "Versus Saxton Hale"), ("zi_", "Zombie Infection"), ("tow_", "Tug of War"), ("mge_", "MGE"),
        ("trade_", "Trading"), ("jump_", "Jumping"), ("rj_", "Jumping"), ("surf_", "Surfing"), ("tfdb_", "Dodgeball"),
        ("db_", "Dodgeball"), ("dr_", "Deathrun"), ("deathrun_", "Deathrun"), ("zf_", "Zombie Fortress"), ("ph_", "Prop Hunt"),
        ("slender_", "Slender Fortress"), ("ba_", "Jail"), ("jb_", "Jail"), ("achievement_", "Achievement"), ("idle_", "Idle"),
        ("tr_", "Training"), ("dm_", "Deathmatch"), ("bball_", "Basketball"), ("ultiduo_", "Ultiduo"), ("cs_", "Hide and Seek"),
    ];

    public static IReadOnlyList<string> All { get; } = Prefixes.Select(p => p.Mode).Distinct().Order(StringComparer.Ordinal).ToArray();

    public static string Of(string map)
    {
        var name = map.StartsWith("workshop/", StringComparison.Ordinal) ? WorkshopMaps.DisplayName(map) : map;
        foreach (var (prefix, mode) in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return mode;
        }
        return "Other";
    }
}
