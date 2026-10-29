"""Mod library operations on top of upstream's import and addon services."""

import json
import logging
import os
import shutil
import tempfile
import zipfile
from collections.abc import Callable, Iterable
from pathlib import Path

from .archives import ArchiveError, check_vpk

log = logging.getLogger(__name__)

Progress = Callable[[int, str], None]

MAX_ARCHIVE_BYTES = 8 * 1024 ** 3
MAX_ARCHIVE_ENTRIES = 200_000
MAX_SEARCH_DEPTH = 4
GENERIC_FOLDERS = {"tf", "tf2", "custom", "mod", "mods", "files", "content", "addons", "download"}
SET_ASIDE = ".eternal-previous-"


class LibraryError(Exception):
    pass


def _config():
    from core.config import config

    return config


def check_name(name: str) -> str:
    if not name or name in (".", "..") or Path(name).name != name or "/" in name or "\\" in name:
        raise LibraryError(f"Invalid mod name: {name!r}")
    return name


def list_library() -> dict:
    from core.services.addons import AddonService
    from core.settings import addon_metadata
    from core.util.pcf_path_walk import get_mod_particles

    restore_set_aside()
    service = AddonService()
    service.scan_addon_contents()

    addons = []
    for group in service.get_addons_grouped().values():
        for info in group:
            name = str(info.get("file_path"))
            meta = addon_metadata.get(name, {})
            addons.append({
                "name": name,
                "display_name": str(info.get("addon_name") or name),
                "type": str(info.get("type") or "Unknown"),
                "description": str(info.get("description") or ""),
                "file_count": int(meta.get("file_count") or 0),
                "modified": float(meta.get("last_modified") or 0),
            })
    addons.sort(key=lambda a: a["display_name"].lower())

    particle_mods, _ = get_mod_particles()
    particles_dir = _config().particles_dir
    particles = [
        {"name": name, "particles": sorted(files), "modified": _newest_mtime(particles_dir / name)}
        for name, files in sorted(particle_mods.items(), key=lambda kv: kv[0].lower())
    ]
    return {"addons": addons, "particles": particles}


def _newest_mtime(folder: Path) -> float:
    return max((f.stat().st_mtime for f in folder.rglob("*") if f.is_file()), default=0.0)


def conflicts(order: list[str], particle_order: list[str]) -> dict:
    """Overlaps between enabled mods. The first mod in each list wins."""
    from core.services.addons import AddonService
    from core.services.conflicts import detect_addon_overwrites
    from core.settings import addon_metadata
    from core.util.pcf_path_walk import get_mod_particles

    AddonService().scan_addon_contents()
    contents = {name: meta.get("files", []) for name, meta in addon_metadata.items()}

    addon_overlaps = [
        {"winner": winner, "loser": loser, "files": sorted(f.replace("\\", "/") for f in files)}
        for winner, losers in detect_addon_overwrites(order, contents).items()
        for loser, files in losers.items()
    ]

    particle_mods, _ = get_mod_particles()
    owners: dict[str, str] = {}
    pairs: dict[tuple[str, str], list[str]] = {}
    for mod in particle_order:
        for particle in particle_mods.get(mod, []):
            winner = owners.setdefault(particle, mod)
            if winner != mod:
                pairs.setdefault((winner, mod), []).append(particle)

    particle_overlaps = [
        {"winner": winner, "loser": loser, "files": sorted(files)}
        for (winner, loser), files in pairs.items()
    ]
    return {"addons": addon_overlaps, "particles": particle_overlaps}


def particle_selections(particle_order: list[str]) -> dict[str, str]:
    from core.util.pcf_path_walk import get_mod_particles

    particle_mods, _ = get_mod_particles()
    selections: dict[str, str] = {}
    for mod in particle_order:
        for particle in particle_mods.get(mod, []):
            selections.setdefault(particle, mod)
    return selections


def remove(addon_names: list[str], particle_names: list[str]) -> tuple[list[str], list[str]]:
    from core.services.addons import AddonService
    from core.services.particles import delete_particle_mods

    config = _config()
    addons, particles, missing = [], [], []
    for names, root, found in ((addon_names, config.addons_dir, addons), (particle_names, config.particles_dir, particles)):
        for name in map(check_name, names):
            (found if (root / name).is_dir() else missing).append(name)

    if addons:
        ok, message = AddonService().delete_addons(addons)
        if not ok:
            raise LibraryError(message)
    if particles:
        ok, message = delete_particle_mods(particles)
        if not ok:
            raise LibraryError(message)
    return addons + particles, missing


def _vpk_base(stem: str) -> str | None:
    """Base name of a multipart VPK member (mod_dir, mod_000), else None."""
    if len(stem) > 4 and stem.lower().endswith("_dir"):
        return stem[:-4]
    if len(stem) > 4 and stem[-4] == "_" and stem[-3:].isdigit():
        return stem[:-4]
    return None


