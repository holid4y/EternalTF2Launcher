using LauncherTF2.Models;
using LauncherTF2.Models.Settings;

namespace LauncherTF2.Services;

/// <summary>
/// Declarative source of truth for every TF2-cvar-backed setting the launcher
/// exposes. Add a new setting here and it shows up in both the UI (via
/// SettingsViewModel → ItemsControl) *and* the generated autoexec.cfg with
/// zero XAML / writer edits.
///
/// Each <see cref="SettingItem"/> wrapper holds a getter/setter pointing at a
/// real <see cref="SettingsModel"/> property — the underlying JSON shape is
/// untouched so old settings.json files keep loading. New properties added
/// to the model fall back to safe defaults during deserialization.
///
/// Categories returned here are scrolled-to by the sidebar via their
/// <see cref="SettingCategory.Id"/>; that id also doubles as the autoexec
/// section comment when the writer emits the managed block.
///
/// Conventions used below:
///   - <c>NotCasualCompatible = true</c> marks settings that won't take effect
///     on sv_pure servers (TF2 Casual + most pubs). The UI shows a chip.
///   - <c>DependsOn / IsEnabledPredicate</c> wires a child row to a parent
///     toggle (e.g. Medic autocall threshold lights up only when Medic
///     autocall is on).
/// </summary>
internal static class SettingsSchema
{
    public static IReadOnlyList<SettingCategory> Build(SettingsModel m) =>
    [
        BuildGameplay(m),
        BuildHud(m),
        BuildCompetitive(m),
        BuildPerformance(m),
        BuildAudio(m),
        BuildViewmodels(m),
        BuildNetwork(m),
        BuildDemo(m),
        BuildAdvanced(m),
    ];

    // ─────────────────────────── Gameplay ──────────────────────────────
    private static SettingCategory BuildGameplay(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "gameplay",
            Title = "Gameplay",
            Description = "Core in-game feel — input, feedback, weapon switching.",
            AutoexecLabel = "Gameplay",
        };

