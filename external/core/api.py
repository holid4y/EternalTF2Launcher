"""
Headless engine API for Casual Preloader.

This module is the stable, GUI-free facade over the existing core services.
It is consumed by:
  * the bundled CLI (`python -m cli`), which is the official integration
    surface for external applications (e.g. Eternal Launcher)
  * any future headless consumer

Design rules:
  * NEVER import PyQt6 (directly or transitively) from this module.
  * Reuse the existing services — do not duplicate algorithms here.
  * Behavior must stay equivalent to the GUI workflows in gui/.
"""

import logging
from pathlib import Path
from typing import Callable, Optional

from core.folder_setup import folder_setup
from core.services.addons import AddonService
from core.services.conflicts import detect_addon_overwrites, scan_for_legacy_conflicts
from core.services.importer import ImportService
from core.services.install import InstallService
from core.services.particles import expand_group_selections
from core.settings import SettingsManager
from core.util.pcf_path_walk import apply_particle_selections, get_mod_particles
from core.util.sourcemod import auto_detect_sourcemod, validate_game_directory
from core.version import VERSION

log = logging.getLogger()

# (percent, message) — same shape the services already use
ProgressCallback = Callable[[int, str], None]


class EngineError(Exception):
    """Raised for operation failures that should surface to the caller."""


