![Banner](https://i.postimg.cc/pXCpTvmx/banner2.png)

# Eternal TF2 Launcher

A launcher for Team Fortress 2 on Windows and Linux. It explains and edits TF2's settings, applies visual mods that work in Casual matchmaking, shows your backpack with community prices, and starts the game through Steam.

Mod preloading is done by [cueki's casual-pre-loader](https://github.com/cueki/casual-pre-loader), bundled without changes. Credit for the preloading engine goes to cueki and its contributors; if you only want the preloader, use the original project.

Mods are packed into VPKs in `tf/custom` before the game starts, which is why they load on Casual servers. The game is started through Steam, with nothing patched and no admin rights needed.

## Features

### Mods

Drop a VPK, a folder or a ZIP, RAR or 7z download anywhere on the window. A moment after each change, the enabled mods are applied to the game. When two mods change the same file, the one higher in the list wins, and each mod shows which files it wins or loses.

When a download holds several versions of a mod (a 54 and a 70 FOV build, say, or a HUD with optional parts), you pick which ones to install; versions that change the same files are marked. Loose files such as a lightwarp or a hitsound with no folders around them are placed by matching their names against the game's own files.

### GameBanana

The Mods tab has a GameBanana browser for TF2 mods and sounds, with every category, search and sorting. Each mod's page shows its pictures, description and files, and you tick the files you want. Downloads are checked against GameBanana's size, MD5 checksum and virus scan before they're installed, and they only come from gamebanana.com. Mods installed from GameBanana show its picture in your library unless you set your own, and both the library and the browser switch between a list and a grid with a size slider.

### Servers

Community servers in your region, with name, game mode, map, players, bots and ping, asked straight from each server. The list comes from GameMonitoring, plus teamwork.tf's community lists with your own teamwork.tf key. Servers that claim more players than their player list shows are tagged and hidden by default, Valve's own servers are left out, and a ping that comes back from a relay near you instead of the server itself is shown as what the distance allows. Join starts TF2 with your mods and goes straight in, or sends the running game there. Favorites and history are shared with the game's own server browser; changes to Steam's files wait until Steam is closed, or for Restart Steam now.

### Screenshots

The TF2 screenshots Steam keeps for your account. Crop, turn, flip, blur or pixelate parts, adjust brightness, contrast and saturation, and add text, arrows and boxes. Saving writes over the same file and thumbnail so Steam can still upload it, and the original is kept for Revert. Upload opens Steam's uploader, Copy link gives the steamcommunity.com page once it's uploaded, and Delete moves the file to the trash.

### Config

Every setting has a short explanation, tags for FPS impact and launch options, and a reset button. See-through weapons work with any HUD: the launcher writes its own folder in `tf/custom` that adds the panel on top of the HUD in effect and turns on the settings the effect needs, without touching the HUD's files. Settings you never change are left to TF2. Binds are set by pressing the key, your own launch options are kept, and the launch line and autoexec block update as you edit. Search finds settings by name or by cvar.

### Profiles

Built-in presets (Competitive, Max Performance, Max Quality, Balanced) plus your own snapshots, which can include the mods you have enabled. Share copies your whole setup as a code (settings, binds, launch options, autoexec lines, and the mod order with each GameBanana file) or saves it as a `.eternal` file that also carries mods that aren't on GameBanana. Importing shows what will change, then downloads the GameBanana mods, installs the packed ones and restores the order. JSON profiles from 2.x and 1.x still import.

### Backpack

Your public TF2 inventory with item art, quality colours and attributes. Prices come from backpack.tf through pricedb.io, in keys and refined, and the Steam Community Market price is shown in your currency. Items sort by value and filter by quality, class or tradability. Renamed items show their new name with the original above it, plus their custom description; with your own Steam Web API key, items with a decal show it.

### Personalisation

Eight themes (Unusual replaced Midnight), any accent colour, your TF2 screenshots or a picture of your own as a blurred background, interface size, compact rows, and which tabs show up in which order.

### Updates

From 2.1.1 on, the launcher checks GitHub for a new release when it starts and every six hours. A new version downloads in the background and installs when you quit, or right away with Restart now. A download is only kept when its size and SHA-256 match what GitHub lists for it, and it can only come from github.com. Both the check and the automatic install can be turned off under Launcher, Updates. Copies that weren't installed with the installer, install.sh or the portable zip only say that a new version is out.

## Install

Download a release from the [Releases](../../releases) page. Release builds include the .NET runtime and the mod engine, so you don't need to install .NET or Python.

### Linux

```sh
tar xf EternalLauncher-2.2.0-linux-x64.tar.gz
cd EternalLauncher && ./install.sh
```

This installs for your user only (`~/.local/share/eternal-launcher`), adds a menu entry and the `eternal-launcher` command. `./install.sh --remove` uninstalls and keeps your settings. QuickPrecache, the preloader step that loads custom props, robots, pickups and flags, compiles with studiomdl, which Linux TF2 doesn't ship; the launcher brings upstream's copy and runs it with Wine, in a prefix of its own under `~/.local/share/eternal-launcher/engine/wine`. Without `wine` installed the step is skipped and the Mods tab says so. Player and weapon models don't need it.

### Windows

Run `EternalLauncher-2.2.0-win-x64-setup.exe` (per-user, no admin rights), or extract the portable zip anywhere and run `EternalLauncher.exe`. The portable build keeps settings and mods in a `userdata` folder next to the executable.

Upgrading from 1.x keeps your settings, profiles, thumbnails and mod library; they are imported on first start.

## How mods reach the game

The launcher keeps your intent (which mods are on, in what order) and a record of what was last written to the game folder. When they differ it asks the engine to rebuild, coalescing quick changes into one build. Builds never run while TF2 is open, and Play waits for a running build to finish instead of starting the game on half-written files. If a build is interrupted (power loss, a crash), the next start detects it and restores vanilla files before building again. A failed build is rolled back and reported.

While a preload is installed, the launcher starts TF2 with `+exec w/config.cfg`, which runs the preloader's setup at boot. If you start TF2 from Steam instead, copy the launch line from Config → Launch Options into TF2's launch options.

Casual servers still enforce Valve's whitelist, but most visual content gets through: particles and unusual effects, weapon and cosmetic skins, war paints, lightwarps, map and world textures, props and HUDs. Full player-model replacements are hit or miss. Their textures usually apply, but the server often forces the vanilla model back. The Mods tab flags models with a known cause of this (an IK-chain mismatch) before you launch.

Your mod library lives in the launcher's data folder, not in `tf/custom`. Restore vanilla turns every mod off and removes the preload while keeping the library.

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

`game.json` holds TF2 settings, `launcher.json` the launcher's own options (and the optional service keys, readable only by you on Linux), `servers.json` your launcher favorites and recent servers, `mods.json` the load order. `steam-writes.json` lists changes waiting for Steam to close, with backups of each Steam file in `steam-backups/`; the originals of edited screenshots are in `screenshot-originals/`. Logs are in `logs/launcher.log` and `logs/engine.log`. A damaged settings file is replaced by its last good copy, or by defaults with the bad file kept aside; the launcher starts either way.

## Building from source

You need the .NET 10 SDK, and Python 3.12 or newer for the engine.

```sh
dotnet run --project src/Eternal.App
dotnet test --project tests/Eternal.Core.Tests
python -m pip install -r engine/requirements.txt
python -m unittest discover -s engine/tests
```

Run from source, the launcher offers a one-click engine setup on the Mods tab that creates a private virtual environment and installs `engine/requirements.txt`. That file pins every package with a hash. To change a dependency, edit `engine/requirements.in` and regenerate it with the `uv` command at the top of that file.

Release packages:

```sh
python -m pip install -r engine/requirements.txt
python -m pip install pyinstaller
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
tests, engine/tests         C# and engine tests
```

### The engine

The launcher runs the engine once per command (`engine/run.py`) and reads one JSON object per line from its stdout: progress events, then a result. Commands that only read are killed if they hang; commands that write, to the mod library or to the game, are never interrupted once started.

The adapter in `engine/eternal_engine` points upstream at the launcher's data and cache folders and journals every install and uninstall. Upstream patches `tf2_misc_dir.vpk` and `gameinfo.txt` in place, so an interrupted build is found on the next start, and the game files are restored before anything else touches them.

It also changes a few things upstream does. The mod list is reversed before installing, because upstream lets later files overwrite earlier ones, so the mod higher in the launcher's list wins, as the conflict report says. Paths inside ZIPs and VPKs are checked before anything is extracted, and a mod being replaced by an import is kept until the new copy is in place. A HUD VPK in `tf/custom` is only deleted after its unpacked copy is complete. Models with a broken header stay out of QuickPrecache, where one would crash TF2 at the main menu, and studiomdl runs without a shell, with a time limit, and only counts as done when it wrote its model. Upstream's JSON files are written to a temporary file and moved into place, so a command that gets stopped can't leave half a file behind. Each install writes `last_install_manifest.json` with the files it put in the game folder.

### Updating the preloader

```sh
python scripts/sync-preloader.py            # latest upstream main
python scripts/sync-preloader.py --ref v2.4.0
```

The script replaces `engine/vendor/casual-pre-loader` with upstream's `core/`, `data/` and `backup/` and records the commit in `UPSTREAM`. Nothing in `vendor/` is edited by hand. The functions the adapter wraps or replaces are set up in `bootstrap.py`, `hud.py` and `precache.py`, and the engine refuses to start if one of them is gone. Run the engine tests after a sync.

## Credits

- [cueki](https://github.com/cueki) and the [casual-pre-loader](https://github.com/cueki/casual-pre-loader) contributors, for the preloading engine this launcher is built around.
- [backpack.tf](https://backpack.tf) for the community prices, served through [pricedb.io](https://pricedb.io).
- [GameBanana](https://gamebanana.com) and its TF2 creators for the mods, sounds and downloads.
- Valve, for TF2 and the Steam Web APIs used for news, inventories and market prices.
- [Deadlock Mod Manager](https://github.com/deadlock-mod-manager/deadlock-mod-manager), whose interrupted-install recovery, conflict model and game guard shaped how mods are applied here.
- Jeremy Tribby and the Barlow Project Authors for the [Barlow](https://github.com/jpt/barlow) typeface.
- Built with [Avalonia](https://avaloniaui.net), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [SharpCompress](https://github.com/adamhathcock/sharpcompress), [valve-parsers](https://pypi.org/project/valve-parsers/) and [PyInstaller](https://pyinstaller.org).

## License

The launcher is MIT licensed (see `LICENSE`). The bundled casual-pre-loader is GPL-3.0 and ships with its own license. The Barlow fonts are under the SIL Open Font License. Not affiliated with Valve.
