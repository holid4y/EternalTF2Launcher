"""QuickPrecache fixes on top of upstream.

QuickPrecache chains every custom prop and bot model into one $includemodel
list. A model with a garbage header in that chain crashes TF2 at the main menu
("GroupStudioHdr(): invalid index"), so such models are left out and reported
instead.

Upstream also runs studiomdl through a shell on Linux and reports success when
compiles fail; here the compiler gets an argument list, Wine runs in a prefix of
the launcher's own without opening windows, and failures become a build warning.
"""

import logging
import os
import re
import struct
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path

log = logging.getLogger(__name__)

_OFF_NUMBONES = 156
_OFF_NUMTEXTURES = 204
_HEADER = _OFF_NUMTEXTURES + 4
# Garbage reads as huge or negative counts; real TF2 models stay far below this.
_MAX_COUNT = 4096
# The first compile also creates the Wine prefix, which takes a while.
_COMPILE_TIMEOUT = 300
_MODEL_NAME = re.compile(r'^\s*\$modelname\s+"([^"]+)"', re.MULTILINE | re.IGNORECASE)

# Models left out by the last scan and where they were found. Upstream scans twice per install.
skipped: dict[str, str] = {}
# What went wrong while compiling during the last install.
failed: list[str] = []
_installed = False


def reset() -> None:
    skipped.clear()
    failed.clear()


def warnings(owner: Callable[[str], str | None] = lambda model: None) -> list[str]:
    found = []
    if skipped:
        names = [f"{model} ({owner(model) or where})" for model, where in skipped.items()]
        more = f" and {len(names) - 3} more" if len(names) > 3 else ""
        found.append(f"Left {len(names)} broken model{'s' if len(names) != 1 else ''} out of QuickPrecache "
                     f"so TF2 doesn't crash at the menu: {', '.join(names[:3])}{more}. "
                     "If that mod looks wrong in game, turn it off.")
    if failed:
        found.append(f"QuickPrecache didn't finish ({failed[0]}), so custom props, robots, pickups and flags "
                     "may not load. engine.log in the launcher's logs folder has the details.")
    return found


def header_is_broken(data: bytes) -> bool:
    """True only when the header is provably bad; too little data to tell is not."""
    if len(data) < 4:
        return False
    if data[:4] != b"IDST":
        return True
    if len(data) < _HEADER:
        return False
    bones = struct.unpack_from("<i", data, _OFF_NUMBONES)[0]
    textures = struct.unpack_from("<i", data, _OFF_NUMTEXTURES)[0]
    return not (0 <= bones <= _MAX_COUNT and 0 <= textures <= _MAX_COUNT)


def _skip(model: str, where: str) -> None:
    log.warning("left out of QuickPrecache, broken model header: %s in %s", model, where)
    skipped[model] = where


def _check_folder(folder: Path, models: set[str]) -> set[str]:
    # Upstream lowercases paths; mods made on Windows rarely match on-disk case.
    by_lower = None
    kept = set()
    for model in models:
        path = folder / model
        if not path.is_file():
            if by_lower is None:
                by_lower = {p.relative_to(folder).as_posix().lower(): p for p in folder.rglob("*.[mM][dD][lL]")}
            path = by_lower.get(model)
        if path is not None:
            try:
                with path.open("rb") as fd:
                    if header_is_broken(fd.read(_HEADER)):
                        _skip(model, folder.name)
                        continue
            except OSError:
                pass
        kept.add(model)
    return kept


def _check_vpk(vpk_path: Path, models: set[str]) -> set[str]:
    from valve_parsers import VPKFile

    try:
        archive = VPKFile(vpk_path)
    except Exception:
        return models
    kept = set()
    for model in models:
        entry = f"models/{model}"
        try:
            broken = archive.file_exists(entry) and header_is_broken(archive.get_file_data(entry)[:_HEADER])
        except Exception:
            broken = False
        if broken:
            _skip(entry, vpk_path.name)
            continue
        kept.add(model)
    return kept