        cat.Items.Add(new ToggleSetting(m, nameof(m.HudFastSwitch), () => m.HudFastSwitch, v => m.HudFastSwitch = v)
        {
            Title = "Fast weapon switch", Cvar = "hud_fastswitch",
            Description = "Skip the inventory popup when selecting a weapon slot.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.MinViewmodels), () => m.MinViewmodels, v => m.MinViewmodels = v)
        {
            Title = "Minimal viewmodels", Cvar = "tf_use_min_viewmodels",
            Description = "Smaller, less obtrusive weapon viewmodels.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.AutoReload), () => m.AutoReload, v => m.AutoReload = v)
        {
            Title = "Auto reload", Cvar = "cl_autoreload",
            Description = "Reload weapons automatically when not firing.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DamageNumbers), () => m.DamageNumbers, v => m.DamageNumbers = v)
        {
            Title = "Damage numbers", Cvar = "hud_combattext",
            Description = "Floating damage numbers above hit targets.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.HitSound), () => m.HitSound, v => m.HitSound = v)
        {
            Title = "Hit sound", Cvar = "tf_dingalingaling",
            Description = "Plays a sound when you deal damage. You can install custom hitsounds using the Mods tab.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.KillSound), () => m.KillSound, v => m.KillSound = v)
        {
            Title = "Kill sound", Cvar = "tf_dingalingaling_lasthit",
            Description = "Plays a separate sound on the killing blow. You can install custom killsounds using the Mods tab.",
            DependsOn = [nameof(m.HitSound)],
            IsEnabledPredicate = () => m.HitSound,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.MouseSensitivity), () => m.MouseSensitivity, v => m.MouseSensitivity = v)
        {
            Title = "Mouse sensitivity", Cvar = "sensitivity",
            Description = "In-game mouse sensitivity multiplier.",
            Min = 0.1, Max = 20, Step = 0.1, IsInteger = false, DisplayFormat = "0.0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.RawInput), () => m.RawInput, v => m.RawInput = v)
        {
            Title = "Raw input", Cvar = "m_rawinput",
            Description = "Bypass OS mouse acceleration / smoothing.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.CustomAccel),
            () => m.CustomAccel == 0, v => m.CustomAccel = v ? 0 : 1)
        {
            Title = "Disable mouse acceleration", Cvar = "m_customaccel",
            Description = "Force m_customaccel 0 (recommended for aiming).",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.AutoRezoom), () => m.AutoRezoom, v => m.AutoRezoom = v)
        {
            Title = "Sniper auto rezoom", Cvar = "cl_autorezoom",
            Description = "Re-zoom automatically after a sniper shot.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.ClosedCaptions), () => m.ClosedCaptions, v => m.ClosedCaptions = v)
        {
            Title = "Closed captions", Cvar = "closecaption",
            Description = "Show subtitles and sound-effect captions.",
            CustomEmitter = on => on
                ? ["closecaption 1", "cc_subtitles 1"]
                : ["closecaption 0", "cc_subtitles 0"],
        });

        return cat;
    }

    // ─────────────────── HUD & Quality of Life ─────────────────────────
    private static SettingCategory BuildHud(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "hud",
            Title = "HUD & QoL",
            Description = "Scoreboard, MOTD, HUD display tweaks, and pop-up removal.",
            AutoexecLabel = "HUD",
        };

        cat.Items.Add(new ToggleSetting(m, nameof(m.ScoreboardPingAsText), () => m.ScoreboardPingAsText, v => m.ScoreboardPingAsText = v)
        {
            Title = "Scoreboard ping as numbers", Cvar = "tf_scoreboard_ping_as_text",
            Description = "Shows actual ping values on the scoreboard instead of vague connection-quality bars.",
            SearchKeywords = ["latency", "ms", "connection"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisableHtmlMotd), () => m.DisableHtmlMotd, v => m.DisableHtmlMotd = v)
        {
            Title = "Disable HTML MOTD", Cvar = "cl_disablehtmlmotd",
            Description = "Blocks the HTML message-of-the-day screen. Prevents video ads that some community servers inject.",
            SearchKeywords = ["motd", "ads", "message of the day"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.HudPlayerClassModel), () => m.HudPlayerClassModel, v => m.HudPlayerClassModel = v)
        {
            Title = "Player class 3D model on HUD", Cvar = "cl_hud_playerclass_use_playermodel",
            Description = "Shows a 3D character model in the bottom-left HUD corner. Disable for a minor FPS gain.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.ColorblindAssist), () => m.ColorblindAssist, v => m.ColorblindAssist = v)
        {
            Title = "Colorblind assist", Cvar = "tf_colorblindassist",
            Description = "Adds a floating icon above players affected by Jarate or Mad Milk so the debuff is visible without relying on color.",
            SearchKeywords = ["jarate", "milk", "accessibility"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.SniperFullChargeBell), () => m.SniperFullChargeBell, v => m.SniperFullChargeBell = v)
        {
            Title = "Sniper full-charge bell", Cvar = "tf_sniper_fullcharge_bell",
            Description = "Plays a subtle bell when your Sniper Rifle reaches 100 % charge while scoped.",
            SearchKeywords = ["sniper", "scope", "charge", "audio"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.SimpleDisguiseMenu), () => m.SimpleDisguiseMenu, v => m.SimpleDisguiseMenu = v)
        {
            Title = "Fast disguise menu (Spy)", Cvar = "tf_simple_disguise_menu",
            Description = "Spy: use a compact 3-option menu rather than stretching across all 9 class slots.",
            SearchKeywords = ["spy", "disguise"],
        });

        // Pop-up removal
        cat.Items.Add(new ToggleSetting(m, nameof(m.AskBlacklistOptOut), () => m.AskBlacklistOptOut, v => m.AskBlacklistOptOut = v)
        {
            Title = "Disable blacklist pop-up", Cvar = "cl_ask_blacklist_opt_out",
            Description = "Suppresses the dialog asking whether to blacklist a server after disconnecting.",
            SearchKeywords = ["popup", "blacklist", "notification"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.AskFavoriteOptOut), () => m.AskFavoriteOptOut, v => m.AskFavoriteOptOut = v)
        {
            Title = "Disable favorite server pop-up", Cvar = "cl_ask_favorite_opt_out",
            Description = "Suppresses the dialog asking whether to favorite a server after leaving it.",
            SearchKeywords = ["popup", "favorite", "notification"],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.RomeVisionSkipPrompt), () => m.RomeVisionSkipPrompt, v => m.RomeVisionSkipPrompt = v)
        {
            Title = "Skip Romevision prompt", Cvar = "tf_romevision_skip_prompt",
            Description = "Skips the Romevision sharing prompt in Mann vs. Machine.",
            SearchKeywords = ["mvm", "mann vs machine", "romevision"],
        });

        return cat;
    }

    // ────────────────────────── Competitive ────────────────────────────
    private static SettingCategory BuildCompetitive(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "competitive",
            Title = "Competitive",
            Description = "Tournament-grade visibility and awareness tweaks.",
            AutoexecLabel = "Competitive",
        };

        cat.Items.Add(new ToggleSetting(m, nameof(m.MedicAutocall), () => m.MedicAutocall, v => m.MedicAutocall = v)
        {
            Title = "Medic autocall", Cvar = "hud_medicautocallers",
            Description = "Show outlines on injured teammates.",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.MedicAutocallThreshold), () => m.MedicAutocallThreshold, v => m.MedicAutocallThreshold = (int)v)
        {
            Title = "Autocall threshold", Cvar = "hud_medicautocallersthreshold",
            Description = "HP percentage that triggers an autocall.",
            Min = 0, Max = 150, Step = 1, IsInteger = true, DisplayFormat = "0",
            DependsOn = [nameof(m.MedicAutocall)],
            IsEnabledPredicate = () => m.MedicAutocall,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.NullMovement), () => m.NullMovement, v => m.NullMovement = v)
        {
            Title = "Null-movement script", Cvar = "alias",
            Description = "Cancels conflicting WASD presses for crisp direction changes. Rewrites WASD binds.",
            CustomEmitter = on => on
                ? [
                    "// null-movement script",
                    "alias +mfwd  \"-back;+forward;alias checkfwd +forward\"",
                    "alias +mback \"-forward;+back;alias checkback +back\"",
                    "alias +mleft \"-moveright;+moveleft;alias checkleft +moveleft\"",
                    "alias +mright\"-moveleft;+moveright;alias checkright +moveright\"",
                    "alias -mfwd  \"-forward;checkback;alias checkfwd none\"",
                    "alias -mback \"-back;checkfwd;alias checkback none\"",
                    "alias -mleft \"-moveleft;checkright;alias checkleft none\"",
                    "alias -mright\"-moveright;checkleft;alias checkright none\"",
                    "alias none   \"\"",
                    "bind w +mfwd; bind s +mback; bind a +mleft; bind d +mright",
                ]
                : [],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.TransparentViewmodels), () => m.TransparentViewmodels, v => m.TransparentViewmodels = v)
        {
            Title = "Transparent viewmodels", Cvar = "r_drawviewmodel",
            Description = "Requires a transparent-viewmodel HUD (e.g. m0rehud). Otherwise has no effect.",
            CustomEmitter = on => on ? ["cl_first_person_uses_world_model 0", "r_drawviewmodel 1"] : [],
            EmitOnlyWhenOn = true,
        });

