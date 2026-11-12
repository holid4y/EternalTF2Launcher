![Banner](https://i.postimg.cc/pXCpTvmx/banner2.png)

# Eternal TF2 Launcher

Manage settings, mods, servers and screenshots for Team Fortress 2, TF2 Classified and TF2: Gold Rush in one place. Each game keeps its own setup. Browse GameBanana, tune your settings or find a server, then press Play. Free for Windows and Linux.

[Download](../../releases/latest) · [Website](https://holid4y.github.io/EternalTF2Launcher/) · [Discord](https://discord.gg/CD8mUDDuHr)

For TF2 Casual, mod support is powered by [casual-pre-loader](https://github.com/cueki/casual-pre-loader), made by cueki and its contributors and bundled here unchanged.

## What you can do

- **Switch between three games:** TF2, TF2 Classified and TF2: Gold Rush. Each has its own mods, settings, profiles, servers and theme.
- **Keep your mods in order:** add a VPK, folder, ZIP, RAR or 7z archive. When mods change the same file, the one higher in your list takes priority.
- **Browse GameBanana:** search and install mods and sounds for the game you have selected.
- **Set hit and kill sounds:** use your own audio files in TF2 Casual. Eternal matches them to TF2's volume; each sound has its own slider, and its preview uses the level you'll hear in game.
- **Make settings your own:** get plain-language descriptions, performance notes, key binding and shareable setups.
- **Find a server:** browse community servers, check their map, players and ping, then join from the list.
- **Check your TF2 backpack:** see your public inventory with community and Steam Market prices.
- **Edit Steam screenshots:** crop, blur, annotate and upload screenshots for the selected game.
- **Change the look:** choose from eight themes, set an accent color and use your screenshots as the background.
- **Stay up to date:** Eternal downloads updates in the background and installs them when you quit or choose Restart to update.

## Supported games

Choose a game from the lower-left menu. Its settings, mods, profiles, servers, screenshots and news stay with it; the games' files are kept separate.

| Feature | Team Fortress 2 | TF2 Classified | TF2: Gold Rush |
|---|---|---|---|
| Mods and GameBanana | Yes | Yes | Yes |
| Settings | Full TF2 catalog | Options supported by the game | Options supported by the game |
| Servers, screenshots and news | Yes | Yes | Yes |
| Backpack prices | Yes | No | No |
| Hit and kill sounds | Yes | Not available | Not available |
| See-through weapons | Yes | No | No |

Many visual mods can load in Casual, but Valve's whitelist still applies. Hit and kill sounds, backpack prices and see-through weapons are currently TF2-only. A supported game that isn't installed yet appears in the menu with a link to its Steam page.

## Install

Download the latest release from the [releases page](../../releases/latest). The release builds include .NET and the mod engine, so you don't need to install either one separately. No admin rights are needed.

**Windows:** run `EternalLauncher-<version>-win-x64-setup.exe`, or extract the portable ZIP and open `EternalLauncher.exe`.

**Linux:**

```sh
tar xf EternalLauncher-*-linux-x64.tar.gz
cd EternalLauncher && ./install.sh
```

To uninstall, run `./install.sh --remove`. Your settings and mods stay on your computer. Install Wine on Linux if you want to use custom props, robots, pickups or flags.

## How it works

Your mod library stays in Eternal's data folder. Before the game starts, Eternal packs enabled mods into VPK files in that game's `custom` folder and prepares them with the preloader. The game starts through Steam; its executable isn't patched. Eternal won't change game files while that game is running. If a build is interrupted, Eternal restores the original files the next time it starts.

Valve's Casual whitelist still applies. Many visual mods can appear in Casual, including particles, skins, war paints, lightwarps, map textures, props and HUDs. Full player-model replacements may be reset by the server.

A mod that includes particles alongside models, sounds or scripts can appear as two entries. The preloader handles particle files separately, so you can enable those parts independently.

You can also start a game from Steam. Copy the launch line shown in Config → Launch Options into that game's Steam launch options. **Restore vanilla** removes Eternal's changes from the game and keeps your library ready for next time.

## Your files and settings

The first time you open a game, Eternal checks its `custom` and `cfg` folders and shows you the files it found. Choose what to keep, import into your library or move to a backup. Nothing changes until you decide.

Settings you change go into one marked block at the end of `autoexec.cfg`; the rest of the file stays as it is. Eternal saves a backup as `autoexec.cfg.eternal-backup`. If mastercomfig is installed, the block goes in `cfg/overrides/autoexec.cfg`.

Before you import a shared setup, Eternal shows its launch options, binds and autoexec lines. Those lines replace yours only if you agree. When sharing your own setup, you choose whether your autoexec lines are included.

| | Windows | Linux |
|---|---|---|
| Settings and profiles | `%APPDATA%\Eternal TF2 Launcher` | `~/.config/eternal-launcher` |
| Mod library and logs | `%LOCALAPPDATA%\Eternal TF2 Launcher` | `~/.local/share/eternal-launcher` |
| Caches | `%LOCALAPPDATA%\Eternal TF2 Launcher\cache` | `~/.cache/eternal-launcher` |

The portable build keeps its files in a `userdata` folder beside the executable.

## Support

Ask for help on [Discord](https://discord.gg/CD8mUDDuHr). If something breaks, [open a GitHub issue](../../issues) and attach `logs/launcher.log`.

## Build from source

You'll need the .NET 10 SDK and Python 3.12 or newer for the engine.

```sh
dotnet run --project src/Eternal.App
dotnet test --project tests/Eternal.Core.Tests
python -m pip install -r engine/requirements.txt
python -m unittest discover -s engine/tests
```

To build release packages:

```sh
python -m pip install -r scripts/requirements-build.txt
python scripts/package.py --rid linux-x64 --freeze-engine
python scripts/package.py --rid win-x64 --freeze-engine --portable   # on Windows
iscc installer/installer.iss                                         # Windows installer
```

Pushing a `v*` tag builds the packages and Windows installer.

```
src/Eternal.Core            settings, autoexec, profiles, Steam, mods, inventory, pricing
src/Eternal.App             Avalonia UI
engine/eternal_engine       Eternal's adapter for the preloader
engine/vendor               casual-pre-loader, unmodified (UPSTREAM has the commit)
packaging, installer        Linux archive and Windows installer
```

Never edit `engine/vendor` by hand. To update it, run `python scripts/sync-preloader.py`, then run the engine tests.

## Credits

- [cueki](https://github.com/cueki) and the [casual-pre-loader](https://github.com/cueki/casual-pre-loader) contributors for the preloader.
- [backpack.tf](https://backpack.tf) and [pricedb.io](https://pricedb.io) for community prices.
- [GameBanana](https://gamebanana.com) and its creators for mods, sounds and downloads.
- Valve for TF2 and the Steam APIs used for news, inventories and Market prices.
- [Deadlock Mod Manager](https://github.com/deadlock-mod-manager/deadlock-mod-manager) for ideas on recovery and file conflicts.
- Jeremy Tribby and the Barlow Project Authors for the [Barlow](https://github.com/jpt/barlow) typeface.
- Built with [Avalonia](https://avaloniaui.net), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [SharpCompress](https://github.com/adamhathcock/sharpcompress), [valve-parsers](https://pypi.org/project/valve-parsers/) and [PyInstaller](https://pyinstaller.org).

## License

Eternal is MIT licensed (see `LICENSE`). The bundled casual-pre-loader is GPL-3.0 and includes its own license. Barlow is under the SIL Open Font License. Eternal is not affiliated with Valve.
