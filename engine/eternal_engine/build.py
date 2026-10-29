"""Building the preload into tf/custom, removing it, and recovering from crashes."""

import hashlib
import json
import logging
import shutil
import sys
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from . import precache
from .journal import Journal
from .library import LibraryError, check_name, particle_selections

log = logging.getLogger(__name__)

Progress = Callable[[int, str], None]


class BuildError(Exception):
    """game_files tells the launcher what state the game was left in: "unchanged"
    (nothing was written), "vanilla" (rolled back) or "unknown"."""

    def __init__(self, message: str, game_files: str = "unchanged"):
        super().__init__(message)
        self.game_files = game_files


@dataclass
class BuildOptions:
    disable_paint_colors: bool = False
    show_console: bool = True
    fix_mdl_paths: bool = True
    skip_quickprecache: bool = False


def _config():
    from core.config import config

    return config


def journal() -> Journal:
    return Journal(_config().project_dir / "journal.json")


def validate_tf_path(tf_path: Path) -> Path:
    from core.util.sourcemod import validate_game_directory

    tf_path = tf_path.expanduser().resolve()
    if not validate_game_directory(tf_path):
        raise BuildError(f"{tf_path} is not a TF2 game folder (gameinfo.txt not found).")
    return tf_path


def _prepare() -> None:
    from core.backup_manager import prepare_runtime_environment

    error = prepare_runtime_environment()
    if error:
        raise BuildError(error)


def wine_available() -> bool | None:
    if sys.platform == "win32":
        return None
    return shutil.which("wine") is not None


def status(tf_path: Path | None) -> dict:
    from core.services.conflicts import scan_for_legacy_conflicts
    from core.services.install import InstallService

    result = {"interrupted": journal().interrupted(), "last": journal().read()}
    if tf_path is not None:
        try:
            tf = validate_tf_path(tf_path)
            result.update(
                tf_path=str(tf),
                tf_valid=True,
                modified=InstallService.is_modified(tf),
                legacy_conflicts=scan_for_legacy_conflicts(tf / "custom"),
            )
        except BuildError as exc:
            result.update(tf_path=str(tf_path), tf_valid=False, error=str(exc))
    return result


def install(tf_path: Path, order: list[str], particle_order: list[str], options: BuildOptions,
            progress: Progress) -> tuple[dict, list[str]]:
    from core.constants import Sourcemods
    from core.services.conflicts import scan_for_legacy_conflicts
    from core.services.install import InstallService
    from core.util.pcf_path_walk import apply_particle_selections

    tf = validate_tf_path(tf_path)
    config = _config()
    try:
        for name in order + particle_order:
            check_name(name)
    except LibraryError as exc:
        raise BuildError(str(exc)) from exc

    missing = [n for n in order if not (config.addons_dir / n).is_dir()]
    missing += [n for n in particle_order if not (config.particles_dir / n).is_dir()]
    if missing:
        raise BuildError(f"Not in the mod library: {', '.join(missing)}")

    warnings = [f"Old preloader files in tf/custom may conflict: {item}"
                for item in scan_for_legacy_conflicts(tf / "custom")]

    skip_precache = options.skip_quickprecache
    if not skip_precache and wine_available() is False:
        skip_precache = True
        warnings.append("Wine is not installed, so QuickPrecache was skipped. Custom models used "
                        "as map props may not load until Wine is installed.")

    _prepare()
    selections = particle_selections(particle_order)
    service = InstallService()
    before = _snapshot(tf / "custom")
    precache.reset()
    entry = journal()
    entry.begin("install", tf, addons=order, particles=particle_order)

    try:
        service.install(
            tf_path=tf,
            # Upstream copies in list order and later files overwrite earlier
            # ones, so the highest-priority mod has to be last.
            selected_addons=list(reversed(order)),
            on_progress=progress,
            apply_particle_selections=(lambda: apply_particle_selections(selections)) if selections else None,
            disable_paint_colors=options.disable_paint_colors,
            show_console_on_startup=options.show_console,
            fix_mdl_paths=options.fix_mdl_paths,
            skip_quickprecache=skip_precache,
            sourcemod=Sourcemods.TF2,
        )
    except Exception as exc:
        log.exception("install failed, restoring vanilla files")
        try:
            service.uninstall(tf, sourcemod=Sourcemods.TF2)
        except Exception as cleanup:
            log.exception("restore after failed install also failed")
            entry.finish("failed", error=str(exc), cleanup_error=str(cleanup))
            raise BuildError(f"Install failed ({exc}) and restoring the game files failed too "
                             f"({cleanup}). Verify the game files in Steam.", "unknown") from exc
        entry.finish("rolled_back", error=str(exc))
        raise BuildError(f"Install failed: {exc}. The game files were restored to vanilla.", "vanilla") from exc

    entry.finish("done")
    warnings.extend(precache.warnings(lambda model: _owner(model, order)))
    try:
        write_manifest(config.project_dir / "last_install_manifest.json", tf / "custom", before,
                       order, sorted(set(selections.values())))
    except OSError as exc:
        log.warning("could not write the install manifest: %s", exc)

    return {
        "tf_path": str(tf),
        "addons": order,
        "particles": sorted(set(selections.values())),
        "quickprecache": not skip_precache,
    }, warnings


