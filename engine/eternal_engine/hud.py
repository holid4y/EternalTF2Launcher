"""Converting HUD VPKs in tf/custom without risking the player's files.

Upstream unpacks any HUD VPK that overrides the main menu into a folder, so it
can add the preloader's #base line, and then deletes the VPK whether or not
every file came out. Here the VPK is only deleted once the folder is complete.
"""

import logging
import shutil
from pathlib import Path

from .archives import ArchiveError, check_vpk, extract_vpk

log = logging.getLogger(__name__)

MENU = "resource/ui/mainmenuoverride.res"


def convert(vpk_path: Path) -> None:
    from core.operations.vgui_preload import _add_vguipreload_string

    staging = vpk_path.with_name(f".{vpk_path.stem}.eternal-unpack")
    try:
        archive, names = check_vpk(vpk_path)
    except (ArchiveError, OSError, ValueError) as exc:
        log.warning("left %s as it is: %s", vpk_path.name, exc)
        return
    try:
        menu = next((name for name in names if name.lower() == MENU), None)
        if menu is None:
            return
        shutil.rmtree(staging, ignore_errors=True)
        extract_vpk(archive, names, staging, vpk_path.name)
    except (ArchiveError, OSError) as exc:
        log.warning("left %s as it is: %s", vpk_path.name, exc)
        shutil.rmtree(staging, ignore_errors=True)
        return
    finally:
        # Windows can't delete the VPK while it's open.
        archive.close()

    try:
        _add_vguipreload_string(staging / menu)
        target = vpk_path.with_suffix("")
        if target.exists():
            shutil.copytree(staging, target, dirs_exist_ok=True)
        else:
            staging.rename(target)
        vpk_path.unlink()
    except OSError as exc:
        log.warning("left %s as it is: %s", vpk_path.name, exc)
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def install() -> None:
    from core.operations import vgui_preload

    if not callable(getattr(vgui_preload, "_process_vpk", None)):
        raise RuntimeError("casual-pre-loader no longer has vgui_preload._process_vpk; update eternal_engine/hud.py")
    vgui_preload._process_vpk = convert
