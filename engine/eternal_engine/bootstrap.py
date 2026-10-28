"""Points casual-pre-loader at launcher-owned folders.

Upstream resolves its folders by parsing sys.argv the first time
``core.config.config`` is read. A headless host has its own arguments, so the
module attributes are filled in here before anything in ``core`` asks for them.
"""

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import ClassVar

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

    return config


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
