#!/usr/bin/env python3
"""
model_depscan — Source-engine player-model dependency scanner.

Scans a TF2 mod folder (or a single .mdl) and reports the full asset
dependency chain of every model it finds: included animation models, material
references and their search paths, the VTX/VVD/PHY geometry set, and any
non-standard paths. It flags the specific conditions that make the Source
engine reject a custom model and fall back to the vanilla one.

Why this exists
---------------
When a custom player model "doesn't apply" in-game, the usual question is *which
asset is broken*. The engine rejects a studio model outright (and falls back) on
only a few conditions — a VVD/VTX whose internal checksum does not match the
MDL, or a missing required geometry lump. Everything else (a missing material, a
missing animation include) degrades gracefully: you get the model with purple
textures or a T-pose, not a vanilla replacement. This tool checks the fatal
conditions directly by parsing the binary headers, so a human can tell
"corrupt/incomplete asset" apart from "engine policy refused to load a valid
asset" without launching the game repeatedly.

It parses the binary formats with the standard library only — no engine, no
third-party packages — so it runs anywhere Python 3.9+ does.

Usage
-----
    python model_depscan.py <mod-folder-or-mdl> [--json out.json] [--player-only]

Exit codes
----------
    0  scan completed, no fatal asset problems found
    1  scan completed, at least one fatal problem found (would fall back)
    2  usage / IO error
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass, field, asdict
from enum import Enum
from pathlib import Path
from typing import Optional

# ─────────────────────────── binary layout constants ───────────────────────────
# studiohdr_t (MDL v44–49). Offsets validated empirically against TF2 v48 models.
_MDL_MAGIC = b"IDST"
_OFF_VERSION = 4
_OFF_CHECKSUM = 8
_OFF_FLAGS = 152
_OFF_NUMBONES = 156
_OFF_BONEINDEX = 160
_OFF_NUMLOCALANIM = 180
_OFF_NUMLOCALSEQ = 188
_OFF_NUMTEXTURES = 204
_OFF_TEXTUREINDEX = 208
_OFF_NUMCDTEXTURES = 212
_OFF_CDTEXTUREINDEX = 216
_OFF_NUMBODYPARTS = 232
_OFF_NUMINCLUDEMODELS = 336
_OFF_INCLUDEMODELINDEX = 340
_OFF_HEADER_MIN = _OFF_INCLUDEMODELINDEX + 4  # smallest header we can fully read

_SIZEOF_MSTUDIOTEXTURE = 64          # mstudiotexture_t
_SIZEOF_MSTUDIOBONE = 216            # mstudiobone_t (v48/49)
_SIZEOF_MSTUDIOMODELGROUP = 8        # mstudiomodelgroup_t

# VVD (vertexFileHeader_t): id "IDSV", checksum at +8.
_VVD_MAGIC = b"IDSV"
_OFF_VVD_CHECKSUM = 8
# VTX (OptimizedModel::FileHeader_t): no magic; checksum at +16.
_OFF_VTX_CHECKSUM = 16

# Geometry lumps a player model needs. The MDL alone is never enough.
_GEOMETRY_SUFFIXES = (".vvd", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".phy")
# Without these two the model cannot render and the engine falls back.
_FATAL_IF_MISSING = (".vvd", ".dx90.vtx")

# Material-search roots that are normal/expected for a player model.
_EXPECTED_CDMATERIAL_ROOTS = ("models/",)
# Roots a mod author deliberately relocates into (community sv_pure-safe paths).
# Not an error — flagged as INFO so a human knows why a path looks odd.
_RELOCATED_ROOTS = ("vgui/replay/thumbnails/", "console/")


class Severity(str, Enum):
    FATAL = "FATAL"      # engine rejects the model → vanilla fallback
    WARNING = "WARNING"  # model loads but content is missing (purple/T-pose)
    INFO = "INFO"        # noteworthy, not a problem


@dataclass
class Issue:
    severity: Severity
    code: str
    message: str


@dataclass
class GeometryFile:
    suffix: str
    present: bool
    checksum: Optional[int] = None
    checksum_matches: Optional[bool] = None


@dataclass
class ModelInfo:
    rel_path: str                       # e.g. "models/player/scout.mdl"
    found: bool
    version: Optional[int] = None
    checksum: Optional[int] = None
    numbones: Optional[int] = None
    flags: Optional[int] = None
    numbodyparts: Optional[int] = None
    numlocalseq: Optional[int] = None
    cdmaterials: list[str] = field(default_factory=list)
    materials: list[str] = field(default_factory=list)
    includes: list[str] = field(default_factory=list)
    geometry: list[GeometryFile] = field(default_factory=list)
    missing_materials: list[str] = field(default_factory=list)
    issues: list[Issue] = field(default_factory=list)


# ─────────────────────────── low-level readers ───────────────────────────
def _s32(b: bytes, o: int) -> int:
    return struct.unpack_from("<i", b, o)[0]


def _u32(b: bytes, o: int) -> int:
    return struct.unpack_from("<I", b, o)[0]


def _cstr(b: bytes, o: int) -> str:
    end = b.index(0, o)
    return b[o:end].decode("ascii", "replace")


def _read_vvd_checksum(data: bytes) -> Optional[int]:
    if len(data) < 12 or data[:4] != _VVD_MAGIC:
        return None
    return _u32(data, _OFF_VVD_CHECKSUM)


def _read_vtx_checksum(data: bytes) -> Optional[int]:
    if len(data) < _OFF_VTX_CHECKSUM + 4:
        return None
    return _u32(data, _OFF_VTX_CHECKSUM)


# ─────────────────────────── MDL parsing ───────────────────────────
class MdlParseError(Exception):
    pass


def parse_mdl(data: bytes) -> dict:
    """Parse the parts of a studiohdr_t this tool needs. Raises MdlParseError
    on anything that isn't a readable MDL header."""
    if len(data) < _OFF_HEADER_MIN:
        raise MdlParseError(f"file too small ({len(data)} bytes) to be an MDL header")
    if data[:4] != _MDL_MAGIC:
        raise MdlParseError("missing IDST magic — not a studio model")

    h: dict = {
        "version": _s32(data, _OFF_VERSION),
        "checksum": _u32(data, _OFF_CHECKSUM),
        "flags": _s32(data, _OFF_FLAGS),
        "numbones": _s32(data, _OFF_NUMBONES),
        "numlocalanim": _s32(data, _OFF_NUMLOCALANIM),
        "numlocalseq": _s32(data, _OFF_NUMLOCALSEQ),
        "numbodyparts": _s32(data, _OFF_NUMBODYPARTS),
    }

    def safe(parse_fn, default):
        try:
            return parse_fn()
        except Exception:
            return default

    h["cdmaterials"] = safe(lambda: _parse_cdmaterials(data), [])
    h["materials"] = safe(lambda: _parse_textures(data), [])
    h["includes"] = safe(lambda: _parse_includes(data), [])
    return h


