![Banner](https://i.postimg.cc/pXCpTvmx/banner2.png)

# Eternal TF2 Launcher

A launcher for Team Fortress 2 on Windows and Linux. It explains and edits TF2's settings, applies visual mods that work in Casual matchmaking, shows your backpack with community prices, and starts the game through Steam.

Mod preloading is done by [cueki's casual-pre-loader](https://github.com/cueki/casual-pre-loader), bundled without changes. Credit for the preloading engine goes to cueki and its contributors; if you only want the preloader, use the original project.

Mods are packed into VPKs in `tf/custom` before the game starts, which is why they load on Casual servers. The game is started through Steam, with nothing patched and no admin rights needed.

## Features

### Mods

Drop a VPK, a folder or a ZIP, RAR or 7z download anywhere on the window. A moment after each change, the enabled mods are applied to the game. When two mods change the same file, the one higher in the list wins, and each mod shows which files it wins or loses. New mods from GameBanana can be imported from the Home tab; downloads are checked against GameBanana's checksum and virus scan.

### Config

Every setting has a short explanation, tags for FPS impact and launch options, and a reset button. Settings you never change are left to TF2. Binds are set by pressing the key, your own launch options are kept, and the launch line and autoexec block update as you edit. Search finds settings by name or by cvar.

### Profiles

Built-in presets (Competitive, Max Performance, Max Quality, Balanced) plus your own snapshots, which can include the mods you have enabled. Profiles export to JSON, and 1.x profiles can be imported.

### Backpack

Your public TF2 inventory with item art, quality colours and attributes. Prices come from backpack.tf through pricedb.io, in keys and refined, and the Steam Community Market price is shown in your currency. Items sort by value and filter by quality, class or tradability.

### Personalisation

Eight themes, any accent colour, your TF2 screenshots or a picture of your own as a blurred background, interface size, compact rows, and which tabs show up in which order.

## Install

Download a release from the [Releases](../../releases) page. Release builds include the .NET runtime and the mod engine, so you don't need to install .NET or Python.

**Linux**

```sh
tar xf EternalLauncher-2.0.0-linux-x64.tar.gz
cd EternalLauncher && ./install.sh
```

This installs for your user only (`~/.local/share/eternal-launcher`), adds a menu entry and the `eternal-launcher` command. `./install.sh --remove` uninstalls and keeps your settings. QuickPrecache, the preloader step that keeps custom models on map props loaded, uses Wine on Linux; without `wine` installed it is skipped and the Mods tab says so.

**Windows**

Run `EternalLauncher-2.0.0-win-x64-setup.exe` (per-user, no admin rights), or extract the portable zip anywhere and run `EternalLauncher.exe`. The portable build keeps settings and mods in a `userdata` folder next to the executable.

Upgrading from 1.x keeps your settings, profiles, thumbnails and mod library; they are imported on first start.

## How mods reach the game

The launcher keeps your intent (which mods are on, in what order) and a record of what was last written to the game folder. When they differ it asks the engine to rebuild, coalescing quick changes into one build. Builds never run while TF2 is open, and Play waits for a running build to finish instead of starting the game on half-written files. If a build is interrupted (power loss, a crash), the next start detects it and restores vanilla files before building again. A failed build is rolled back and reported.

While a preload is installed, the launcher starts TF2 with `+exec w/config.cfg`, which runs the preloader's setup at boot. If you start TF2 from Steam instead, copy the launch line from Config → Launch Options into TF2's launch options.

Casual servers still enforce Valve's whitelist; preloading gets most visual content through:

- **Works:** particles and unusual effects, weapon and cosmetic skins, war paints, lightwarps, map and world textures, props, HUDs.
- **Hit or miss:** full player-model replacements. Textures usually apply but the server often forces the vanilla model. The Mods tab flags models with a known cause (an IK-chain mismatch) before you launch.

Your mod library lives in the launcher's data folder, not in `tf/custom`. **Restore vanilla** turns every mod off and removes the preload while keeping the library.

## Settings and autoexec.cfg

The launcher owns one marked block at the end of `autoexec.cfg` and never touches anything outside it:

```
// === ETERNAL LAUNCHER MANAGED BLOCK — do not edit between these markers ===
...
// === ETERNAL LAUNCHER MANAGED BLOCK END ===
```

Only settings you changed in the launcher are written. Values you never touched are read from TF2's own `config.cfg` and `video.txt`, so the Config tab shows what the game really uses. Saves are atomic, the previous file is kept as `autoexec.cfg.eternal-backup`, and an `autoexec.cfg` that can't be read is never overwritten. With mastercomfig installed the block goes to `cfg/overrides/autoexec.cfg`, which is the file mastercomfig runs.

Cvar names, ranges and defaults were checked against TF2's own binaries and `cfg/user_default.scr`.

## Where files live

| | Windows | Linux |
|---|---|---|
| Settings and profiles | `%APPDATA%\Eternal TF2 Launcher` | `~/.config/eternal-launcher` |
| Mod library, logs, preload state | `%LOCALAPPDATA%\Eternal TF2 Launcher` | `~/.local/share/eternal-launcher` |
| Image, price and backpack caches | `%LOCALAPPDATA%\Eternal TF2 Launcher\cache` | `~/.cache/eternal-launcher` |

`game.json` holds TF2 settings, `launcher.json` the launcher's own options, `mods.json` the load order. Logs are in `logs/launcher.log` and `logs/engine.log`. A damaged settings file is replaced by its last good copy, or by defaults with the bad file kept aside; the launcher starts either way.

## Building from source

Requirements: .NET 10 SDK, and Python 3.12 or newer for the engine.

```sh
dotnet run --project src/Eternal.App
```

Run from source, the launcher offers a one-click engine setup on the Mods tab that creates a private virtual environment and installs `engine/requirements.txt`.

Release packages:

```sh
python -m pip install -r engine/requirements.txt pyinstaller
python scripts/package.py --rid linux-x64 --freeze-engine
python scripts/package.py --rid win-x64 --freeze-engine --portable   # on Windows
iscc installer/installer.iss                                         # Windows installer
```

Pushing a `v*` tag makes the CI build both packages and the Windows installer.

### Layout

```
src/Eternal.Core            settings catalog, autoexec, profiles, Steam, mod coordinator, inventory, pricing
src/Eternal.App             Avalonia UI
engine/eternal_engine       the launcher's adapter over the preloader
engine/vendor               cueki/casual-pre-loader, copied unmodified (UPSTREAM has the commit)
packaging, installer        Linux archive and Windows installer
```

### The engine

The launcher runs the engine once per command (`engine/run.py`) and reads one JSON object per line from its stdout: progress events, then a result. Commands that only read are killed if they hang; commands that write, to the mod library or to the game, are never interrupted once started.

What the adapter adds on top of upstream:

- It points upstream at the launcher's data and cache folders instead of the install folder.
- It journals every install and uninstall. Upstream patches `tf2_misc_dir.vpk` and `gameinfo.txt` in place, so an interrupted build is detected on the next start and the game is restored to vanilla before building again.
- It reverses the mod list before installing, because upstream lets later files overwrite earlier ones. The mod higher in the launcher's list wins, as the conflict report says.
- It checks ZIP entries before extracting, collapses multipart VPK sets and handles short numeric VPK names.
- It keeps models with a broken header out of QuickPrecache, where one would crash TF2 at the main menu, and writes `last_install_manifest.json` with the files each install wrote.
- It flags player models with an IK-chain mismatch, which Casual servers replace with the vanilla model.

### Updating the preloader

```sh
python scripts/sync-preloader.py            # latest upstream main
python scripts/sync-preloader.py --ref v2.4.0
```

The script replaces `engine/vendor/casual-pre-loader` with upstream's `core/`, `data/` and `backup/` and records the commit in `UPSTREAM`. Nothing in `vendor/` is edited by hand. The adapter relies on a few upstream names: `core.config`, `InstallService`, the importer and conflict services, and `manage_folder` and `manage_vpk` in `core/quickprecache/precache_list.py`. Check those after a sync.

## Credits

- [cueki](https://github.com/cueki) and the [casual-pre-loader](https://github.com/cueki/casual-pre-loader) contributors, for the preloading engine this launcher is built around.
- [backpack.tf](https://backpack.tf) for the community prices, served through [pricedb.io](https://pricedb.io).
- [GameBanana](https://gamebanana.com) and its TF2 creators for the mod feed and downloads.
- Valve, for TF2 and the Steam Web APIs used for news, inventories and market prices.
- [Deadlock Mod Manager](https://github.com/deadlock-mod-manager/deadlock-mod-manager), whose interrupted-install recovery, conflict model and game guard shaped how mods are applied here.
- Jeremy Tribby and the Barlow Project Authors for the [Barlow](https://github.com/jpt/barlow) typeface.
- Built with [Avalonia](https://avaloniaui.net), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [SharpCompress](https://github.com/adamhathcock/sharpcompress), [valve-parsers](https://pypi.org/project/valve-parsers/) and [PyInstaller](https://pyinstaller.org).

## License

The launcher is MIT licensed (see `LICENSE`). The bundled casual-pre-loader is GPL-3.0 and ships with its own license. The Barlow fonts are under the SIL Open Font License. Not affiliated with Valve.
