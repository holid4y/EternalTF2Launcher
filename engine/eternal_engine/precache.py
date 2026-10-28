"""Keeps structurally broken models out of QuickPrecache.

QuickPrecache chains every custom prop and bot model into one $includemodel
list. A model with a garbage header in that chain crashes TF2 at the main menu
("GroupStudioHdr(): invalid index"), as the femspy and femmepyro bot models
did, so such models are left out and reported instead.
"""

import logging
import struct
from pathlib import Path

log = logging.getLogger(__name__)

_OFF_NUMBONES = 156
_OFF_NUMTEXTURES = 204
_HEADER = _OFF_NUMTEXTURES + 4
# Garbage reads as huge or negative counts; real TF2 models stay far below this.
_MAX_COUNT = 4096

# Models left out by the last scan and where they were found. Upstream scans twice per install.
skipped: dict[str, str] = {}


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


def install_guard() -> None:
    """Wraps upstream's precache scan; make_precache_list looks both functions up at call time."""
    from core.quickprecache import precache_list as upstream

    if getattr(upstream, "eternal_guard", False):
        return
    scan_folder, scan_vpk = upstream.manage_folder, upstream.manage_vpk
    upstream.manage_folder = lambda folder: _check_folder(folder, scan_folder(folder))
    upstream.manage_vpk = lambda vpk_path: _check_vpk(vpk_path, scan_vpk(vpk_path))
    upstream.eternal_guard = True