def _parse_cdmaterials(data: bytes) -> list[str]:
    n = _s32(data, _OFF_NUMCDTEXTURES)
    idx = _s32(data, _OFF_CDTEXTUREINDEX)
    out = []
    for k in range(min(n, 64)):
        off = _s32(data, idx + k * 4)
        s = _cstr(data, off).replace("\\", "/")
        if s:
            out.append(s)
    return out


def _parse_textures(data: bytes) -> list[str]:
    n = _s32(data, _OFF_NUMTEXTURES)
    idx = _s32(data, _OFF_TEXTUREINDEX)
    out = []
    for k in range(min(n, 512)):
        base = idx + k * _SIZEOF_MSTUDIOTEXTURE
        name_off = _s32(data, base)
        out.append(_cstr(data, base + name_off).replace("\\", "/"))
    return out


def _parse_includes(data: bytes) -> list[str]:
    n = _s32(data, _OFF_NUMINCLUDEMODELS)
    idx = _s32(data, _OFF_INCLUDEMODELINDEX)
    out = []
    for k in range(min(n, 64)):
        base = idx + k * _SIZEOF_MSTUDIOMODELGROUP
        szname = _s32(data, base + 4)
        if szname:
            out.append(_cstr(data, base + szname).replace("\\", "/"))
    return out