def normalize_inputs(paths: Iterable[Path]) -> list[Path]:
    """Dedupe inputs and collapse multipart VPK sets into their _dir file."""
    seen: dict[tuple[str, str], Path] = {}
    for given in paths:
        path = given.resolve()
        base = _vpk_base(path.stem) if path.suffix.lower() == ".vpk" else None
        # Only a real _dir file makes this a part of a set; pack_100.vpk alone is its own mod.
        if base is not None and (dir_file := path.with_name(f"{base}_dir.vpk")).is_file():
            path = dir_file
        seen.setdefault((str(path.parent).lower(), path.name.lower()), path)
    return list(seen.values())


def extract_zip(path: Path, destination: Path) -> None:
    """Unpack with entry-count, size and containment checks before writing anything."""
    root = destination.resolve()
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) > MAX_ARCHIVE_ENTRIES:
            raise LibraryError(f"{path.name} has too many entries ({len(entries)})")
        if sum(e.file_size for e in entries) > MAX_ARCHIVE_BYTES:
            raise LibraryError(f"{path.name} expands to more than {MAX_ARCHIVE_BYTES // 1024 ** 3} GiB")
        for entry in entries:
            target = (root / entry.filename).resolve()
            if target != root and root not in target.parents:
                raise LibraryError(f"{path.name} contains an unsafe path: {entry.filename}")
        archive.extractall(root)


def _snapshot() -> dict[tuple[str, str], int]:
    config = _config()
    state = {}
    for kind, root in (("addon", config.addons_dir), ("particle", config.particles_dir)):
        if root.is_dir():
            for entry in root.iterdir():
                if entry.is_dir():
                    state[(kind, entry.name)] = entry.stat().st_mtime_ns
    return state


def _rename_library_entry(old: str, new: str) -> None:
    config = _config()
    for root in (config.addons_dir, config.particles_dir):
        source = root / old
        if not source.is_dir():
            continue
        target = root / new
        shutil.rmtree(target, ignore_errors=True)
        source.rename(target)
        mod_json = target / "mod.json"
        if mod_json.is_file():
            info = json.loads(mod_json.read_text(encoding="utf-8"))
            if info.get("addon_name") == old:
                info["addon_name"] = new
                mod_json.write_text(json.dumps(info, indent=2), encoding="utf-8")


def restore_set_aside() -> None:
    """Puts back mods that an import moved out of the way and never got to finish."""
    for root in (_config().addons_dir, _config().particles_dir):
        if not root.is_dir():
            continue
        for aside in root.glob(f"{SET_ASIDE}*"):
            current = root / aside.name[len(SET_ASIDE):]
            log.warning("restoring %s after an unfinished import", current.name)
            shutil.rmtree(current, ignore_errors=True)
            aside.rename(current)


def _replacing(names: list[str], run: Callable[[], tuple[bool, str]]) -> tuple[bool, str]:
    """Upstream deletes a mod before copying its replacement in. Keep the old copy
    until the import has succeeded, and put it back if it fails."""
    config = _config()
    moved = []
    for name in dict.fromkeys(names):
        for root in (config.addons_dir, config.particles_dir):
            current = root / name
            if current.exists():
                aside = root / f"{SET_ASIDE}{name}"
                shutil.rmtree(aside, ignore_errors=True)
                current.rename(aside)
                moved.append((current, aside))

    ok = False
    try:
        ok, message = run()
        return ok, message
    finally:
        for current, aside in moved:
            if ok and current.exists():
                shutil.rmtree(aside, ignore_errors=True)
            else:
                shutil.rmtree(current, ignore_errors=True)
                aside.rename(current)


def _upstream_vpk_name(stem: str) -> str:
    # Same rule as ImportService.process_vpk_file uses to name the library folder.
    return stem[:-4] if stem[-3:].isdigit() and stem[-4:-3] == "_" or stem[-4:] == "_dir" else stem


def _import_vpk(importer, path: Path, progress: Progress) -> tuple[bool, str]:
    archive, _ = check_vpk(path)
    archive.close()

    stem = path.stem
    if not (stem.isdigit() and len(stem) <= 3):
        name = _upstream_vpk_name(stem)
        return _replacing([name], lambda: importer.process_vpk_file(path, progress_callback=progress))

    # Upstream indexes stem[-4] when the stem ends in three digits.
    def run() -> tuple[bool, str]:
        with tempfile.TemporaryDirectory() as tmp:
            alias = Path(tmp) / f"{stem}_.vpk"
            try:
                os.link(path, alias)
            except OSError:
                shutil.copy2(path, alias)
            ok, message = importer.process_vpk_file(alias, progress_callback=progress)
        if ok:
            _rename_library_entry(f"{stem}_", stem)
        return ok, message.replace(f"{stem}_", stem)

    return _replacing([f"{stem}_", stem], run)


