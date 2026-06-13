![Banner](https://i.postimg.cc/pXCpTvmx/banner2.png)

# Eternal TF2 Launcher

Eternal is a Windows launcher for Team Fortress 2 built around one goal: giving you full control over TF2's detailed settings and allowing you to run visual mods on Casual servers legitimately and safely. You can manage your settings, use mods on matchmaking, and see your inventory, all from one streamlined interface.

This application acts as a comprehensive manager that safely integrates with your game installation. The preloading capability is powered by a customized, headless implementation of [cueki's Casual Preloader](https://github.com/cueki/casual-pre-loader). All credit for the underlying preloading engine belongs entirely to cueki. If you prefer a standalone preloader without a launcher interface, please check out their original project.

The launcher prepares your mods by baking them into `tf/custom` VPKs. This ensures they load correctly and legitimately when the game engine starts up, allowing them to work in Casual matchmaking without any invasive techniques. The launcher runs entirely unelevated and launches Team Fortress 2 completely normally through the standard Steam protocol.

## Features

*   **Settings:** Manage every useful TF2 cvar in one place, organized by category (Gameplay, HUD & Quality of Life, Competitive, Performance, Audio, Viewmodels, Network, Demo Recording, Advanced). Changes are safely written into a dedicated managed block in your `autoexec.cfg`. If you already have a hand-rolled autoexec, the launcher intelligently reads it on first launch and imports your existing values without altering the rest of your file.
*   **Profiles:** Save, load, and share complete settings snapshots. Four built-in presets (Competitive, Max Performance, Max Quality, Stability) apply optimal settings and the correct DirectX level in one click. You can also save your current setup as a named profile and export it as a JSON file.
*   **Mods:** The Mods tab serves as the engine manager. Drag and drop a VPK, folder, ZIP, RAR, or 7z file to add it to your library. Archives are handled automatically, and newly imported mods are activated right away. Toggling or importing a mod quietly bakes the active set into `tf/custom` in the background, so your mods are ready the moment you finish. Rapid changes are smartly batched into a single process. If TF2 is open, a Sync button appears so you can apply your changes once you close the game. The Remove Preload button restores a completely clean state while keeping your library intact.
*   **Inventory:** Browse your TF2 backpack seamlessly. Pricing from external services like prices.tf and the Steam Market is currently a work in progress.
*   **Home Feed:** View the latest TF2 Steam news and fresh GameBanana mods directly on the landing page.
*   **Themes:** Choose from 10 built-in themes (Eternal Classic, Australium, RED, BLU, Carbon, Midnight, Plasma, Toxic, Synthwave, Minimal). The palette, logo, and window icon update instantly when you switch.

## Requirements

*   Windows 10 version 2004 or later (64-bit)
*   Team Fortress 2 installed via Steam

## Installation

Visit the [Releases](../../releases) page and select the download that suits you best:

### Option A: Portable (Recommended)

1. Download `EternalLauncher-Portable-{version}.zip`
2. Extract the archive anywhere you prefer (such as your Desktop or a games folder)
3. Run `LauncherTF2.exe`

This version requires no installation and bundles the necessary .NET runtime inside the executable.

### Option B: Installer

1. Download `EternalLauncher-WinSetup-{version}.exe`
2. Run the installer (no admin rights are required)
3. The launcher installs to `%LocalAppData%\Programs\Eternal TF2 Launcher` and creates a Start Menu shortcut

This version requires the [.NET 8 Desktop Runtime (x64)](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe) to be installed on your system.

## Launch Sequence and Architecture

Clicking "Launch TF2" starts the game normally through Steam (`steam://rungameid/440`) using your configured launch options. There are no UAC prompts or client patching involved. The launcher simply asks Steam to start the game, then minimizes to the system tray if you have that option enabled. You can right-click the tray icon to restore or quit at any time.

Mods load in Casual matchmaking because they are pre-baked into `tf/custom` by the integrated engine. This happens automatically whenever you import or toggle a mod. Because the preload persists once built, you can launch the game as many times as you like (whether through Steam, a shortcut, or the launcher) and your mods will remain active. If you try to launch while a rebuild is in progress, the launcher will wait for it to finish so the game never starts on a half-built preload.

### Preloader Engine Integration

The launcher includes an edited copy of [cueki's Casual Preloader](https://github.com/cueki/casual-pre-loader) located in the `external/` directory. The original GUI was stripped out and replaced with a headless CLI that the launcher communicates with, along with some defensive improvements like broken-model detection and archive handling. Release builds include this engine next to the launcher in the `preloader/` folder. The CLI protocol is documented in `docs/preloader-integration-contract.md`.

The engine runs on Python 3.10+. The installer version bundles a Python installer and sets it up automatically if your PC does not have it. The portable build cannot install Python automatically, but if it is missing, the Mods tab will show a convenient prompt to download it. If you are running from the source code, you can install the dependencies with `pip install -r external/requirements.txt`.

## Settings and Configuration Handling

The launcher writes only to its explicitly marked section inside `autoexec.cfg`:

```
// === ETERNAL LAUNCHER MANAGED BLOCK — do not edit between these markers ===
...
// === ETERNAL LAUNCHER MANAGED BLOCK END ===
```

Everything outside these boundaries is completely yours and remains untouched. Only settings that differ from the default values are written. If a cvar matches the default it is omitted, keeping the block minimal and avoiding conflicts with your personal config.

DirectX levels are applied as a Steam launch option (`-dxlevel N`) rather than as `mat_dxlevel` in your autoexec. This prevents the well-known issue of corrupting `video.txt` on startup.

## Casual Matchmaking Compatibility

It is important to know that Valve's whitelist still applies in matchmaking, and preloading can only get certain things past it.

*   **Works:** Particles and unusual effects, weapon and cosmetic skins, lightwarps, war paints, map/world textures, props, bot models, and custom HUDs.
*   **Hit or Miss:** Full player-model replacements. The textures usually apply successfully, but the model itself is often forced back to vanilla by the server. This results in visual oddities like resized cosmetics on a normal mercenary model. This is a Source engine limitation, not something the launcher can circumvent.
*   **Not Supported Yet:** Custom sounds and decals are currently pending upstream support in the preloader engine.

Your imported mods live in the preloader's private library, not loose in `tf/custom`. The only thing that lands in the game folder is the final baked preload. Clicking "Remove Preload" restores a clean game state while securely keeping your mod library intact.

## Generated Files

All runtime files live next to the executable or in AppData, and are excluded from the repository:

| File | Purpose |
|---|---|
| `settings.json` | TF2 settings and launch arguments |
| `launcher_config.json` | Theme, tray behavior, and log level preferences |
| `mod_state.json` | Mod enable/disable state tracking |
| `%AppData%\Eternal TF2 Launcher\profiles\user\` | Saved user profiles |
| `price_cache.json` | Market prices (2-hour cache) |
| `tf2_inventory_cache.json` | Backpack data (10-minute cache) |
| `mod_metadata_cache.json` & `mod_thumbnails/` | GameBanana enrichment (7-day cache) |
| `app_debug.log` | Debug logging output |
| `crash_log.txt` | Written automatically on unhandled crashes |

## Building from Source

The .NET 8 SDK is required to build the application.

```powershell
dotnet build src/LauncherTF2/LauncherTF2.csproj -c Debug
dotnet run --project src/LauncherTF2/LauncherTF2.csproj
```

Alternatively, use the included helper scripts:

```powershell
scripts\build.ps1                 # build only
scripts\start.bat                 # build and run
scripts\publish.ps1               # framework-dependent release build (for installer)
scripts\publish-portable.ps1      # self-contained single-file build (for portable zip)
```

## License

MIT License. See the `LICENSE` file for details.