# ─────────────────────────── scanning ───────────────────────────
class ModDepScanner:
    """Resolves model dependencies relative to a mod root (the folder that
    *contains* ``models/`` and ``materials/``)."""

    def __init__(self, mod_root: Path):
        self.mod_root = mod_root
        self._cache: dict[str, ModelInfo] = {}

    # -- path helpers ----------------------------------------------------------
    def _abs(self, rel: str) -> Path:
        return self.mod_root / rel.replace("/", "\\")

    def _exists(self, rel: str) -> bool:
        return self._abs(rel).exists()

    # -- public API ------------------------------------------------------------
    def scan_model(self, rel_path: str, _seen: Optional[set[str]] = None) -> ModelInfo:
        """Scan one model by its mod-relative path (e.g. models/player/scout.mdl),
        recursing into its $includemodel references. Cycle-safe."""
        rel_path = rel_path.replace("\\", "/")
        if rel_path in self._cache:
            return self._cache[rel_path]

        _seen = _seen or set()
        info = ModelInfo(rel_path=rel_path, found=self._exists(rel_path))
        self._cache[rel_path] = info  # cache early to break cycles

        if not info.found:
            return info  # caller decides whether absence is fatal/expected

        try:
            data = self._abs(rel_path).read_bytes()
            h = parse_mdl(data)
        except (OSError, MdlParseError) as exc:
            info.issues.append(Issue(Severity.FATAL, "MDL_UNREADABLE",
                                     f"cannot parse MDL header: {exc}"))
            return info

        info.version = h["version"]
        info.checksum = h["checksum"]
        info.numbones = h["numbones"]
        info.flags = h["flags"]
        info.numbodyparts = h["numbodyparts"]
        info.numlocalseq = h["numlocalseq"]
        info.cdmaterials = h["cdmaterials"]
        info.materials = h["materials"]
        info.includes = h["includes"]

        self._check_geometry(rel_path, info)
        self._check_materials(info)
        self._check_paths(info)

        # Recurse into includes (animation / shared models)
        for inc in info.includes:
            if inc in _seen:
                continue
            _seen.add(inc)
            child = self.scan_model(inc, _seen)
            if not child.found:
                # An include that isn't in the mod is normal *if* it's a stock
                # vanilla file (engine loads it from the game VPK). We can't see
                # the game VPK here, so flag it as INFO unless it lives under a
                # clearly mod-specific folder.
                sev = Severity.WARNING if self._looks_mod_specific(inc) else Severity.INFO
                info.issues.append(Issue(
                    sev, "INCLUDE_NOT_IN_MOD",
                    f"$includemodel '{inc}' is not in the mod "
                    f"({'expected to be shipped — likely dangling' if sev is Severity.WARNING else 'assumed vanilla, loaded from game VPK'})"))
        return info

    def find_models(self, player_only: bool) -> list[str]:
        """All .mdl files under the mod's models/ tree, as mod-relative paths."""
        models_dir = self.mod_root / "models"
        if not models_dir.is_dir():
            return []
        out = []
        for p in models_dir.rglob("*.mdl"):
            rel = p.relative_to(self.mod_root).as_posix()
            if player_only and "/player/" not in rel:
                continue
            out.append(rel)
        return sorted(out)

    # -- checks ----------------------------------------------------------------
    def _check_geometry(self, rel_path: str, info: ModelInfo) -> None:
        # Animation/sequence models ($includemodel targets) carry no render
        # geometry — numbodyparts == 0 — so a missing VVD/VTX there is expected,
        # not a fault. Only models with bodyparts must ship the geometry set.
        has_geometry = bool(info.numbodyparts and info.numbodyparts > 0)
        stem = rel_path[:-4]  # strip ".mdl"
        for suf in _GEOMETRY_SUFFIXES:
            rel = stem + suf
            abs_p = self._abs(rel)
            gf = GeometryFile(suffix=suf, present=abs_p.exists())
            if gf.present and suf != ".phy":
                try:
                    blob = abs_p.read_bytes()
                    gf.checksum = _read_vvd_checksum(blob) if suf == ".vvd" else _read_vtx_checksum(blob)
                    if gf.checksum is not None and info.checksum is not None:
                        gf.checksum_matches = (gf.checksum == info.checksum)
                except OSError:
                    gf.present = False
            info.geometry.append(gf)

            if gf.checksum_matches is False:
                info.issues.append(Issue(
                    Severity.FATAL, "CHECKSUM_MISMATCH",
                    f"'{Path(rel).name}' checksum {gf.checksum} != MDL checksum "
                    f"{info.checksum} — the engine rejects the model and falls back "
                    f"to vanilla (the .mdl/.vvd/.vtx set was not compiled together)"))
            elif has_geometry and not gf.present and suf in _FATAL_IF_MISSING:
                info.issues.append(Issue(
                    Severity.FATAL, "GEOMETRY_MISSING",
                    f"required geometry '{Path(rel).name}' is missing — the engine "
                    f"cannot build the model and falls back to vanilla"))

        if not has_geometry:
            info.issues.append(Issue(
                Severity.INFO, "ANIMATION_ONLY",
                "animation-only model (numbodyparts=0): no VVD/VTX expected; "
                "provides sequences/bones only"))

    def _check_materials(self, info: ModelInfo) -> None:
        roots = info.cdmaterials or [""]
        for tex in info.materials:
            if any((self.mod_root / "materials" / (root + tex + ".vmt").replace("/", "\\")).exists()
                   for root in roots):
                continue
            info.missing_materials.append(tex)
        if info.missing_materials:
            info.issues.append(Issue(
                Severity.WARNING, "MATERIAL_MISSING",
                f"{len(info.missing_materials)} material(s) not found under any "
                f"$cdmaterials path in this mod (renders purple unless supplied by "
                f"the vanilla game): {', '.join(info.missing_materials[:6])}"
                f"{'...' if len(info.missing_materials) > 6 else ''}"))

    def _check_paths(self, info: ModelInfo) -> None:
        for cd in info.cdmaterials:
            low = cd.lower()
            if any(low.startswith(r) for r in _RELOCATED_ROOTS):
                info.issues.append(Issue(
                    Severity.INFO, "RELOCATED_CDMATERIAL",
                    f"$cdmaterials path '{cd}' is a community-relocated root — the "
                    f"model's textures are deliberately served from an sv_pure-safe "
                    f"location"))
            elif not any(low.startswith(r) for r in _EXPECTED_CDMATERIAL_ROOTS) and low:
                info.issues.append(Issue(
                    Severity.INFO, "NONSTANDARD_CDMATERIAL",
                    f"$cdmaterials path '{cd}' is outside the usual models/ root"))
        for inc in info.includes:
            if self._looks_mod_specific(inc):
                info.issues.append(Issue(
                    Severity.INFO, "NONSTANDARD_INCLUDE",
                    f"$includemodel '{inc}' uses a non-standard, mod-specific path "
                    f"(vanilla animation models live at models/player/<class>_*.mdl "
                    f"or models/workshop/player/animations/)"))

    @staticmethod
    def _looks_mod_specific(rel: str) -> bool:
        """True when an include path looks author-private rather than a stock
        vanilla animation model the engine would have anyway."""
        low = rel.lower()
        stock_markers = (
            "models/player/scout_", "models/player/soldier_", "models/player/pyro_",
            "models/player/demo_", "models/player/heavy_", "models/player/engineer_",
            "models/player/medic_", "models/player/sniper_", "models/player/spy_",
            "models/workshop/player/animations/",
        )
        if any(low.startswith(m) for m in stock_markers):
            return False
        return low.startswith("models/player/") and "/" in low[len("models/player/"):]


