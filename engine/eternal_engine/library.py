"""Mod library operations on top of upstream's import and addon services."""

import logging
import shutil
import tempfile
import zipfile
from collections import Counter
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
from pathlib import Path

from .archives import ArchiveError, check_vpk, extract_vpk

log = logging.getLogger(__name__)

Progress = Callable[[int, str], None]

MAX_ARCHIVE_BYTES = 8 * 1024 ** 3
MAX_ARCHIVE_ENTRIES = 200_000
MAX_SEARCH_DEPTH = 4
GENERIC_FOLDERS = {"tf", "tf2", "custom", "mod", "mods", "files", "content", "addons", "download"}
SET_ASIDE = ".eternal-previous-"
GAME_VPKS = ("tf2_misc_dir.vpk", "tf2_textures_dir.vpk", "tf2_sound_misc_dir.vpk", "tf2_sound_vo_english_dir.vpk")
# Files that come with mods but are never game files: readmes, previews, model sources.
DOCUMENTS = {".txt", ".md", ".rtf", ".pdf", ".url", ".htm", ".html", ".nfo", ".doc", ".docx", ".odt",
             ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".psd", ".xcf", ".blend", ".qc", ".smd", ".dmx"}
# Custom maps have names the game doesn't know, but only one place to go.
MAP_FILES = {".bsp", ".nav"}
TEXTURES = {".vtf", ".vmt"}


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


def _vpk_mod_name(stem: str) -> str:
    # normalize_inputs has already turned the parts of a set into their _dir file,
    # so a numbered stem here is a mod of its own: pack_100.vpk stays pack_100.
    return stem[:-4] if len(stem) > 4 and stem.lower().endswith("_dir") else stem


def _import_vpks(importer, name: str, paths: list[Path], progress: Progress,
                 beside: Path | None = None) -> tuple[bool, str]:
    """Upstream's VPK import keeps whatever bytes it managed to read, so a cut-off
    download would replace a working mod. Unpack and check every file first, then
    import the result as one folder. The other files of `beside`, the folder the
    VPKs sit in, go in on top: TF2 read those and never the VPKs."""
    with tempfile.TemporaryDirectory(dir=_config().temp_dir) as tmp:
        staging = Path(tmp) / name
        staging.mkdir()
        for path in paths:
            archive, names = check_vpk(path)
            try:
                progress(5, f"Extracting {path.name}")
                extract_vpk(archive, names, staging, path.name)
            finally:
                archive.close()
        if beside is not None:
            for file in beside.rglob("*"):
                if file.is_file() and not (file.parent == beside and file.suffix.lower() == ".vpk"):
                    target = staging / file.relative_to(beside)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, target)
        return _import_folder(importer, staging, progress, name)


def _import_vpk(importer, path: Path, progress: Progress) -> tuple[bool, str]:
    return _import_vpks(importer, _vpk_mod_name(path.stem), [path], progress)


def _vpks_in(folder: Path) -> list[Path]:
    return normalize_inputs(p for p in folder.iterdir() if p.suffix.lower() == ".vpk") if folder.is_dir() else []


def unpack_vpk_folders(names: Iterable[str], progress: Progress) -> list[str]:
    """A folder imported with a VPK inside used to be kept as it was, and TF2
    never opens a VPK packed into the preload. Unpacks those mods in place under
    the same name, so they stay enabled and in order. Returns what couldn't be."""
    from core.services.importer import ImportService

    problems = []
    for name in names:
        folder = _config().addons_dir / check_name(name)
        vpks = _vpks_in(folder)
        if not vpks:
            continue
        try:
            ok, message = _import_vpks(ImportService(), name, vpks, progress, beside=folder)
        except (ArchiveError, LibraryError, OSError) as exc:
            ok, message = False, str(exc)
        if ok:
            log.info("unpacked the VPK kept inside %s", name)
        else:
            log.warning("could not unpack the VPK inside %s: %s", name, message)
            problems.append(f"{name} still holds a VPK that couldn't be unpacked: {message}")
    return problems


