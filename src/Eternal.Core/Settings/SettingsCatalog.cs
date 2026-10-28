using System.Globalization;

namespace Eternal.Core.Settings;

public sealed record SettingCategory(string Id, string Title, string Summary, IReadOnlyList<SettingDefinition> Settings);

/// <summary>
/// Cvar names, ranges and defaults are checked against TF2's binaries and
/// cfg/user_default.scr. Defaults match the game, so an untouched setting
/// changes nothing.
/// </summary>
public static class SettingsCatalog
{
    private const SettingTags Launch = SettingTags.LaunchOption;
    private const SettingTags Perf = SettingTags.Performance;
    private const SettingTags Looks = SettingTags.Visuals;
    private const SettingTags Adv = SettingTags.Advanced;

    private static readonly ChoiceOption[] DingSounds =
    [
        new("Default", "0"), new("Electro", "1"), new("Notes", "2"), new("Percussion", "3"), new("Retro", "4"),
        new("Space", "5"), new("Beepo", "6"), new("Vortex", "7"), new("Squasher", "8"),
    ];

    public static IReadOnlyList<SettingCategory> Categories { get; } = Build();

    public static IReadOnlyList<SettingDefinition> All { get; } = Categories.SelectMany(c => c.Settings).ToArray();

    public static IReadOnlyDictionary<string, SettingDefinition> ById { get; } =
        All.ToDictionary(s => s.Id, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, SettingDefinition> ByCvar { get; } =
        All.Where(s => s.Cvar is not null && !s.IsLaunchOption)
           .ToDictionary(s => s.Cvar!, StringComparer.OrdinalIgnoreCase);

    public static SettingDefinition? Find(string id) => ById.GetValueOrDefault(id);

    public static T Get<T>(string id) where T : SettingDefinition => (T)ById[id];

    private static SettingCategory Category(string id, string title, string summary, params SettingDefinition[] settings)
    {
        foreach (var setting in settings)
            setting.Category = id;
        return new SettingCategory(id, title, summary, settings);
    }

    private static Func<bool, IEnumerable<string>> Lines(string[] on, string[] off) => value => value ? on : off;

    private static IReadOnlyList<SettingCategory> Build() =>
    [
        Category("display", "Display", "Window mode, resolution and rendering level. Applied when TF2 starts.",
            new ChoiceSetting
            {
                Id = "DisplayMode", Title = "Window mode", Tags = Launch,
                Summary = "Borderless looks like fullscreen but switches to other windows instantly.",
                Options = [new("Fullscreen", "fullscreen"), new("Windowed", "windowed"), new("Borderless window", "borderless")],
                Default = "fullscreen",
                Emitter = option => option.Value switch
                {
                    "windowed" => ["-windowed"],
                    "borderless" => ["-windowed", "-noborder"],
                    _ => ["-fullscreen"],
                },
            },
            new SliderSetting
            {
                Id = "Width", Title = "Width", Cvar = "-w", Tags = Launch, Unit = "px",
                Summary = "Horizontal resolution. Leave untouched to keep TF2's own video setting.",
                Min = 640, Max = 7680, Default = 1920, Keywords = ["resolution"],
            },
            new SliderSetting
            {
                Id = "Height", Title = "Height", Cvar = "-h", Tags = Launch, Unit = "px",
                Summary = "Vertical resolution.",
                Min = 480, Max = 4320, Default = 1080, Keywords = ["resolution"],
            },
            new SliderSetting
            {
                Id = "RefreshRate", Title = "Refresh rate", Cvar = "-freq", Tags = Launch, Unit = "Hz",
                Summary = "Refresh rate TF2 asks for in fullscreen. 0 uses whatever the display is set to.",
                Min = 0, Max = 500, Default = 0, SpecialValues = new Dictionary<double, string> { [0] = "Match display" },
                Emitter = hz => hz > 0 ? ["-freq", hz.ToString(CultureInfo.InvariantCulture)] : [],
            },
            new ChoiceSetting
            {
                Id = "DxLevel", Title = "Rendering level", Cvar = "-dxlevel", Tags = Launch | Perf,
                Summary = "Forcing a level resets TF2's video options to that level's defaults on every launch. Only force one if you need it.",
                Options =
                [
                    new("Don't force", "0", "TF2 picks the best level for your GPU."),
                    new("DirectX 8.1", "81", "Fastest. Hides skins, war paints and some effects."),
                    new("DirectX 9.0", "90"),
                    new("DirectX 9.5", "95"),
                    new("DirectX 9.8", "98"),
                    new("DirectX 10", "100", "Full quality."),
                ],
                Default = "0", Keywords = ["dxlevel", "directx", "vulkan"],
                Emitter = option => option.Value == "0" ? [] : ["-dxlevel", option.Value],
            }),

        Category("gameplay", "Gameplay", "Aim, movement and weapon handling.",
            new SliderSetting
            {
                Id = "Fov", Title = "Field of view", Cvar = "fov_desired",
                Summary = "How much of the world you see. Most players use the maximum.",
                Min = 75, Max = 90, Default = 75, Unit = "°", Keywords = ["fov"],
            },
            new SliderSetting
            {
                Id = "MouseSensitivity", Title = "Mouse sensitivity", Cvar = "sensitivity",
                Summary = "Multiplier for how far the camera turns per mouse movement.",
                Min = 0.1, Max = 20, Step = 0.01, Decimals = 2, Default = 3,
            },
            new ToggleSetting
            {
                Id = "RawInput", Title = "Raw mouse input", Cvar = "m_rawinput", Default = true,
                Summary = "Reads the mouse directly and ignores pointer acceleration from the OS.",
            },
            new ChoiceSetting
            {
                Id = "CustomAccel", Title = "Mouse acceleration", Cvar = "m_customaccel",
                Summary = "Faster flicks turn further. Off keeps aim consistent.",
                Options = [new("Off", "0"), new("On", "1")], Default = "0", Tags = SettingTags.Competitive,
            },
            new ToggleSetting
            {
                Id = "HudFastSwitch", Title = "Quick weapon switch", Cvar = "hud_fastswitch",
                Summary = "Number keys swap weapons immediately instead of opening the selection menu.",
            },
            new ToggleSetting
            {
                Id = "AutoReload", Title = "Auto reload", Cvar = "cl_autoreload",
                Summary = "Reloads by itself whenever you stop firing.",
            },
            new ToggleSetting
            {
                Id = "AutoRezoom", Title = "Sniper re-zoom", Cvar = "cl_autorezoom", Default = true,
                Summary = "Zooms back in after each Sniper Rifle shot.",
            },
            new ToggleSetting
            {
                Id = "RememberActiveWeapon", Title = "Keep weapon after respawn", Cvar = "tf_remember_activeweapon",
                Summary = "Respawn holding the weapon you died with.",
            },
            new ToggleSetting
            {
                Id = "NullMovement", Title = "Null-movement", Tags = SettingTags.Competitive,
                Summary = "Holding two opposite movement keys moves you toward the newest one instead of stopping. Rebinds W, A, S and D.",
                Emitter = NullMovement.Emit,
            }),

        Category("feedback", "Combat Feedback", "Damage numbers and the sounds that confirm a hit.",
            new ToggleSetting
            {
                Id = "DamageNumbers", Title = "Damage numbers", Cvar = "hud_combattext",
                Summary = "Floating numbers show how much damage each hit did.",
            },
            new ToggleSetting
            {
                Id = "DamageNumbersBatching", Title = "Combine quick hits", Cvar = "hud_combattext_batching",
                Summary = "Rapid hits add up into one number instead of many small ones.", DependsOn = "DamageNumbers",
            },
            new ToggleSetting
            {
                Id = "HitSound", Title = "Hit sound", Cvar = "tf_dingalingaling",
                Summary = "Plays a ding whenever you damage an enemy.", Keywords = ["hitsound", "ding"],
            },
            new ChoiceSetting
            {
                Id = "HitSoundEffect", Title = "Hit sound style", Cvar = "tf_dingalingaling_effect",
                Summary = "Which built-in sound plays on hits. Custom hitsounds come from mods.",
                Options = DingSounds, Default = "0", DependsOn = "HitSound",
            },
            new SliderSetting
            {
                Id = "HitsoundVolume", Title = "Hit sound volume", Cvar = "tf_dingaling_volume",
                Summary = "Loudness of the hit sound.", DependsOn = "HitSound",
                Min = 0, Max = 1, Step = 0.05, Decimals = 2, Default = 0.75,
            },
            new SliderSetting
            {
                Id = "HitsoundPitchMin", Title = "Pitch on light hits", Cvar = "tf_dingaling_pitchmindmg",
                Summary = "Pitch for small hits. 100 is the normal pitch.", DependsOn = "HitSound",
                Min = 1, Max = 255, Default = 100,
            },
            new SliderSetting
            {
                Id = "HitsoundPitchMax", Title = "Pitch on heavy hits", Cvar = "tf_dingaling_pitchmaxdmg",
                Summary = "Pitch for big hits. Set it apart from light hits to hear damage.", DependsOn = "HitSound",
                Min = 1, Max = 255, Default = 100,
            },
            new ToggleSetting
            {
                Id = "KillSound", Title = "Kill sound", Cvar = "tf_dingalingaling_lasthit",
                Summary = "A separate sound for the killing blow.", Keywords = ["killsound"],
            },
            new ChoiceSetting
            {
                Id = "KillSoundEffect", Title = "Kill sound style", Cvar = "tf_dingalingaling_last_effect",
                Summary = "Which built-in sound plays on kills.", Options = DingSounds, Default = "0", DependsOn = "KillSound",
            },
            new SliderSetting
            {
                Id = "KillSoundVolume", Title = "Kill sound volume", Cvar = "tf_dingaling_lasthit_volume",
                Summary = "Loudness of the kill sound.", DependsOn = "KillSound",
                Min = 0, Max = 1, Step = 0.05, Decimals = 2, Default = 0.75,
            }),

        Category("hud", "HUD & Menus", "What the interface shows and which pop-ups get out of the way.",
            new ToggleSetting
            {
                Id = "MinimalHud", Title = "Minimal HUD", Cvar = "cl_hud_minmode",
                Summary = "Smaller health and ammo panels for a cleaner screen.",
            },
            new ToggleSetting
            {
                Id = "HudPlayerClassModel", Title = "3D class portrait", Cvar = "cl_hud_playerclass_use_playermodel", Default = true,
                Summary = "Animated character model next to your health. Off saves a little performance.", Tags = Perf,
            },
            new ToggleSetting
            {
                Id = "HideFloatingHealth", Title = "Hide target health bars", Cvar = "tf_hud_target_id_disable_floating_health",
                Summary = "Removes the health bar that floats over the player you aim at.",
            },
            new ToggleSetting
            {
                Id = "ScoreboardPingAsText", Title = "Ping as numbers", Cvar = "tf_scoreboard_ping_as_text",
                Summary = "Scoreboard shows each player's ping in milliseconds instead of bars.", Keywords = ["latency"],
            },
            new ToggleSetting
            {
                Id = "MedicAutocall", Title = "Highlight hurt teammates", Cvar = "hud_medicautocallers",
                Summary = "As Medic, teammates below a health threshold call for you automatically.",
            },
            new SliderSetting
            {
                Id = "MedicAutocallThreshold", Title = "Highlight below", Cvar = "hud_medicautocallersthreshold",
                Summary = "Health percentage that triggers the highlight.", DependsOn = "MedicAutocall",
                Min = 10, Max = 99, Default = 75, Unit = "%",
            },
            new ToggleSetting
            {
                Id = "SniperFullChargeBell", Title = "Full-charge bell", Cvar = "tf_sniper_fullcharge_bell",
                Summary = "As Sniper, a bell rings when a scoped shot reaches full charge.",
            },
            new ToggleSetting
            {
                Id = "SimpleDisguiseMenu", Title = "Simple disguise menu", Cvar = "tf_simple_disguise_menu",
                Summary = "As Spy, pick disguises from a compact three-column menu.",
            },
            new ToggleSetting
            {
                Id = "ColorblindAssist", Title = "Colorblind assist", Cvar = "tf_colorblindassist",
                Summary = "Adds icons over players soaked in Jarate or Mad Milk.", Keywords = ["accessibility"],
            },
            new ToggleSetting
            {
                Id = "DisableHtmlMotd", Title = "Block server web pages", Cvar = "cl_disablehtmlmotd",
                Summary = "Stops community servers from showing web-page welcome screens, which are often ads.", Keywords = ["motd", "ads"],
            },
            new ToggleSetting
            {
                Id = "AskBlacklistOptOut", Title = "Skip \"blacklist server?\"", Cvar = "cl_ask_blacklist_opt_out",
                Summary = "No prompt asking to blacklist a server after leaving it.",
            },
            new ToggleSetting
            {
                Id = "AskFavoriteOptOut", Title = "Skip \"favorite server?\"", Cvar = "cl_ask_favorite_opt_out",
                Summary = "No prompt asking to favorite a server after playing on it.",
            },
            new ToggleSetting
            {
                Id = "RomeVisionSkipPrompt", Title = "Skip Romevision prompt", Cvar = "tf_romevision_skip_prompt",
                Summary = "No Romevision question in Mann vs. Machine.", Keywords = ["mvm"],
            }),

        Category("viewmodel", "Weapon View", "How your weapon appears in first person.",
            new ToggleSetting
            {
                Id = "DrawViewmodel", Title = "Show weapon", Cvar = "r_drawviewmodel", Default = true,
                Summary = "Off hides your weapon entirely for a clear view.",
            },
            new ToggleSetting
            {
                Id = "MinViewmodels", Title = "Minimal weapon view", Cvar = "tf_use_min_viewmodels",
                Summary = "Weapons sit lower and smaller, covering less of the screen.", DependsOn = "DrawViewmodel",
            },
            new ToggleSetting
            {
                Id = "FlipViewmodels", Title = "Left-handed", Cvar = "cl_flipviewmodels",
                Summary = "Holds the weapon on the left side.", DependsOn = "DrawViewmodel",
            },
            new SliderSetting
            {
                Id = "ViewmodelFov", Title = "Weapon field of view", Cvar = "viewmodel_fov",
                Summary = "Higher values push the weapon further away from the camera.", DependsOn = "DrawViewmodel",
                Min = 54, Max = 90, Default = 54, Unit = "°",
            }),

        Category("performance", "Performance", "Trade effects for frames. Turn things off to gain FPS.",
            new SliderSetting
            {
                Id = "FpsMax", Title = "Frame rate cap", Cvar = "fps_max", Tags = Perf,
                Summary = "Highest frame rate TF2 will render. A cap a little above your refresh rate keeps input smooth.",
                Min = 0, Max = 1000, Default = 300, Unit = "fps", SpecialValues = new Dictionary<double, string> { [0] = "Unlimited" },
            },
            new ToggleSetting
            {
                Id = "MatPhong", Title = "Shiny models", Cvar = "mat_phong", Default = true, Tags = Perf | Looks,
                Summary = "Phong lighting on players and weapons. Off gives the flat competitive look.",
            },
            new ToggleSetting
            {
                Id = "MatSpecular", Title = "Reflections", Cvar = "mat_specular", Default = true, Tags = Perf | Looks,
                Summary = "Specular highlights on glass, water and metal.",
            },
            new ToggleSetting
            {
                Id = "MatBumpmap", Title = "Surface detail", Cvar = "mat_bumpmap", Default = true, Tags = Perf | Looks,
                Summary = "Bump maps that give walls and models depth.",
            },
            new ToggleSetting
            {
                Id = "Bloom", Title = "Bloom", Cvar = "mat_disable_bloom", Default = true, Tags = Perf | Looks,
                Summary = "Soft glow around bright areas.",
                Emitter = Lines(["mat_disable_bloom 0"], ["mat_disable_bloom 1"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "MotionBlur", Title = "Motion blur", Cvar = "mat_motion_blur_enabled", Tags = Perf | Looks,
                Summary = "Blurs the screen while turning quickly.",
            },
            new ToggleSetting
            {
                Id = "Ragdolls", Title = "Ragdolls", Cvar = "cl_ragdoll_physics_enable", Default = true, Tags = Perf | Looks,
                Summary = "Bodies fall with physics. Off makes them vanish on death.",
                Emitter = Lines(
                    ["cl_ragdoll_physics_enable 1", "cl_ragdoll_fade_time 15", "cl_ragdoll_forcefade 0"],
                    ["cl_ragdoll_physics_enable 0", "cl_ragdoll_fade_time 0", "cl_ragdoll_forcefade 1",
                     "g_ragdoll_fadespeed 0", "g_ragdoll_lvfadespeed 0", "ragdoll_sleepaftertime 0"]),
            },
            new ToggleSetting
            {
                Id = "RemoveGibs", Title = "Gibs", Cvar = "cl_burninggibs", Inverted = true, Tags = Perf | Looks,
                Summary = "Body parts that fly from explosive deaths.",
                Emitter = Lines(["cl_burninggibs 0", "violence_agibs 0", "violence_hgibs 0"],
                                ["cl_burninggibs 1", "violence_agibs 1", "violence_hgibs 1"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "RemoveSprays", Title = "Player sprays", Cvar = "cl_spraydisable", Inverted = true, Default = true, Tags = Perf,
                Summary = "Images other players spray on walls.", Keywords = ["spray"],
            },
            new ToggleSetting
            {
                Id = "DisableDecals", Title = "Decals", Cvar = "r_decals", Inverted = true, Tags = Perf | Looks,
                Summary = "Bullet holes, scorch marks and blood on surfaces.",
                Emitter = Lines(["r_decals 0", "mp_decals 0"], ["r_decals 2048", "mp_decals 200"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "DisableWeather", Title = "Weather", Cvar = "tf_particles_disable_weather", Inverted = true, Tags = Perf | Looks,
                Summary = "Rain and snow particles on maps that have them.",
            },
            new ToggleSetting
            {
                Id = "DisableJiggleBones", Title = "Jiggle bones", Cvar = "cl_jiggle_bone_framerate_cutoff", Inverted = true, Tags = Perf,
                Summary = "Wobble physics on hats and cosmetics.",
                Emitter = Lines(["cl_jiggle_bone_framerate_cutoff 0"], ["cl_jiggle_bone_framerate_cutoff 20"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "DisableFacialAnims", Title = "Facial animations", Cvar = "r_eyemove", Inverted = true, Tags = Perf,
                Summary = "Blinking, eye movement and facial expressions.",
                Emitter = Lines(["r_eyemove 0", "blink_duration 0", "r_eyegloss 0"],
                                ["r_eyemove 1", "blink_duration 0.2", "r_eyegloss 1"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "DisableDynamicLights", Title = "Dynamic lights", Cvar = "r_dynamic", Inverted = true, Tags = Perf | Looks,
                Summary = "Light from muzzle flashes, explosions and flames.",
                Emitter = Lines(["r_dynamic 0"], ["r_dynamic 1"]),
                Parser = raw => raw.Trim() == "0",
            },
            new ToggleSetting
            {
                Id = "SoftParticlesOff", Title = "Soft particles", Cvar = "-softparticlesdefaultoff", Inverted = true, Tags = Launch | Perf,
                Summary = "Smoke and fire blend smoothly into walls. Takes effect after a restart.",
            },
            new SliderSetting
            {
                Id = "DetailDistance", Title = "Grass and clutter distance", Cvar = "cl_detaildist", Tags = Perf | Adv,
                Summary = "How far small props such as grass are drawn.", Min = 0, Max = 2000, Step = 50, Default = 1200,
            },
            new ChoiceSetting
            {
                Id = "ModelLod", Title = "Model detail", Cvar = "r_lod", Tags = Perf | Adv,
                Summary = "Lower detail uses simpler models at every distance.",
                Options = [new("Automatic", "-1"), new("High", "0"), new("Medium", "1"), new("Low", "2")], Default = "-1",
            },
            new ChoiceSetting
            {
                Id = "QueueMode", Title = "Multicore rendering", Cvar = "mat_queue_mode", Tags = Perf | Adv,
                Summary = "Spreads rendering across CPU cores. Change only if TF2 crashes or stutters.",
                Options = [new("Automatic", "-1"), new("Single thread", "0"), new("Queued", "1"), new("Multithreaded", "2")], Default = "-1",
            },
            new ChoiceSetting
            {
                Id = "AntiAliasing", Title = "Anti-aliasing", Cvar = "mat_antialias", Tags = Perf | Looks | Adv,
                Summary = "Smooths jagged edges at a GPU cost.",
                Options = [new("Off", "0"), new("2×", "2"), new("4×", "4"), new("8×", "8")], Default = "0",
            },
            new ChoiceSetting
            {
                Id = "AnisotropicFiltering", Title = "Texture filtering", Cvar = "mat_forceaniso", Tags = Looks | Adv,
                Summary = "Keeps floors sharp at steep angles. Nearly free on modern GPUs.",
                Options = [new("Bilinear", "1"), new("2×", "2"), new("4×", "4"), new("8×", "8"), new("16×", "16")], Default = "1",
            },
            new ToggleSetting
            {
                Id = "VSync", Title = "Vertical sync", Cvar = "mat_vsync", Tags = Adv,
                Summary = "Removes tearing but adds input delay.",
            }),

        Category("audio", "Audio", "Voice chat and sound engine options.",
            new SliderSetting
            {
                Id = "VoiceScale", Title = "Voice chat volume", Cvar = "voice_scale",
                Summary = "Volume of other players' voices.", Min = 0, Max = 2, Step = 0.05, Decimals = 2, Default = 1,
            },
            new ToggleSetting
            {
                Id = "ClosedCaptions", Title = "Subtitles", Cvar = "closecaption",
                Summary = "Shows subtitles for character dialogue.",
                Emitter = Lines(["closecaption 1", "cc_subtitles 1"], ["closecaption 0"]),
            },
            new ToggleSetting
            {
                Id = "AsyncSound", Title = "Background sound mixing", Cvar = "snd_mix_async", Tags = Perf | Adv,
                Summary = "Mixes audio on its own thread, which helps on most CPUs.",
            }),

        Category("network", "Network", "Rates and interpolation. Defaults suit almost everyone.",
            new SliderSetting
            {
                Id = "UpdateRate", Title = "Updates per second", Cvar = "cl_updaterate", Tags = Adv,
                Summary = "Snapshots you ask the server for. 66 matches Valve servers.", Min = 10, Max = 66, Default = 20,
            },
            new SliderSetting
            {
                Id = "CmdRate", Title = "Commands per second", Cvar = "cl_cmdrate", Tags = Adv,
                Summary = "Input packets you send to the server.", Min = 10, Max = 66, Default = 30,
            },
            new SliderSetting
            {
                Id = "Rate", Title = "Bandwidth limit", Cvar = "rate", Tags = Adv, Unit = "B/s",
                Summary = "Most data per second the server may send you.", Min = 10000, Max = 1048576, Step = 1000, Default = 196608,
            },
            new SliderSetting
            {
                Id = "InterpRatio", Title = "Interpolation ratio", Cvar = "cl_interp_ratio", Tags = Adv,
                Summary = "1 is snappier, 2 hides packet loss better.", Min = 1, Max = 5, Default = 2,
            },
            new SliderSetting
            {
                Id = "Interp", Title = "Interpolation delay", Cvar = "cl_interp", Tags = Adv, Unit = "s",
                Summary = "Extra delay used to smooth movement. 0 lets the ratio decide.",
                Min = 0, Max = 0.5, Step = 0.0001, Decimals = 4, Default = 0.1,
            },
            new ToggleSetting
            {
                Id = "SmoothEnabled", Title = "Smooth prediction errors", Cvar = "cl_smooth", Default = true, Tags = Adv,
                Summary = "Eases the camera back when the server corrects your position.",
            }),

        Category("demos", "Demo Recording", "Record matches automatically for highlights and reviews.",
            new ChoiceSetting
            {
                Id = "DemoSupport", Title = "Record automatically", Cvar = "ds_enable",
                Summary = "Which matches get recorded without typing record in the console.",
                Options = [new("Never", "0"), new("Competitive matches", "1"), new("All matches", "2"), new("Tournament matches", "3")],
                Default = "0", Keywords = ["record", "replay"],
            },
            new TextSetting
            {
                Id = "DemoDir", Title = "Folder", Cvar = "ds_dir", Default = "Demos",
                Summary = "Folder inside tf/ that receives the recordings.", DependsOn = "DemoSupport", MaxLength = 48,
            },
            new ToggleSetting
            {
                Id = "DemoSound", Title = "Recording sound", Cvar = "ds_sound", Default = true,
                Summary = "Plays a sound when recording starts or stops.", DependsOn = "DemoSupport",
            },
            new ChoiceSetting
            {
                Id = "DemoNotify", Title = "Recording messages", Cvar = "ds_notify", DependsOn = "DemoSupport",
                Summary = "Where recording messages appear.",
                Options = [new("Console only", "0"), new("Chat", "1"), new("HUD", "2")], Default = "0",
            },
            new ToggleSetting
            {
                Id = "DemoAutoDelete", Title = "Delete uneventful demos", Cvar = "ds_autodelete", DependsOn = "DemoSupport",
                Summary = "Removes recordings that have no bookmarks or killstreaks.",
            }),

        Category("startup", "Startup", "Launch options that change how TF2 boots.",
            new ToggleSetting
            {
                Id = "SkipIntro", Title = "Skip intro video", Cvar = "-novid", Tags = Launch,
                Summary = "Goes straight to the main menu.",
            },
            new ToggleSetting
            {
                Id = "DisableJoystick", Title = "Ignore controllers", Cvar = "-nojoy", Tags = Launch,
                Summary = "Skips controller support. Saves a little memory if you only use mouse and keyboard.",
            },
            new ToggleSetting
            {
                Id = "DisableReplay", Title = "Disable replays", Cvar = "-noreplay", Tags = Launch | Perf,
                Summary = "Turns off the replay system for slightly faster loads.",
            },
            new ToggleSetting
            {
                Id = "NoTextureStream", Title = "Load all textures up front", Cvar = "-no_texture_stream", Tags = Launch,
                Summary = "Avoids blurry textures popping in, at the cost of memory and load time.",
            },
            new ToggleSetting
            {
                Id = "HighPriority", Title = "High process priority", Cvar = "-high", Tags = Launch | SettingTags.WindowsOnly,
                Summary = "Asks Windows to favour TF2 over background apps.",
            }),
    ];
}

/// <summary>
/// Classic null-movement script. Turning it off has to restore WASD, because
/// TF2 saves binds to config.cfg and the aliases disappear with the script.
/// </summary>
internal static class NullMovement
{
    public static readonly string[] On =
    [
        "alias +mfwd \"-back;+forward;alias checkfwd +forward\"",
        "alias +mback \"-forward;+back;alias checkback +back\"",
        "alias +mleft \"-moveright;+moveleft;alias checkleft +moveleft\"",
        "alias +mright \"-moveleft;+moveright;alias checkright +moveright\"",
        "alias -mfwd \"-forward;checkback;alias checkfwd none\"",
        "alias -mback \"-back;checkfwd;alias checkback none\"",
        "alias -mleft \"-moveleft;checkright;alias checkleft none\"",
        "alias -mright \"-moveright;checkleft;alias checkright none\"",
        "alias checkfwd none",
        "alias checkback none",
        "alias checkleft none",
        "alias checkright none",
        "alias none \"\"",
        "bind w +mfwd",
        "bind s +mback",
        "bind a +mleft",
        "bind d +mright",
    ];

    public static readonly string[] Off = ["bind w +forward", "bind s +back", "bind a +moveleft", "bind d +moveright"];

    public static IEnumerable<string> Emit(bool enabled) => enabled ? On : Off;

    public static bool? Detect(string cfgText)
    {
        if (cfgText.Contains("alias +mfwd", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }
}
