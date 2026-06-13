"""
Model compatibility scan — the detection half of the Model Compatibility layer
(docs/mdl-ik-patch-prd.md).

Goal: never let a mod fall back to vanilla *silently*. This module reads player
model MDLs straight from the library, validation-first (signature -> version ->
bounds before any offset is touched), and reports structural problems that are
known to make a custom model load as vanilla in Valve Casual — most importantly
the body/animation IK-chain mismatch (the "Lucy" class).

It NEVER writes. It is pure analysis, safe to run on anything. Already-consistent
models (FemmePyro, Burnice) produce zero issues. New failure classes are added as
extra checks in CHECKS without touching callers.
"""
from __future__ import annotations

import logging
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from core.folder_setup import folder_setup

log = logging.getLogger()

# ── studiohdr_t offsets (little-endian int32), validated against TF2 v48/49 ──
_OFF_ID = 0
_OFF_VERSION = 4
_OFF_NUMBONES = 156
_OFF_NUMIKCHAINS = 284
_OFF_IKCHAININDEX = 288
_OFF_NUMINCLUDEMODELS = 336
_OFF_INCLUDEMODELINDEX = 340
_MIN_HEADER = _OFF_IKCHAININDEX + 4  # 292: smallest read that reaches IK fields
_SIZEOF_MODELGROUP = 8               # mstudiomodelgroup_t
_SUPPORTED_VERSIONS = frozenset(range(44, 50))  # TF2 ships 48/49; accept the family


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
    severity: str          # "error" (will fall back) | "warning" (may misbehave)
    message: str

    def as_text(self) -> str:
        return f"{self.addon}: {self.message}"


def _s32(buf: bytes, off: int) -> int:
    return struct.unpack_from("<i", buf, off)[0]


def _cstr(buf: bytes, off: int) -> str:
    end = buf.index(0, off)
    return buf[off:end].decode("ascii", "replace")


def read_mdl_header(path: Path) -> Optional[MdlHeader]:
    """Validation-first header read. Returns None for anything that isn't a
    studio model we can safely interpret (never reads out of bounds)."""
    try:
        with open(path, "rb") as f:
            data = f.read(1 << 16)  # header lives well within 64 KiB
    except OSError as exc:
        log.debug(f"[compat] cannot open {path}: {exc}")
        return None

    if len(data) < _MIN_HEADER:
        return None
    if data[_OFF_ID:_OFF_ID + 4] != b"IDST":
        return None
    version = _s32(data, _OFF_VERSION)
    if version not in _SUPPORTED_VERSIONS:
        return None

    numbones = _s32(data, _OFF_NUMBONES)
    numikchains = _s32(data, _OFF_NUMIKCHAINS)

    includes: list[str] = []
    n_inc = _s32(data, _OFF_NUMINCLUDEMODELS)
    inc_idx = _s32(data, _OFF_INCLUDEMODELINDEX)
    if 0 <= n_inc <= 64 and 0 < inc_idx < len(data):
        for k in range(n_inc):
            base = inc_idx + k * _SIZEOF_MODELGROUP
            if base + _SIZEOF_MODELGROUP > len(data):
                break
            name_off = _s32(data, base + 4)
            if 0 < name_off and inc_idx + name_off < len(data):
                try:
                    includes.append(_cstr(data, base + name_off).replace("\\", "/"))
                except ValueError:
                    break

    return MdlHeader(version=version, numbones=numbones,
                     numikchains=numikchains, includes=tuple(includes))


def _is_player_body(rel_posix: str) -> bool:
    """A base player body: models/player/<class>.mdl, not an item, gib, bot, or
    animation model."""
    r = rel_posix.lower()
    if not r.startswith("models/player/"):
        return False
    tail = r[len("models/player/"):]
    if "/" in tail:                      # in items/, gibs/, lucy/, etc.
        return False
    if "animation" in tail:
        return False
    return tail.endswith(".mdl")


# ─────────────────────────── checks ───────────────────────────
def _check_ik_chain_mismatch(addon: str, addon_dir: Path, body_rel: str,
                             body: MdlHeader) -> list[CompatIssue]:
    """The Lucy class: a body that declares fewer IK chains than the custom
    animation models it includes. Animations then carry IK rules that index
    chains the merged (body) skeleton does not define — an out-of-bounds IK
    setup that, on a player model, surfaces as a clean vanilla fallback."""
    issues: list[CompatIssue] = []
    for inc in body.includes:
        inc_path = addon_dir / inc.replace("/", "\\")
        if not inc_path.exists():
            continue  # vanilla include (loaded from the game VPK) — never the problem
        inc_hdr = read_mdl_header(inc_path)
        if inc_hdr is None:
            continue
        if inc_hdr.numikchains > body.numikchains:
            issues.append(CompatIssue(
                addon=addon, model=body_rel, code="ik_chain_mismatch",
                severity="error",
                message=(f"\"{body_rel}\" declares {body.numikchains} IK chain(s) but its "
                         f"custom animation model \"{inc}\" declares {inc_hdr.numikchains} — "
                         f"the animations reference IK chains the body lacks, which makes the "
                         f"model load as the default class model in Casual. (Mods whose body "
                         f"and rig agree on IK chains, like stock-rig models, are unaffected.)")))
    return issues


# Registry: add new failure-class checks here (PRD §0 / P4 extensibility).
CHECKS = (_check_ik_chain_mismatch,)


def scan_addon(addon: str) -> list[CompatIssue]:
    """Scan one library addon folder for known compatibility problems."""
    addon_dir = folder_setup.addons_dir / addon
    if not addon_dir.is_dir():
        return []

    issues: list[CompatIssue] = []
    for mdl_path in addon_dir.rglob("*.mdl"):
        rel = mdl_path.relative_to(addon_dir).as_posix()
        if not _is_player_body(rel):
            continue
        body = read_mdl_header(mdl_path)
        if body is None:
            continue
        for check in CHECKS:
            try:
                issues.extend(check(addon, addon_dir, rel, body))
            except Exception:
                log.exception(f"[compat] check {check.__name__} failed on {addon}/{rel}")
    return issues


def scan_addons(addon_names: list[str]) -> list[CompatIssue]:
    """Scan a set of library addons (typically the enabled load order)."""
    out: list[CompatIssue] = []
    for name in addon_names:
        out.extend(scan_addon(name))
    return out