        return cat;
    }

    // ────────────────────────── Performance ────────────────────────────
    private static SettingCategory BuildPerformance(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "performance",
            Title = "Performance",
            Description = "Remove cosmetic effects to squeeze out frames.",
            AutoexecLabel = "Performance",
        };



        cat.Items.Add(new SliderSetting(m, nameof(m.FpsMax), () => m.FpsMax, v => m.FpsMax = (int)v)
        {
            Title = "FPS cap (in-game)", Cvar = "fps_max",
            Description = "Maximum frames per second during a match. 0 = uncapped (not recommended — causes input-lag spikes).",
            Min = 0, Max = 500, Step = 1, IsInteger = true, DisplayFormat = "0",
        });
        // fps_max_menu intentionally NOT in schema — this cvar does not exist
        // in TF2 (it belongs to CS:S / HL2). TF2 logs "Unknown command" if emitted.
        cat.Items.Add(new ToggleSetting(m, nameof(m.MatPhong), () => m.MatPhong, v => m.MatPhong = v)
        {
            Title = "Phong shading", Cvar = "mat_phong",
            Description = "Shiny material lighting on weapons + players. Off = flat \"classic comp\" look + a small FPS gain.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.MatSpecular), () => m.MatSpecular, v => m.MatSpecular = v)
        {
            Title = "Specular highlights", Cvar = "mat_specular",
            Description = "Reflective highlights on shiny surfaces (water, glass, weapons). Off saves a few frames.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.MatBumpmap), () => m.MatBumpmap, v => m.MatBumpmap = v)
        {
            Title = "Bump mapping", Cvar = "mat_bumpmap",
            Description = "Surface normal maps that give walls + textures depth. Off makes everything look flat.",
        });

        cat.Items.Add(new ToggleSetting(m, nameof(m.Ragdolls),
            () => !m.Ragdolls, v => m.Ragdolls = !v)
        {
            Title = "Remove ragdolls", Cvar = "cl_ragdoll_physics_enable",
            Description = "Replace death ragdolls with an instant disappear. Takes effect next game launch.",
            // Full mastercomfig-style set — physics_enable alone leaves corpses
            // posed on some 64-bit builds; the fade cvars make them vanish.
            CustomEmitter = removed => removed
                ? ["cl_ragdoll_physics_enable 0", "cl_ragdoll_fade_time 0", "cl_ragdoll_forcefade 1",
                   "g_ragdoll_fadespeed 0", "g_ragdoll_lvfadespeed 0", "ragdoll_sleepaftertime 0"]
                : ["cl_ragdoll_physics_enable 1", "cl_ragdoll_fade_time 15", "cl_ragdoll_forcefade 0"],
            // cl_ragdoll_physics_enable 0 = ragdolls off (Ragdolls=false); 1 = on
            CvarParser = (model, v) => model.Ragdolls = v == "1",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.RemoveGibs), () => m.RemoveGibs, v => m.RemoveGibs = v)
        {
            Title = "Remove gibs", Cvar = "cl_burninggibs",
            Description = "Hide gib + body part particles on death.",
            CustomEmitter = on => on
                ? ["cl_burninggibs 0", "violence_agibs 0", "violence_hgibs 0"]
                : ["cl_burninggibs 1", "violence_agibs 1", "violence_hgibs 1"],
            // cl_burninggibs 0 = gibs removed (RemoveGibs=true); 1 = gibs on
            CvarParser = (model, v) => model.RemoveGibs = v == "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.RemoveSprays), () => m.RemoveSprays, v => m.RemoveSprays = v)
        {
            Title = "Block player sprays", Cvar = "cl_playerspraydisable",
            Description = "Prevents player sprays from rendering. Saves a bit of FPS in busy spaces.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisableJiggleBones), () => m.DisableJiggleBones, v => m.DisableJiggleBones = v)
        {
            Title = "Disable jigglebones", Cvar = "cl_jiggle_bone_framerate_cutoff",
            Description = "Stops wobbly hat / cosmetic physics from animating.",
            CustomEmitter = on => on ? ["cl_jiggle_bone_framerate_cutoff 0"] : ["cl_jiggle_bone_framerate_cutoff 45"],
            // 0 = jigglebones disabled; any non-zero = enabled
            CvarParser = (model, v) => model.DisableJiggleBones = v == "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisableFacialAnims), () => m.DisableFacialAnims, v => m.DisableFacialAnims = v)
        {
            Title = "Disable facial animations", Cvar = "r_eyemove",
            Description = "Disables face flexing, eye tracking, and blink animation.",
            NotCasualCompatible = true,
            CustomEmitter = on => on
                ? ["r_eyemove 0", "blink_duration 0", "r_eyegloss 0"]
                : ["r_eyemove 1", "blink_duration 0.2", "r_eyegloss 1"],
            // r_eyemove 0 = animations disabled; 1 = enabled
            CvarParser = (model, v) => model.DisableFacialAnims = v == "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisableDynamicLights), () => m.DisableDynamicLights, v => m.DisableDynamicLights = v)
        {
            Title = "Disable dynamic lights", Cvar = "r_dynamic",
            Description = "Removes muzzle flashes + explosion / pyro light sources.",
            NotCasualCompatible = true,
            CustomEmitter = on => on ? ["r_dynamic 0"] : ["r_dynamic 1"],
            // r_dynamic 0 = lights disabled; 1 = enabled
            CvarParser = (model, v) => model.DisableDynamicLights = v == "0",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.MotionBlurStrength), () => m.MotionBlurStrength, v => m.MotionBlurStrength = v)
        {
            Title = "Motion blur strength", Cvar = "mat_motion_blur_strength",
            Description = "0 = off. Source's motion blur is heavy — most players keep this at 0.",
            Min = 0, Max = 1, Step = 0.05, IsInteger = false, DisplayFormat = "0.00",
            DefaultValue = 0,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.Bloom), () => m.Bloom, v => m.Bloom = v)
        {
            Title = "Bloom", Cvar = "mat_disable_bloom",
            Description = "Soft glow on bright surfaces. Turning it off saves a few frames.",
            CustomEmitter = on => [$"mat_disable_bloom {(on ? "0" : "1")}"],
            // mat_disable_bloom 0 = bloom ON; 1 = bloom off (inverted semantics)
            CvarParser = (model, v) => model.Bloom = v == "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisablePyroland), () => m.DisablePyroland, v => m.DisablePyroland = v)
        {
            Title = "Disable Pyroland effects", Cvar = "tf_pyrovision_override",
            Description = "Forces Pyrovision off regardless of equipped goggles.",
            NotCasualCompatible = true,
            CustomEmitter = on => on ? ["tf_pyrovision_override -1"] : ["tf_pyrovision_override 0"],
            // -1 = pyroland disabled; 0 = enabled
            CvarParser = (model, v) => model.DisablePyroland = v == "-1",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DisableDecals), () => m.DisableDecals, v => m.DisableDecals = v)
        {
            Title = "Disable decals", Cvar = "r_decals",
            Description = "Bullet holes / blood splatters / explosions — set to 0.",
            // r_drawdecals requires sv_cheats in multiplayer — omitted.
            // r_decals + mp_decals are client-only and work without cheats.
            CustomEmitter = on => on
                ? ["r_decals 0", "mp_decals 0"]
                : ["r_decals 200", "mp_decals 200"],
            // r_decals 0 = decals disabled; non-zero = enabled
            CvarParser = (model, v) => model.DisableDecals = v == "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.SoftParticlesOff), () => m.SoftParticlesOff, v => m.SoftParticlesOff = v)
        {
            Title = "Disable soft particles", Cvar = "-softparticlesdefaultoff",
            Description = "Launch flag — relaunch TF2 to apply. Not a runtime cvar.",
            CustomEmitter = _ => [],
        });

        return cat;
    }

    // ──────────────────────────── Audio ────────────────────────────────
    private static SettingCategory BuildAudio(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "audio",
            Title = "Audio",
            Description = "Hitsound feel, voice chat, and engine sound mixer.",
            AutoexecLabel = "Audio",
        };

        cat.Items.Add(new SliderSetting(m, nameof(m.HitsoundVolume), () => m.HitsoundVolume, v => m.HitsoundVolume = v)
        {
            Title = "Hitsound volume", Cvar = "tf_dingalingaling_volume",
            Description = "How loud the hit / kill sounds play. 0 mutes them entirely.",
            Min = 0, Max = 1, Step = 0.05, IsInteger = false, DisplayFormat = "0.00",
            DependsOn = [nameof(m.HitSound)],
            IsEnabledPredicate = () => m.HitSound,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.VoiceScale), () => m.VoiceScale, v => m.VoiceScale = v)
        {
            Title = "Voice chat volume", Cvar = "voice_scale",
            Description = "Volume of incoming voice chat (independent of Windows mixer).",
            Min = 0, Max = 2, Step = 0.05, IsInteger = false, DisplayFormat = "0.00",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.HitsoundPitchMin), () => m.HitsoundPitchMin, v => m.HitsoundPitchMin = (int)v)
        {
            Title = "Hitsound pitch (low HP)", Cvar = "tf_dingalingaling_pitch_min",
            Description = "Pitch played when the target is near death. 100 = no shift.",
            Min = 50, Max = 200, Step = 1, IsInteger = true, DisplayFormat = "0",
            DependsOn = [nameof(m.HitSound)],
            IsEnabledPredicate = () => m.HitSound,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.HitsoundPitchMax), () => m.HitsoundPitchMax, v => m.HitsoundPitchMax = (int)v)
        {
            Title = "Hitsound pitch (high HP)", Cvar = "tf_dingalingaling_pitch_max",
            Description = "Pitch played when the target is full health. 100 = no shift.",
            Min = 50, Max = 200, Step = 1, IsInteger = true, DisplayFormat = "0",
            DependsOn = [nameof(m.HitSound)],
            IsEnabledPredicate = () => m.HitSound,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.AsyncSound), () => m.AsyncSound, v => m.AsyncSound = v)
        {
            Title = "Async sound mixer", Cvar = "snd_mix_async",
            Description = "Improves audio mixing performance on most systems.",
        });

        return cat;
    }

    // ─────────────────────────── Viewmodels ────────────────────────────
    private static SettingCategory BuildViewmodels(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "viewmodels",
            Title = "Viewmodels",
            Description = "Where the weapon model sits on screen.",
            AutoexecLabel = "Viewmodels",
        };

        cat.Items.Add(new ToggleSetting(m, nameof(m.DrawViewmodel), () => m.DrawViewmodel, v => m.DrawViewmodel = v)
        {
            Title = "Draw viewmodel", Cvar = "r_drawviewmodel",
            Description = "Show the weapon model in first person.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.FlipViewmodels), () => m.FlipViewmodels, v => m.FlipViewmodels = v)
        {
            Title = "Flip viewmodel", Cvar = "cl_flipviewmodels",
            Description = "Mirror the viewmodel to the opposite side of the screen (left-hand mode).",
            DependsOn = [nameof(m.DrawViewmodel)],
            IsEnabledPredicate = () => m.DrawViewmodel,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.ViewmodelFov), () => m.ViewmodelFov, v => m.ViewmodelFov = (int)v)
        {
            Title = "Viewmodel FOV", Cvar = "viewmodel_fov",
            Description = "Higher values pull the model further from the camera.",
            Min = 54, Max = 120, Step = 1, IsInteger = true, DisplayFormat = "0",
            DependsOn = [nameof(m.DrawViewmodel)],
            IsEnabledPredicate = () => m.DrawViewmodel,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.ViewmodelOffsetX), () => m.ViewmodelOffsetX, v => m.ViewmodelOffsetX = v)
        {
            Title = "Offset X (forward/back)", Cvar = "tf_viewmodels_offset_override_x",
            Description = "Push the viewmodel forward (+) or back (-) from the camera.",
            NotCasualCompatible = true,
            Min = -10, Max = 10, Step = 0.1, IsInteger = false, DisplayFormat = "0.0",
            DefaultValue = 0,
            DependsOn = [nameof(m.DrawViewmodel)],
            IsEnabledPredicate = () => m.DrawViewmodel,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.ViewmodelOffsetY), () => m.ViewmodelOffsetY, v => m.ViewmodelOffsetY = v)
        {
            Title = "Offset Y (left/right)", Cvar = "tf_viewmodels_offset_override_y",
            Description = "Push the viewmodel right (+) or left (-) across the screen.",
            NotCasualCompatible = true,
            Min = -10, Max = 10, Step = 0.1, IsInteger = false, DisplayFormat = "0.0",
            DefaultValue = 0,
            DependsOn = [nameof(m.DrawViewmodel)],
            IsEnabledPredicate = () => m.DrawViewmodel,
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.ViewmodelOffsetZ), () => m.ViewmodelOffsetZ, v => m.ViewmodelOffsetZ = v)
        {
            Title = "Offset Z (up/down)", Cvar = "tf_viewmodels_offset_override_z",
            Description = "Push the viewmodel up (+) or down (-) vertically.",
            NotCasualCompatible = true,
            Min = -10, Max = 10, Step = 0.1, IsInteger = false, DisplayFormat = "0.0",
            DefaultValue = 0,
            DependsOn = [nameof(m.DrawViewmodel)],
            IsEnabledPredicate = () => m.DrawViewmodel,
        });

        return cat;
    }

    // ─────────────────────────── Network ───────────────────────────────
    private static SettingCategory BuildNetwork(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "network",
            Title = "Network",
            Description = "Tick rate, interpolation, and prediction tuning. Use presets unless you know what you're doing.",
            AutoexecLabel = "Network",
        };



        cat.Items.Add(new SliderSetting(m, nameof(m.Interp), () => m.Interp, v => m.Interp = v)
        {
            Title = "cl_interp", Cvar = "cl_interp", IsAdvanced = true,
            Description = "Interpolation delay in seconds. Lower = more reactive, more dependent on stable ping.",
            Min = 0, Max = 0.1, Step = 0.001, IsInteger = false, DisplayFormat = "0.000000",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.InterpRatio), () => m.InterpRatio, v => m.InterpRatio = (int)v)
        {
            Title = "cl_interp_ratio", Cvar = "cl_interp_ratio", IsAdvanced = true,
            Description = "Number of interpolation snapshots to keep. 1 = fastest, 2 = forgiving.",
            Min = 1, Max = 3, Step = 1, IsInteger = true, DisplayFormat = "0",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.Rate), () => m.Rate, v => m.Rate = (int)v)
        {
            Title = "rate", Cvar = "rate", IsAdvanced = true,
            Description = "Bandwidth ceiling in bytes/sec the server may send. Higher is generally better.",
            Min = 10000, Max = 786432, Step = 1000, IsInteger = true, DisplayFormat = "0",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.UpdateRate), () => m.UpdateRate, v => m.UpdateRate = (int)v)
        {
            Title = "cl_updaterate", Cvar = "cl_updaterate", IsAdvanced = true,
            Description = "Snapshots per second the server should send you.",
            Min = 10, Max = 128, Step = 1, IsInteger = true, DisplayFormat = "0",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.CmdRate), () => m.CmdRate, v => m.CmdRate = (int)v)
        {
            Title = "cl_cmdrate", Cvar = "cl_cmdrate", IsAdvanced = true,
            Description = "Commands per second you send to the server.",
            Min = 10, Max = 128, Step = 1, IsInteger = true, DisplayFormat = "0",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.Interpolate), () => m.Interpolate, v => m.Interpolate = v)
        {
            Title = "cl_interpolate", Cvar = "cl_interpolate", IsAdvanced = true,
            Description = "Master switch for client-side entity interpolation. Leave on unless debugging.",
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.SmoothEnabled), () => m.SmoothEnabled, v => m.SmoothEnabled = v)
        {
            Title = "cl_smooth", Cvar = "cl_smooth", IsAdvanced = true,
            Description = "Smooth view after prediction errors. Reduces jitter on rubberband.",
        });

        return cat;
    }

    // ─────────────────────── Demo Recording ────────────────────────────
    private static SettingCategory BuildDemo(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "demo",
            Title = "Demo Recording",
            Description = "Automatic demo file recording. Recommended for competitive players and frag movie creators.",
            AutoexecLabel = "Demo",
        };

        cat.Items.Add(new ChoiceSetting(m, nameof(m.DemoSupport), () => m.DemoSupport, v => m.DemoSupport = (int)v!)
        {
            Title = "Recording mode", Cvar = "ds_enable",
            Description = "When demos are recorded automatically. 0 = off, 1 = competitive only, 2 = all matches.",
            SearchKeywords = ["demos", "record", "replay"],
            Options =
            [
                new ChoiceOption("Disabled", 0),
                new ChoiceOption("Competitive matches only", 1),
                new ChoiceOption("All matches", 2),
                new ChoiceOption("Tournament matches only", 3),
            ],
        });
        cat.Items.Add(new TextSetting(m, nameof(m.DemoDir), () => m.DemoDir, v => m.DemoDir = v)
        {
            Title = "Demo folder", Cvar = "ds_dir",
            Description = "Subfolder inside tf/ where demos are saved. Avoid spaces in the name.",
            DependsOn = [nameof(m.DemoSupport)],
            IsEnabledPredicate = () => m.DemoSupport > 0,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DemoSound), () => m.DemoSound, v => m.DemoSound = v)
        {
            Title = "Recording audio cue", Cvar = "ds_sound",
            Description = "Plays a sound when demo recording starts or stops.",
            DependsOn = [nameof(m.DemoSupport)],
            IsEnabledPredicate = () => m.DemoSupport > 0,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DemoNotify), () => m.DemoNotify, v => m.DemoNotify = v)
        {
            Title = "Recording notifications", Cvar = "ds_notify",
            Description = "Outputs recording start/stop events to console and in-game chat.",
            DependsOn = [nameof(m.DemoSupport)],
            IsEnabledPredicate = () => m.DemoSupport > 0,
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.DemoAutoDelete), () => m.DemoAutoDelete, v => m.DemoAutoDelete = v)
        {
            Title = "Auto-delete empty demos", Cvar = "ds_autodelete",
            Description = "Automatically deletes demo files that have no bookmarks or kill-streak events saved to them.",
            DependsOn = [nameof(m.DemoSupport)],
            IsEnabledPredicate = () => m.DemoSupport > 0,
        });

        return cat;
    }

    // ─────────────────────────── Advanced ──────────────────────────────
    private static SettingCategory BuildAdvanced(SettingsModel m)
    {
        var cat = new SettingCategory
        {
            Id = "advanced",
            Title = "Advanced",
            Description = "DirectX, rendering threads, and engine knobs.",
            AutoexecLabel = "Advanced",
        };

        // mat_dxlevel intentionally NOT in autoexec — DxLevel is driven through
        // the `-dxlevel N` launch argument (see SettingsViewModel.SyncLaunchOptions)
        // and surfaced as a dedicated picker in General → Launch behavior.
        // Writing mat_dxlevel into autoexec.cfg corrupts video.txt on next launch.
        cat.Items.Add(new ChoiceSetting(m, nameof(m.QueueMode), () => m.QueueMode, v => m.QueueMode = (int)v!)
        {
            Title = "Multi-core rendering", Cvar = "mat_queue_mode", IsAdvanced = true,
            Description = "2 = multi-threaded (default). Drop to -1/0/1 only if you hit driver crashes.",
            Options =
            [
                new ChoiceOption("2 — multi-thread", 2),
                new ChoiceOption("1 — two threads",  1),
                new ChoiceOption("0 — single",       0),
                new ChoiceOption("-1 — engine pick", -1),
            ],
        });
        cat.Items.Add(new ChoiceSetting(m, nameof(m.AntiAliasing), () => m.AntiAliasing, v => m.AntiAliasing = (int)v!)
        {
            Title = "Anti-aliasing", Cvar = "mat_antialias", IsAdvanced = true,
            Description = "MSAA sample count. Higher = smoother edges, more GPU cost.",
            Options =
            [
                new ChoiceOption("Off",  0),
                new ChoiceOption("2×",   2),
                new ChoiceOption("4×",   4),
                new ChoiceOption("8×",   8),
                new ChoiceOption("16×",  16),
            ],
        });
        cat.Items.Add(new ChoiceSetting(m, nameof(m.AnisotropicFiltering), () => m.AnisotropicFiltering, v => m.AnisotropicFiltering = (int)v!)
        {
            Title = "Anisotropic", Cvar = "mat_forceaniso", IsAdvanced = true,
            Description = "Texture filtering quality at oblique angles. 8× / 16× is essentially free on modern GPUs.",
            Options =
            [
                new ChoiceOption("1×",  1),
                new ChoiceOption("2×",  2),
                new ChoiceOption("4×",  4),
                new ChoiceOption("8×",  8),
                new ChoiceOption("16×", 16),
            ],
        });
        cat.Items.Add(new ToggleSetting(m, nameof(m.VSync), () => m.VSync, v => m.VSync = v)
        {
            Title = "VSync", Cvar = "mat_vsync", IsAdvanced = true,
            Description = "Prevents tearing. Adds 1 frame of input lag — most players keep this off.",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.DetailDistance), () => m.DetailDistance, v => m.DetailDistance = (int)v)
        {
            Title = "Detail distance", Cvar = "cl_detaildist", IsAdvanced = true,
            Description = "How far away ground clutter (grass, debris) draws. Lower = more FPS.",
            Min = 0, Max = 2000, Step = 50, IsInteger = true, DisplayFormat = "0",
        });
        cat.Items.Add(new SliderSetting(m, nameof(m.ModelLod), () => m.ModelLod, v => m.ModelLod = (int)v)
        {
            Title = "Model LOD", Cvar = "r_lod", IsAdvanced = true,
            Description = "Model detail level. -1 = engine pick, 0 = full, 2 = lowest. Lower = more FPS.",
            Min = -1, Max = 2, Step = 1, IsInteger = true, DisplayFormat = "0",
        });

        return cat;
    }
}
