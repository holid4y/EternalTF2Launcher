"""Read-only scan for player models that Casual will silently replace with vanilla.

The known case is an IK-chain mismatch: a body MDL that declares fewer IK chains
than the custom animation models it includes. Headers are validated (signature,
version, bounds) before any offset is trusted, and nothing is ever written.
"""

import logging
import struct
from dataclasses import dataclass
from pathlib import Path

log = logging.getLogger(__name__)

_OFF_VERSION = 4
_OFF_NUMBONES = 156
_OFF_NUMIKCHAINS = 284
_OFF_NUMINCLUDEMODELS = 336
_OFF_INCLUDEMODELINDEX = 340
_MIN_HEADER = 292
_SIZEOF_MODELGROUP = 8
_SUPPORTED_VERSIONS = range(44, 50)


@dataclass(frozen=True)
class MdlHeader:
    version: int
    numbones: int
    numikchains: int
    includes: tuple[str, ...]


@dataclass(frozen=True)
class CompatIssue:
    addon: str
    model: str
    code: str
    severity: str
    message: str


def _s32(buf: bytes, off: int) -> int:
    return struct.unpack_from("<i", buf, off)[0]


def read_mdl_header(path: Path) -> MdlHeader | None:
    try:
        with path.open("rb") as fd:
            data = fd.read(1 << 16)
    except OSError:
        return None

    if len(data) < _MIN_HEADER or data[:4] != b"IDST":
        return None
    version = _s32(data, _OFF_VERSION)
    if version not in _SUPPORTED_VERSIONS:
        return None

    includes: list[str] = []
    if len(data) >= _OFF_INCLUDEMODELINDEX + 4:
        count = _s32(data, _OFF_NUMINCLUDEMODELS)
        index = _s32(data, _OFF_INCLUDEMODELINDEX)
        if 0 <= count <= 64 and 0 < index < len(data):
            for k in range(count):
                base = index + k * _SIZEOF_MODELGROUP
                if base + _SIZEOF_MODELGROUP > len(data):
                    break
                name_off = base + _s32(data, base + 4)
                if not 0 < name_off < len(data):
                    break
                end = data.find(b"\0", name_off)
                if end < 0:
                    break
                includes.append(data[name_off:end].decode("ascii", "replace").replace("\\", "/"))

    return MdlHeader(version, _s32(data, _OFF_NUMBONES), _s32(data, _OFF_NUMIKCHAINS), tuple(includes))


def _is_player_body(rel: str) -> bool:
    rel = rel.lower()
    if not rel.startswith("models/player/"):
        return False
    tail = rel[len("models/player/"):]
    return "/" not in tail and "animation" not in tail and tail.endswith(".mdl")


def scan_addon(addons_dir: Path, addon: str) -> list[CompatIssue]:
    addon_dir = addons_dir / addon
    if not addon_dir.is_dir():
        return []

    # Mods are authored on Windows; MDL include paths rarely match on-disk case.
    files = {p.relative_to(addon_dir).as_posix().lower(): p for p in addon_dir.rglob("*.mdl")}
    issues: list[CompatIssue] = []

    for rel_lower, path in files.items():
        if not _is_player_body(rel_lower):
            continue
        body = read_mdl_header(path)
        if body is None:
            continue
        rel = path.relative_to(addon_dir).as_posix()
        for include in body.includes:
            include_path = files.get(include.lower())
            if include_path is None:
                continue
            animation = read_mdl_header(include_path)
            if animation and animation.numikchains > body.numikchains:
                issues.append(CompatIssue(
                    addon=addon,
                    model=rel,
                    code="ik_chain_mismatch",
                    severity="error",
                    message=(
                        f"{rel} declares {body.numikchains} IK chain(s) but its animation model "
                        f"{include} declares {animation.numikchains}. Casual will load the "
                        f"default class model instead."
                    ),
                ))
    return issues


def scan(addons_dir: Path, names: list[str]) -> list[CompatIssue]:
    issues: list[CompatIssue] = []
    for name in names:
        try:
            issues.extend(scan_addon(addons_dir, name))
        except Exception:
            log.exception("compat scan failed for %s", name)
    return issues