# ─────────────────────────── reporting ───────────────────────────
def _model_fatal_count(info: ModelInfo) -> int:
    return sum(1 for i in info.issues if i.severity is Severity.FATAL)


def print_report(root_models: list[ModelInfo], scanner: ModDepScanner) -> int:
    fatals = 0
    bar = "=" * 74
    print(bar)
    print(f" model_depscan — {scanner.mod_root}")
    print(bar)

    for info in root_models:
        fatals += _print_tree(info, scanner, indent=0, seen=set())

    print(bar)
    print(" SUMMARY")
    print(bar)
    all_infos = list(scanner._cache.values())
    n_models = sum(1 for i in all_infos if i.found)
    n_fatal = sum(_model_fatal_count(i) for i in all_infos)
    n_warn = sum(1 for i in all_infos for x in i.issues if x.severity is Severity.WARNING)
    print(f"  models parsed:        {n_models}")
    print(f"  FATAL issues:         {n_fatal}  (each would force a vanilla fallback)")
    print(f"  WARNING issues:       {n_warn}  (missing content; model still loads)")
    if n_fatal == 0:
        print()
        print("  No fatal asset problem found. Every required geometry lump is")
        print("  present and every VVD/VTX checksum matches its MDL, so the model")
        print("  set is internally valid and would load offline. A vanilla result")
        print("  in Valve Casual is therefore NOT explained by a broken asset —")
        print("  isolate it with the offline-vs-Casual experiment (see below).")
    return 1 if n_fatal else 0