def _owner(model: str, order: list[str]) -> str | None:
    library = _config().addons_dir
    for name in order:
        folder = library / name
        if (folder / model).is_file() or any(p.relative_to(folder).as_posix().lower() == model
                                             for p in folder.rglob("*.[mM][dD][lL]")):
            return name
    return None


def _snapshot(folder: Path) -> dict[str, tuple[int, int]]:
    files = {}
    for path in folder.rglob("*"):
        try:
            if path.is_file():
                stat = path.stat()
                files[path.relative_to(folder).as_posix()] = (stat.st_size, stat.st_mtime_ns)
        except OSError:
            continue
    return files


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fd:
        for block in iter(lambda: fd.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def write_manifest(target: Path, custom: Path, before: dict[str, tuple[int, int]],
                   addons: list[str], particles: list[str]) -> None:
    """What an install wrote into tf/custom, so a crash report can be matched to a build."""
    files = [{"path": f"custom/{rel}", "size": size, "sha256": _sha256(custom / rel)}
             for rel, (size, mtime) in sorted(_snapshot(custom).items()) if before.get(rel) != (size, mtime)]
    manifest = {
        "time": datetime.now(UTC).isoformat(timespec="seconds"),
        "addons": addons,
        "particles": particles,
        "files": files,
    }
    target.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    log.info("install wrote %d file(s), manifest at %s", len(files), target)


def uninstall(tf_path: Path, progress: Progress) -> dict:
    from core.constants import Sourcemods
    from core.services.install import InstallService

    tf = validate_tf_path(tf_path)
    _prepare()
    entry = journal()
    entry.begin("uninstall", tf)
    try:
        InstallService().uninstall(tf, on_progress=progress, sourcemod=Sourcemods.TF2)
    except Exception as exc:
        log.exception("uninstall failed")
        entry.finish("failed", error=str(exc))
        raise BuildError(f"Removing the preload failed: {exc}", "unknown") from exc
    entry.finish("done")
    return {"tf_path": str(tf)}


def recover(progress: Progress) -> dict:
    interrupted = journal().interrupted()
    if not interrupted:
        return {"recovered": False}
    tf_path = interrupted.get("tf_path")
    if not tf_path:
        journal().finish("abandoned")
        return {"recovered": False, "abandoned": True}
    log.warning("recovering interrupted %s on %s", interrupted.get("operation"), tf_path)
    uninstall(Path(tf_path), progress)
    return {"recovered": True, "operation": interrupted.get("operation"), "tf_path": tf_path}