def tidy_library(names: Iterable[str]) -> None:
    """Mods imported before _tidy existed get the same fixes before a build."""
    for name in names:
        try:
            _tidy(_config().addons_dir / check_name(name))
        except OSError as exc:
            log.warning("could not tidy %s: %s", name, exc)


def _import_folder(importer, folder: Path, progress: Progress, override: str | None = None) -> tuple[bool, str]:
    name = override or folder.name

    def run() -> tuple[bool, str]:
        ok, message = importer.process_folder(folder, override_name=override, progress_callback=progress)
        if ok:
            _tidy(_config().addons_dir / name)
        return ok, message

    return _replacing([name], run)


def _child(folder: Path, name: str) -> Path | None:
    return next((c for c in folder.iterdir() if c.is_dir() and c.name.lower() == name), None)


def _move_missing(source: Path, target: Path) -> None:
    for file in sorted(p for p in source.rglob("*") if p.is_file()):
        destination = target / file.relative_to(source)
        if not destination.exists():
            destination.parent.mkdir(parents=True, exist_ok=True)
            file.rename(destination)
    shutil.rmtree(source)


def _tidy(mod: Path) -> None:
    """Two packing mistakes leave a mod doing nothing in game: its folders one level
    too deep (femspy/sound/... for sound/...), and textures zipped relative to
    materials/ (models/player/x.vtf for materials/models/player/x.vtf). TF2 reads
    textures from materials/ only, so moving them can't break anything."""
    from core.constants import VALID_MOD_ROOT_FOLDERS

    if not mod.is_dir():
        return
    roots = {name.lower() for name in VALID_MOD_ROOT_FOLDERS}
    children = [c for c in mod.iterdir() if c.is_dir()]
    if not any(c.name.lower() in roots for c in children):
        for child in children:
            if _looks_like_mod(child):
                _move_missing(child, mod)

    models = _child(mod, "models")
    if models is None:
        return
    textures = [p for p in models.rglob("*") if p.is_file() and p.suffix.lower() in TEXTURES]
    if not textures:
        return
    materials = _child(mod, "materials") or mod / "materials"
    for file in textures:
        destination = materials / "models" / file.relative_to(models)
        if not destination.exists():
            destination.parent.mkdir(parents=True, exist_ok=True)
            file.rename(destination)
    for folder in sorted((p for p in models.rglob("*") if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
        if not any(folder.iterdir()):
            folder.rmdir()
    if not any(models.iterdir()):
        models.rmdir()


def _looks_like_mod(folder: Path) -> bool:
    from core.constants import VALID_MOD_ROOT_FOLDERS

    wanted = {name.lower() for name in VALID_MOD_ROOT_FOLDERS}
    try:
        return any(child.is_dir() and child.name.lower() in wanted for child in folder.iterdir())
    except OSError:
        return False


def _find_mod_roots(validator, root: Path) -> list[Path]:
    """Folders laid out like a mod. Upstream turns down a folder for holding VPKs;
    here those VPKs are parts of their own, so the folder still counts."""
    found, queue = [], [(root, 0)]
    while queue:
        folder, depth = queue.pop(0)
        if _looks_like_mod(folder):
            result = validator.validate_folder(folder)
            if result.is_valid or result.type_detected == "vpk_container":
                found.append(folder)
                continue
        if depth < MAX_SEARCH_DEPTH:
            queue.extend((child, depth + 1) for child in sorted(folder.iterdir()) if child.is_dir())
    return found


@dataclass(frozen=True)
class Part:
    """One mod inside something being imported: a VPK, a mod folder, or loose
    game files. `source` is where it sits, relative to what was scanned."""
    source: str
    name: str
    kind: str
    files: frozenset[str]
    size: int
    placement: tuple[tuple[str, str], ...] = field(default=())


def game_names(tf: Path | None) -> dict[str, str]:
    """File name to game path for every name TF2 uses exactly once, read from its own VPKs."""
    from valve_parsers import VPKFile

    if tf is None:
        return {}
    counts: Counter[str] = Counter()
    paths: dict[str, str] = {}
    for name in GAME_VPKS:
        vpk = tf / name
        if not vpk.is_file():
            continue
        try:
            listing = VPKFile(str(vpk)).list_files()
        except Exception:
            log.warning("could not read %s", vpk, exc_info=True)
            continue
        for entry in listing:
            base = entry.rsplit("/", 1)[-1].lower()
            counts[base] += 1
            paths[base] = entry.lower()
    return {base: paths[base] for base, n in counts.items() if n == 1}


def _relative(path: Path, root: Path) -> str:
    return "." if path == root else path.relative_to(root).as_posix()


def _game_files(folder: Path) -> frozenset[str]:
    from core.constants import VALID_MOD_ROOT_FOLDERS

    roots = {name.lower() for name in VALID_MOD_ROOT_FOLDERS}
    files = (p.relative_to(folder).as_posix().lower() for p in folder.rglob("*") if p.is_file())
    return frozenset(f for f in files if f.split("/", 1)[0] in roots)


def _vpk_part(vpk: Path, root: Path) -> Part:
    archive, names = check_vpk(vpk)
    try:
        size = sum(archive.get_file_info(n)["size"] + archive.get_file_info(n)["preload_bytes"] for n in names)
    finally:
        archive.close()
    return Part(_relative(vpk, root), _vpk_mod_name(vpk.stem), "vpk", frozenset(n.lower() for n in names), size)


def _loose_parts(root: Path, names: dict[str, str]) -> tuple[list[Part], list[str]]:
    """Files without the game's folders around them, placed by their file name.
    Each folder holding some becomes its own part, so variants stay apart."""
    groups: dict[Path, dict[str, Path]] = {}
    skipped = []
    for file in sorted(p for p in root.rglob("*") if p.is_file()):
        name = file.name.lower()
        target = names.get(name) or (f"maps/{name}" if file.suffix.lower() in MAP_FILES else None)
        group = groups.setdefault(file.parent, {}) if target else None
        if group is None or target in group:
            if file.suffix.lower() not in DOCUMENTS:
                skipped.append(_relative(file, root))
            continue
        group[target] = file
    parts = []
    for folder, placed in sorted(groups.items()):
        if not placed:
            continue
        name = root.name if folder == root or len(groups) == 1 else f"{root.name} - {folder.name}"
        parts.append(Part(_relative(folder, root), name, "loose", frozenset(placed),
                          sum(f.stat().st_size for f in placed.values()),
                          tuple(sorted((target, _relative(f, root)) for target, f in placed.items()))))
    return parts, skipped


def _part_name(folder: Path, root: Path) -> str:
    """A mod folder called tf or custom says nothing; the nearest folder above it that does names the mod."""
    while folder != root and folder.name.lower() in GENERIC_FOLDERS:
        folder = folder.parent
    return root.name if folder == root else folder.name


def _unique_names(parts: list[Part]) -> list[Part]:
    seen: Counter[str] = Counter()
    result = []
    for part in parts:
        seen[part.name.lower()] += 1
        n = seen[part.name.lower()]
        result.append(part if n == 1 else Part(part.source, f"{part.name} {n}", part.kind, part.files, part.size, part.placement))
    return result


def find_parts(path: Path, tf: Path | None = None) -> tuple[list[Part], list[str]]:
    """What importing `path` would add to the library, one part per mod, plus the
    files left out because nothing says where in the game they belong. Upstream
    refuses folders holding VPKs in its own window; here their VPKs become parts."""
    from core.structure_validator import StructureValidator

    path = path.resolve()
    if path.is_file():
        if path.suffix.lower() != ".vpk":
            raise LibraryError(f"Unsupported file type: {path.name}")
        return [_vpk_part(path, path.parent)], []
    if not path.is_dir():
        raise LibraryError(f"Not found: {path.name}")

    parts = [_vpk_part(vpk, path) for vpk in normalize_inputs(path.rglob("*.vpk"))]
    for folder in _find_mod_roots(StructureValidator(), path):
        parts.append(Part(_relative(folder, path), _part_name(folder, path), "folder", _game_files(folder),
                          sum(p.stat().st_size for p in folder.rglob("*") if p.is_file())))
    skipped = []
    if not parts:
        parts, skipped = _loose_parts(path, game_names(tf))
    return _unique_names(parts), skipped


def _documents(path: Path) -> list[str]:
    if not path.is_dir():
        return []
    return [_relative(p, path) for p in sorted(path.rglob("*")) if p.is_file() and p.suffix.lower() in DOCUMENTS]


def inspect(path: Path, tf: Path | None = None) -> dict:
    parts, skipped = find_parts(path, tf)
    return {
        "documents": _documents(path) if not parts else [],
        "parts": [{
            "source": part.source,
            "name": part.name,
            "kind": part.kind,
            "files": len(part.files),
            "size": part.size,
            "conflicts": [other.source for other in parts if other is not part and part.files & other.files],
        } for part in parts],
        "skipped": skipped,
    }


def _import_part(importer, root: Path, part: Part, progress: Progress) -> tuple[bool, str]:
    location = root if part.source == "." else root / part.source
    if part.kind == "vpk":
        return _import_vpks(importer, part.name, [location], progress)
    if part.kind == "folder":
        if not any(location.rglob("*.[vV][pP][kK]")):
            return _import_folder(importer, location, progress, part.name)
        # Its VPKs are parts of their own, and upstream refuses a folder holding any.
        with tempfile.TemporaryDirectory(dir=_config().temp_dir) as tmp:
            staging = Path(tmp) / part.name
            shutil.copytree(location, staging, ignore=lambda _, names: [n for n in names if n.lower().endswith(".vpk")])
            return _import_folder(importer, staging, progress, part.name)
    with tempfile.TemporaryDirectory(dir=_config().temp_dir) as tmp:
        staging = Path(tmp) / part.name
        for target, source in part.placement:
            destination = staging.joinpath(*target.split("/"))
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / source, destination)
        return _import_folder(importer, staging, progress, part.name)


def _import_extracted(importer, root: Path, progress: Progress, chosen: set[str] | None = None,
                      tf: Path | None = None, name: str | None = None) -> tuple[bool, str]:
    """A dropped folder or an archive the launcher already unpacked, named after root."""
    parts, skipped = find_parts(root, tf)
    if not parts:
        if skipped:
            return False, f"No mod content found in {root.name}: nothing says where in the game {', '.join(skipped[:3])} belong."
        documents = _documents(root)
        if documents:
            return False, f"{root.name} only has {', '.join(Path(d).name for d in documents[:3])}, no game files."
        return False, f"No mod content found in {root.name}"
    if chosen is not None:
        parts = [part for part in parts if part.source in chosen]
        if not parts:
            return False, f"None of the chosen parts are in {root.name}"
    if name and len(parts) == 1:
        part = parts[0]
        parts = [Part(part.source, check_name(name), part.kind, part.files, part.size, part.placement)]

    imported, errors = 0, []
    for part in parts:
        ok, message = _import_part(importer, root, part, progress)
        imported += ok
        if not ok:
            errors.append(message)
    if not imported:
        return False, "; ".join(errors)
    return True, f"Imported {imported} part(s) from {root.name}"


def _stage_vanilla_particles() -> None:
    """Upstream merges particle mods against vanilla copies that it only stages
    while preparing a build, so a particle import on a fresh install found none."""
    from core.backup_manager import prepare_runtime_environment

    if any(_config().temp_to_be_referenced_dir.glob("*.pcf")):
        return
    error = prepare_runtime_environment()
    if error:
        raise LibraryError(error)


def import_items(paths: list[Path], extracted: list[Path], progress: Progress,
                 chosen: set[str] | None = None, tf: Path | None = None, name: str | None = None) -> list[dict]:
    """Imports each path whole, or with `chosen`, only the parts with those sources (see inspect)."""
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
            elif is_extracted or path.is_dir():
                ok, message = _import_extracted(importer, path, report, chosen, tf, name)
            elif path.suffix.lower() == ".vpk":
                ok, message = _import_vpks(importer, check_name(name) if name else _vpk_mod_name(path.stem), [path], report)
            elif path.suffix.lower() == ".zip":
                with tempfile.TemporaryDirectory(dir=_config().temp_dir) as tmp:
                    unpacked = Path(tmp) / path.stem
                    extract_zip(path, unpacked)
                    ok, message = _import_extracted(importer, unpacked, report, chosen, tf, name)
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
