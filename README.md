![Banner](https://i.postimg.cc/pXCpTvmx/banner2.png)

# Eternal TF2 Launcher

A Team Fortress 2 launcher for Windows and Linux. It manages your mods, loads them in Casual, explains TF2's settings and shows your backpack with community prices.

[Download](../../releases/latest) · [Website](https://holid4y.github.io/EternalTF2Launcher/) · [Discord](https://discord.gg/CD8mUDDuHr)

Mods are loaded by [casual-pre-loader](https://github.com/cueki/casual-pre-loader), made by cueki and its contributors and bundled here without changes. If the preloader is all you need, use the original project.

## Features

- **Mods:** drag in a VPK, folder, ZIP, RAR or 7z. When two mods change the same file, the one higher in the list wins.
- **GameBanana:** browse, search and install TF2 mods and sounds from inside the launcher.
- **Hit and kill sounds:** turn your own audio files into hit and kill sounds that work in Casual.
- **Config:** every setting explained, binds set by pressing the key, and profiles you can share as a code.
- **Servers:** community servers near you, with servers that fake their player count flagged.
- **Backpack:** your public inventory with backpack.tf and Steam Market prices.
- **Screenshots:** crop, blur, annotate and upload the screenshots Steam keeps for TF2.
- **Themes:** eight themes, any accent colour and custom backgrounds.
- **Updates:** new versions download in the background and install when you quit or restart.

## Install

Get the latest version from the [releases page](../../releases/latest). Release builds include .NET and the mod engine, and admin rights aren't needed.

**Windows:** run `EternalLauncher-<version>-win-x64-setup.exe`, or extract the portable zip and run `EternalLauncher.exe`.

**Linux:**

```sh
tar xf EternalLauncher-*-linux-x64.tar.gz
cd EternalLauncher && ./install.sh
```

Run `./install.sh --remove` to uninstall. Your settings and mods are kept. Custom props, robots, pickups and flags need `wine` on Linux.

## How it works

Your mods are packed into VPKs in `tf/custom` and the preloader sets them up before the game starts. TF2 is launched through Steam and its executables aren't changed. Nothing is written while the game is running, and an interrupted build is detected and rolled back on the next start.

Casual still enforces Valve's whitelist. Most visual content gets through, including particles, skins, war paints, lightwarps, map textures, props and HUDs. Full player model replacements often revert to the default model.

If you start TF2 from Steam instead of the launcher, copy the line from Config, Launch Options into TF2's launch options. Restore vanilla takes every mod out of the game and keeps your library.

## Your files and settings

The first time it opens, the launcher lists what's already in `tf/custom` and `tf/cfg` and asks what to do with each item. It writes nothing to the game until you've answered.

Settings you change are written to one marked block at the end of `autoexec.cfg`, and the rest of the file is left alone. The previous version is kept as `autoexec.cfg.eternal-backup`. With mastercomfig installed, the block goes to `cfg/overrides/autoexec.cfg`.

| | Windows | Linux |
|---|---|---|
| Settings and profiles | `%APPDATA%\Eternal TF2 Launcher` | `~/.config/eternal-launcher` |
| Mod library and logs | `%LOCALAPPDATA%\Eternal TF2 Launcher` | `~/.local/share/eternal-launcher` |
| Caches | `%LOCALAPPDATA%\Eternal TF2 Launcher\cache` | `~/.cache/eternal-launcher` |

The portable build keeps everything in a `userdata` folder next to the executable.

## Support

Questions and help are on [Discord](https://discord.gg/CD8mUDDuHr). To report a bug, open an [issue](../../issues) and attach `logs/launcher.log`.

## Building from source

You need the .NET 10 SDK, and Python 3.12 or newer for the engine.

```sh
dotnet run --project src/Eternal.App
dotnet test --project tests/Eternal.Core.Tests
python -m pip install -r engine/requirements.txt
python -m unittest discover -s engine/tests
```

To build release packages:

```sh
python -m pip install pyinstaller
python scripts/package.py --rid linux-x64 --freeze-engine
python scripts/package.py --rid win-x64 --freeze-engine --portable   # on Windows
iscc installer/installer.iss                                         # Windows installer
```

Pushing a `v*` tag builds both packages and the Windows installer.

```
src/Eternal.Core            settings, autoexec, profiles, Steam, mods, inventory, pricing
src/Eternal.App             Avalonia UI
engine/eternal_engine       the launcher's adapter over the preloader
engine/vendor               casual-pre-loader, unmodified (UPSTREAM has the commit)
packaging, installer        Linux archive and Windows installer
```

`engine/vendor` is never edited by hand. To update it, run `python scripts/sync-preloader.py` and then the engine tests.

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