def _wine_env() -> dict[str, str]:
    from core.config import config

    env = dict(os.environ)
    env.update({
        # Never the player's own ~/.wine.
        "WINEPREFIX": str(config.project_dir / "wine"),
        "WINEDEBUG": "-all",
        # No Mono or Gecko install prompt, and no menu entries for what runs in the prefix.
        "WINEDLLOVERRIDES": "mscoree,mshtml=;winemenubuilder.exe=d",
        # studiomdl is a console tool; with no display Wine opens no windows.
        "DISPLAY": "",
        "WAYLAND_DISPLAY": "",
    })
    return env


def _model_name(qc_file: Path) -> str | None:
    try:
        match = _MODEL_NAME.search(qc_file.read_text(errors="replace"))
    except OSError:
        return None
    return match.group(1) if match else None


def _make_model(self, qc_file: Path) -> bool:
    from core.quickprecache.studio_mdl import StudioMDLVersion

    if self.studio_mdl_version == StudioMDLVersion.MISSING:
        failed.append(f"no studiomdl.exe for {qc_file.name}")
        return False
    exe = str(self.bundled_studiomdl_path if self.studio_mdl_version == StudioMDLVersion.BUNDLED_SDK
              else self.game_path / self.studio_mdl_version.value)
    tf = self.game_path / "tf"
    if sys.platform == "win32":
        args, env = [exe, "-game", str(tf), "-nop4", "-verbose", str(qc_file)], None
    else:
        # Wine maps Z: to /. As a list, nothing in these paths is read by a shell.
        args, env = ["wine", exe, "-game", f"Z:{tf}", "-nop4", "-verbose", f"Z:{qc_file}"], _wine_env()
    log.info("compiling %s", qc_file.name)
    try:
        result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace",
                                env=env, timeout=_COMPILE_TIMEOUT)
    except subprocess.TimeoutExpired:
        failed.append(f"{qc_file.name} was still compiling after {_COMPILE_TIMEOUT} s")
        return False
    except OSError as exc:
        failed.append(f"{qc_file.name}: {exc}")
        return False
    for line in result.stdout.splitlines():
        log.info(line)
    if result.returncode != 0:
        failed.append(f"{qc_file.name} exited with {result.returncode}")
        return False
    # studiomdl can exit cleanly without writing anything when it can't start properly under Wine.
    name = _model_name(qc_file)
    if name and not (tf / "models" / name).is_file():
        failed.append(f"{qc_file.name} wrote no {name}")
        return False
    return True


def install() -> None:
    """make_precache_list and QuickPrecache look these up at call time, so replacing them is enough."""
    global _installed
    if _installed:
        return
    from core.quickprecache import precache_list
    from core.quickprecache.quick_precache import QuickPrecache
    from core.quickprecache.studio_mdl import StudioMDL

    scan_folder, scan_vpk = precache_list.manage_folder, precache_list.manage_vpk
    precache_list.manage_folder = lambda folder: _check_folder(folder, scan_folder(folder))
    precache_list.manage_vpk = lambda vpk_path: _check_vpk(vpk_path, scan_vpk(vpk_path))
    if not callable(getattr(StudioMDL, "make_model", None)):
        raise RuntimeError("casual-pre-loader no longer has StudioMDL.make_model; update eternal_engine/precache.py")
    StudioMDL.make_model = _make_model
    find = StudioMDL.__init__

    def named_find(self, game_path: Path) -> None:
        try:
            find(self, game_path)
        except RuntimeError:
            failed.append("studiomdl.exe wasn't found in the game or the launcher")
            raise

    StudioMDL.__init__ = named_find

    run = QuickPrecache.run

    def checked_run(self, auto: bool = False, list_file: str = "", flush: bool = False) -> bool:
        ok = run(self, auto, list_file, flush)
        if not ok and not failed:
            failed.append("it stopped with an error")
        return ok

    QuickPrecache.run = checked_run
    _installed = True