def _print_tree(info: ModelInfo, scanner: ModDepScanner, indent: int, seen: set[str]) -> int:
    pad = "  " * indent
    fatals = 0
    if info.rel_path in seen:
        print(f"{pad}~ {info.rel_path} (already shown)")
        return 0
    seen.add(info.rel_path)

    if not info.found:
        print(f"{pad}* {info.rel_path}  [not in mod]")
        return 0

    cs = info.checksum
    print(f"{pad}* {info.rel_path}")
    print(f"{pad}    v{info.version} checksum={cs} bones={info.numbones} "
          f"bodyparts={info.numbodyparts} seqs={info.numlocalseq}")

    geo = "  ".join(
        f"{g.suffix}:" + ("--" if not g.present else
                          "ok" if g.checksum_matches in (True, None) else "MISMATCH")
        for g in info.geometry)
    print(f"{pad}    geometry: {geo}")

    if info.cdmaterials:
        print(f"{pad}    $cdmaterials: {', '.join(info.cdmaterials)}")
    if info.materials:
        shown = ", ".join(info.materials[:6]) + ("..." if len(info.materials) > 6 else "")
        print(f"{pad}    materials[{len(info.materials)}]: {shown}")

    for iss in info.issues:
        mark = {"FATAL": "[X]", "WARNING": "[!]", "INFO": "[i]"}[iss.severity.value]
        print(f"{pad}    {mark} [{iss.severity.value}] {iss.message}")
        if iss.severity is Severity.FATAL:
            fatals += 1

    for inc in info.includes:
        child = scanner._cache.get(inc)
        if child is not None:
            fatals += _print_tree(child, scanner, indent + 1, seen)
    return fatals


# ─────────────────────────── CLI ───────────────────────────
def resolve_mod_root(target: Path) -> tuple[Path, list[str]]:
    """Return (mod_root, [model rel-paths to scan]). A mod root is the folder
    containing models/. Accepts either that folder or a direct .mdl path."""
    if target.is_file() and target.suffix.lower() == ".mdl":
        # Walk up to the folder that contains "models/"
        for parent in target.parents:
            if (parent / "models").is_dir() and "models" in target.relative_to(parent).parts:
                rel = target.relative_to(parent).as_posix()
                return parent, [rel]
        # Fallback: treat the .mdl's grandparent chain start as root
        return target.parent, [target.name]
    if target.is_dir():
        if (target / "models").is_dir():
            return target, []
        # maybe they pointed at the models/ dir itself
        if target.name.lower() == "models":
            return target.parent, []
    raise FileNotFoundError(f"'{target}' is not a .mdl or a mod folder containing models/")


def main(argv: Optional[list[str]] = None) -> int:
    # Best-effort UTF-8 so the tool's output survives redirection into logs on
    # legacy Windows code pages; harmless where stdout is already UTF-8.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass

    ap = argparse.ArgumentParser(
        prog="model_depscan",
        description="Scan a TF2 mod folder for a model's full dependency chain and "
                    "flag the asset problems that cause a vanilla fallback.")
    ap.add_argument("target", type=Path,
                    help="mod folder (containing models/) or a single .mdl")
    ap.add_argument("--player-only", action="store_true",
                    help="only scan models under models/player/")
    ap.add_argument("--json", type=Path, metavar="PATH",
                    help="also write the full result as JSON")
    args = ap.parse_args(argv)

    try:
        mod_root, explicit = resolve_mod_root(args.target)
    except FileNotFoundError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    scanner = ModDepScanner(mod_root)
    targets = explicit or scanner.find_models(player_only=args.player_only)
    if not targets:
        print(f"error: no .mdl files found under {mod_root / 'models'}", file=sys.stderr)
        return 2

    roots = [scanner.scan_model(t) for t in targets]
    code = print_report(roots, scanner)

    if args.json:
        payload = {
            "mod_root": str(mod_root),
            "models": [asdict(i) for i in scanner._cache.values()],
        }
        args.json.write_text(json.dumps(payload, indent=2, default=str), encoding="utf-8")
        print(f"\nJSON written to {args.json}")

    return code


if __name__ == "__main__":
    raise SystemExit(main())