def _import_folder(importer, folder: Path, progress: Progress, override: str | None = None) -> tuple[bool, str]:
    return _replacing([override or folder.name],
                      lambda: importer.process_folder(folder, override_name=override, progress_callback=progress))


def _looks_like_mod(folder: Path) -> bool:
    from core.constants import VALID_MOD_ROOT_FOLDERS

    wanted = {name.lower() for name in VALID_MOD_ROOT_FOLDERS}
    try:
        return any(child.is_dir() and child.name.lower() in wanted for child in folder.iterdir())
    except OSError:
        return False


def _find_mod_roots(validator, root: Path) -> list[Path]:
    found, queue = [], [(root, 0)]
    while queue:
        folder, depth = queue.pop(0)
        if _looks_like_mod(folder) and validator.validate_folder(folder).is_valid:
            found.append(folder)
            continue
        if depth < MAX_SEARCH_DEPTH:
            queue.extend((child, depth + 1) for child in sorted(folder.iterdir()) if child.is_dir())
    return found


def _import_extracted(importer, root: Path, progress: Progress) -> tuple[bool, str]:
    """An archive the launcher already unpacked; root.name is the archive name."""
    name = root.name
    vpks = normalize_inputs(root.rglob("*.vpk"))
    if vpks:
        outcomes = [_import_vpk(importer, vpk, progress) for vpk in vpks]
        imported = sum(ok for ok, _ in outcomes)
        if not imported:
            return False, "; ".join(message for _, message in outcomes)
        return True, f"Imported {imported} VPK(s) from {name}"

    roots = _find_mod_roots(importer.validator, root)
    if not roots:
        return False, f"No mod content found in {name}"

    imported, errors = 0, []
    for folder in roots:
        override = name if folder == root or folder.name.lower() in GENERIC_FOLDERS else None
        ok, message = _import_folder(importer, folder, progress, override)
        imported += ok
        if not ok:
            errors.append(message)
    if not imported:
        return False, "; ".join(errors)
    return True, f"Imported {imported} mod(s) from {name}"


def _stage_vanilla_particles() -> None:
    """Upstream merges particle mods against vanilla copies that it only stages
    while preparing a build, so a particle import on a fresh install found none."""
    from core.backup_manager import prepare_runtime_environment

    if any(_config().temp_to_be_referenced_dir.glob("*.pcf")):
        return
    error = prepare_runtime_environment()
    if error:
        raise LibraryError(error)


def import_items(paths: list[Path], extracted: list[Path], progress: Progress) -> list[dict]:
    from core.services.addons import AddonService
    from core.services.importer import ImportService

    restore_set_aside()
    _stage_vanilla_particles()
    importer = ImportService()
    jobs = [(path, False) for path in normalize_inputs(paths)] + [(path.resolve(), True) for path in extracted]
    results = []

    for index, (path, is_extracted) in enumerate(jobs):
        def report(percent: int, message: str, _i=index) -> None:
            progress(int((_i + max(0, min(percent, 100)) / 100) * 100 / len(jobs)), message)

        before = _snapshot()
        try:
            if not path.exists():
                ok, message = False, f"Not found: {path}"
            elif is_extracted:
                ok, message = _import_extracted(importer, path, report)
            elif path.is_dir():
                ok, message = _import_folder(importer, path, report)
            elif path.suffix.lower() == ".vpk":
                ok, message = _import_vpk(importer, path, report)
            elif path.suffix.lower() == ".zip":
                with tempfile.TemporaryDirectory() as tmp:
                    unpacked = Path(tmp) / path.stem
                    extract_zip(path, unpacked)
                    ok, message = _import_extracted(importer, unpacked, report)
            else:
                ok, message = False, f"Unsupported file type: {path.name}"
        except (LibraryError, ArchiveError) as exc:
            log.warning("import rejected %s: %s", path, exc)
            ok, message = False, str(exc)
        except Exception as exc:
            log.exception("import failed for %s", path)
            ok, message = False, str(exc)

        after = _snapshot()
        changed = [
            {"kind": kind, "name": name, "new": (kind, name) not in before}
            for (kind, name), mtime in after.items()
            if before.get((kind, name)) != mtime
        ]
        if not ok:
            config = _config()
            for entry in changed:
                if entry["new"]:
                    root = config.addons_dir if entry["kind"] == "addon" else config.particles_dir
                    shutil.rmtree(root / entry["name"], ignore_errors=True)
            changed = [entry for entry in changed if not entry["new"]]

        results.append({
            "source": path.name,
            "ok": bool(ok),
            "message": message,
            "mods": sorted(changed, key=lambda e: e["name"].lower()),
        })

    AddonService().scan_addon_contents()
    return results