class PreloaderEngine:
    """
    Single entry point for all headless preloader functionality.

    Construction is cheap; expensive setup (staging folders, migrations)
    happens in initialize(), mirroring main.py's startup sequence.
    """

    def __init__(self, progress_callback: Optional[ProgressCallback] = None):
        self.progress_callback = progress_callback
        self.settings = SettingsManager()
        self.addons = AddonService(self.settings)
        self.importer = ImportService(self.settings)
        self.installer = InstallService()

    # ───────────────────────── lifecycle ─────────────────────────

    @staticmethod
    def initialize(run_migrations: bool = True) -> Optional[str]:
        """
        Run the same startup steps the GUI runs in main.py: migrations and
        staging-folder preparation. Returns an error message on failure,
        None when the environment is ready.
        """
        try:
            if run_migrations:
                import core.migrations
                core.migrations.migrate()
        except Exception as e:
            log.exception("Migration step failed")
            return f"Migration step failed: {e}"

        from core.backup_manager import prepare_runtime_environment
        return prepare_runtime_environment()

    @staticmethod
    def version() -> str:
        return VERSION

    def _ensure_profile(self) -> None:
        """
        Profile field setters on SettingsManager silently no-op when no profile
        exists (the GUI's first-time setup used to guarantee one). Headless
        consumers get a default profile created on demand instead.
        """
        if not self.settings.get_profiles():
            self.settings.create_profile("Default", self.get_tf_path() or "")

    # ───────────────────────── discovery / validation ─────────────────────────

    @staticmethod
    def discover_game(game_target: str = "Team Fortress 2") -> Optional[str]:
        """Auto-detect the game's mod directory (the tf/ folder for TF2)."""
        return auto_detect_sourcemod(game_target)

    def get_tf_path(self) -> str:
        return self.settings.get_tf_directory()

    def set_tf_path(self, tf_path: str) -> None:
        self._ensure_profile()
        self.settings.set_tf_directory(tf_path)

    def resolve_tf_path(self, tf_path: Optional[str] = None) -> str:
        """Explicit path > active profile path > auto-detection."""
        resolved = tf_path or self.get_tf_path() or self.discover_game()
        if not resolved:
            raise EngineError(
                "No game directory configured and auto-detection failed. "
                "Pass --tf-path or configure a profile first.")
        return resolved

    def validate(self, tf_path: Optional[str] = None) -> dict:
        """
        Validate the target game directory. Returns a report dict; never raises
        for an invalid directory (callers check 'valid').
        """
        resolved = tf_path or self.get_tf_path() or self.discover_game() or ""
        valid = validate_game_directory(resolved)
        report = {
            "tf_path": resolved,
            "valid": valid,
            "is_modified": InstallService.is_modified(resolved) if valid else False,
            "legacy_conflicts": [],
        }
        if valid:
            report["legacy_conflicts"] = scan_for_legacy_conflicts(Path(resolved) / "custom")
        return report

    # ───────────────────────── mod management ─────────────────────────

    def import_mods(
        self,
        paths: list[Path],
        progress_callback: Optional[ProgressCallback] = None,
    ) -> tuple[list[str], list[tuple[str, str]]]:
        """Import VPKs / ZIPs / folders. Returns (successful, [(name, error)])."""
        callback = progress_callback or self.progress_callback
        successful, failed = self.importer.process_dropped_items(paths, progress_callback=callback)
        if successful:
            self.addons.scan_addon_contents()
        return successful, failed

    def remove_mods(self, names: list[str]) -> None:
        """Delete imported mods — regular addons and/or particle mods."""
        from core.util.file import delete

        addon_names = []
        missing = []
        for name in names:
            particle_dir = folder_setup.particles_dir / name
            if (folder_setup.addons_dir / name).is_dir():
                addon_names.append(name)
            elif particle_dir.is_dir():
                delete(particle_dir)
            else:
                missing.append(name)

        if addon_names:
            ok, message = self.addons.delete_addons(addon_names)
            if not ok:
                raise EngineError(message)
            # also drop them from the active profile's selection list
            enabled = [n for n in self.settings.get_addon_selections() if n not in addon_names]
            self._ensure_profile()
            self.settings.set_addon_selections(enabled)

        if missing:
            raise EngineError(f"Mod(s) not found: {', '.join(missing)}")

    def list_addons(self) -> dict[str, list[dict]]:
        """Addons grouped by type, same data the GUI's addon manager shows."""
        self.addons.scan_addon_contents()
        return self.addons.get_addons_grouped()

    def get_enabled_addons(self) -> list[str]:
        """The active profile's load order (top = highest priority)."""
        return self.settings.get_addon_selections()

    def enable_addon(self, addon_dir_name: str) -> None:
        addon_path = folder_setup.addons_dir / addon_dir_name
        if not addon_path.is_dir():
            raise EngineError(f"Addon not found: {addon_dir_name}")
        self._ensure_profile()
        enabled = self.settings.get_addon_selections()
        if addon_dir_name not in enabled:
            enabled.append(addon_dir_name)
            self.settings.set_addon_selections(enabled)

    def disable_addon(self, addon_dir_name: str) -> None:
        enabled = self.settings.get_addon_selections()
        if addon_dir_name in enabled:
            enabled.remove(addon_dir_name)
            self._ensure_profile()
            self.settings.set_addon_selections(enabled)

    def detect_conflicts(self, load_order: Optional[list[str]] = None) -> dict:
        """File-overwrite matrix between addons for the given/active load order."""
        self.addons.scan_addon_contents()
        order = load_order if load_order is not None else self.get_enabled_addons()
        return detect_addon_overwrites(order, self.settings.get_addon_contents())

    # ───────────────────────── particles ─────────────────────────

    def list_particle_mods(self) -> dict[str, list[str]]:
        """Imported particle mods → the particle files each one provides."""
        mod_particles, _ = get_mod_particles()
        return mod_particles

    def _apply_saved_particle_selections(self) -> bool:
        """
        Headless equivalent of the GUI conflict-matrix callback: applies the
        particle selections persisted in the active profile. When no selections
        were ever saved (headless-only usage has no conflict matrix UI), every
        imported particle mod is auto-selected — deterministically, with the
        alphabetically-last mod winning per-particle conflicts.
        """
        simple_mode = self.settings.get_simple_particle_mode()
        selections = (self.settings.get_matrix_selections_simple()
                      if simple_mode else self.settings.get_matrix_selections())
        mod_particles, _ = get_mod_particles()

        if selections:
            # stored selections are already expanded by the GUI before saving;
            # expanding again is a no-op for individual names and handles any
            # group names written by older versions
            selections = expand_group_selections(selections, mod_particles, simple_mode)
        elif mod_particles:
            selections = {}
            for mod_name in sorted(mod_particles):
                for particle in mod_particles[mod_name]:
                    selections[particle] = mod_name
            log.info(f"No saved particle selections — auto-selecting all particles "
                     f"from {len(mod_particles)} imported particle mod(s)")
        else:
            return False

        return apply_particle_selections(selections)

    # ───────────────────────── install / uninstall ─────────────────────────

    def install(
        self,
        tf_path: Optional[str] = None,
        addons: Optional[list[str]] = None,
        progress_callback: Optional[ProgressCallback] = None,
        disable_paint_colors: Optional[bool] = None,
        show_console_on_startup: Optional[bool] = None,
        fix_mdl_paths: Optional[bool] = None,
        skip_quickprecache: Optional[bool] = None,
        game_target: str = "Team Fortress 2",
        rollback_on_failure: bool = True,
    ) -> None:
        """
        Build and install the preload (same single operation as the GUI's
        Install button). Option arguments default to the active profile's
        saved values. Raises EngineError on failure; on failure the game
        directory is restored to a clean state (like InstallController).
        """
        resolved = self.resolve_tf_path(tf_path)
        if not validate_game_directory(resolved):
            raise EngineError(f"Invalid game directory (gameinfo.txt not found): {resolved}")

        selected = addons if addons is not None else self.get_enabled_addons()
        callback = progress_callback or self.progress_callback

        try:
            self.installer.install(
                tf_path=resolved,
                selected_addons=selected,
                on_progress=callback,
                apply_particle_selections=self._apply_saved_particle_selections,
                disable_paint_colors=self._opt(disable_paint_colors, self.settings.get_disable_paint_colors),
                show_console_on_startup=self._opt(show_console_on_startup, self.settings.get_show_console_on_startup),
                fix_mdl_paths=self._opt(fix_mdl_paths, self.settings.get_fix_mdl_paths),
                skip_quickprecache=self._opt(skip_quickprecache, self.settings.get_skip_quickprecache),
                game_target=game_target,
            )
        except Exception as e:
            log.exception("Install failed")
            if not rollback_on_failure:
                raise EngineError(f"Installation failed: {e}") from e
            try:
                self.installer.uninstall(tf_path=resolved, game_target=game_target)
            except Exception as cleanup_error:
                raise EngineError(
                    f"Installation failed and cleanup also failed. "
                    f"Original error: {e}. Cleanup error: {cleanup_error}. "
                    f"Verify game files through Steam.") from e
            raise EngineError(f"Installation failed: {e}. Files were restored to default state.") from e

    def uninstall(
        self,
        tf_path: Optional[str] = None,
        progress_callback: Optional[ProgressCallback] = None,
        game_target: str = "Team Fortress 2",
    ) -> None:
        """Remove all preloader-generated content from the game directory."""
        resolved = self.resolve_tf_path(tf_path)
        try:
            self.installer.uninstall(
                tf_path=resolved,
                on_progress=progress_callback or self.progress_callback,
                game_target=game_target,
            )
        except Exception as e:
            log.exception("Uninstall failed")
            raise EngineError(f"Uninstall failed: {e}") from e

    # ───────────────────────── reporting ─────────────────────────

    def report(self) -> dict:
        """Machine-readable snapshot of engine state."""
        addons_grouped = self.list_addons()
        active_profile = self.settings.get_active_profile()
        return {
            "version": VERSION,
            "portable": folder_setup.portable,
            "install_dir": str(folder_setup.install_dir),
            "project_dir": str(folder_setup.project_dir),
            "settings_dir": str(folder_setup.settings_dir),
            "log_file": str(folder_setup.log_file),
            "active_profile": active_profile.name if active_profile else None,
            "tf_path": self.get_tf_path(),
            "enabled_addons": self.get_enabled_addons(),
            "addons": addons_grouped,
            "particle_mods": self.list_particle_mods(),
            "conflicts": self.detect_conflicts(),
        }

    def compatibility_report(self, addons: Optional[list[str]] = None) -> dict:
        """Scan addons for structural problems that make a custom model load as
        vanilla in Casual (read-only; never mutates). Defaults to the active
        load order. See docs/mdl-ik-patch-prd.md."""
        from core.operations.model_compat import scan_addons
        names = addons if addons is not None else self.get_enabled_addons()
        issues = scan_addons(names)
        return {
            "scanned": names,
            "issues": [
                {"addon": i.addon, "model": i.model, "code": i.code,
                 "severity": i.severity, "message": i.message}
                for i in issues
            ],
        }

    @staticmethod
    def _opt(value, getter):
        return getter() if value is None else value
