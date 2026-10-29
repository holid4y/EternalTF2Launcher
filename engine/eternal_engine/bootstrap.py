"""Points casual-pre-loader at launcher-owned folders and applies the adapter's fixes.

Upstream resolves its folders by parsing sys.argv the first time
``core.config.config`` is read. A headless host has its own arguments, so the
module attributes are filled in here before anything in ``core`` asks for them.
"""

import logging
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import ClassVar

from . import hud, precache

log = logging.getLogger(__name__)
_files_hardened = False

if getattr(sys, "frozen", False):
    VENDOR_DIR = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
else:
    VENDOR_DIR = Path(__file__).resolve().parent.parent / "vendor" / "casual-pre-loader"


def configure(data_dir: Path, cache_dir: Path, verbose: bool = False):
    if str(VENDOR_DIR) not in sys.path:
        sys.path.insert(0, str(VENDOR_DIR))

    import core.config as upstream

    project = data_dir.resolve()
    temp = cache_dir.resolve()

    @dataclass
    class Folders(upstream.FolderConfig):
        project_dir: ClassVar[Path] = project
        settings_dir: ClassVar[Path] = project / "config"
        temp_dir: ClassVar[Path] = temp

    @dataclass
    class Config(upstream.Args, Folders):
        pass

    config = Config()
    if hasattr(config, "verbose"):
        config.verbose = verbose

    upstream.FolderConfig = Folders
    upstream.Config = Config
    upstream.config = config
    upstream.subcommand = None

    for folder in (project, project / "config", temp, config.mods_dir, config.addons_dir, config.particles_dir):
        folder.mkdir(parents=True, exist_ok=True)

    _harden_files()
    hud.install()
    precache.install()
    return config


def _harden_files() -> None:
    """Upstream rewrites its JSON files in place. A query the launcher stops can end
    halfway through a write, and a cut-off file then breaks every later command."""
    global _files_hardened
    if _files_hardened:
        return
    from core.settings import AddonMetadata, Settings

    AddonMetadata.save = _write_aside(AddonMetadata.save, "addon_metadata_file")
    Settings.save_settings = _write_aside(Settings.save_settings, "app_settings_file")
    AddonMetadata.load = _skip_cut_off(AddonMetadata.load, "addon_metadata_file")
    Settings._load_settings = _skip_cut_off(Settings._load_settings, "app_settings_file")
    _files_hardened = True


def _write_aside(save, attr: str):
    # The save method reads its path from config, so point it at a temporary file and swap it in after.
    def write(self, *args, **kwargs):
        from core.config import config

        real = getattr(config, attr)
        overridden = attr in vars(config)
        temp = real.with_name(f"{real.name}.tmp")
        setattr(config, attr, temp)
        try:
            result = save(self, *args, **kwargs)
        except BaseException:
            temp.unlink(missing_ok=True)
            raise
        finally:
            if overridden:
                setattr(config, attr, real)
            else:
                delattr(config, attr)
        os.replace(temp, real)
        return result

    return write


def _skip_cut_off(load, attr: str):
    def read(self, *args, **kwargs):
        from core.config import config

        try:
            return load(self, *args, **kwargs)
        except ValueError:
            if args or kwargs:
                raise
            path = getattr(config, attr)
            log.warning("%s could not be read; kept it as %s.bad and started fresh", path.name, path.name)
            os.replace(path, path.with_name(f"{path.name}.bad"))
            return load(self)

    return read


def upstream_info() -> dict:
    info = {}
    marker = VENDOR_DIR / "UPSTREAM"
    if marker.is_file():
        for line in marker.read_text(encoding="utf-8").splitlines():
            key, _, value = line.partition(":")
            if value:
                info[key.strip()] = value.strip()
    try:
        from core.version import VERSION

        info.setdefault("version", VERSION)
    except ImportError:
        pass
    return info
