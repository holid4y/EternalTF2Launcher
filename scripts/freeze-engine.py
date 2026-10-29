#!/usr/bin/env python3
"""Freeze the engine into a self-contained folder with PyInstaller.

Release builds ship this folder as engine/ next to the launcher, so players
need neither Python nor pip. Run it with the Python that has the engine's
requirements and PyInstaller installed:

    python -m pip install -r engine/requirements.txt
    python -m pip install pyinstaller
    python scripts/freeze-engine.py --out build/engine
"""

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ENGINE = ROOT / "engine"
VENDOR = ENGINE / "vendor" / "casual-pre-loader"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=ROOT / "build" / "engine")
    args = parser.parse_args()

    work = ROOT / "build" / "pyinstaller"
    shutil.rmtree(work, ignore_errors=True)
    sep = os.pathsep

    # Upstream resolves its data relative to core/config.py, which PyInstaller
    # places at the bundle root, so the data folders go there as well.
    data = [
        (VENDOR / "backup", "backup"),
        (VENDOR / "data", "data"),
        (VENDOR / "core" / "quickprecache" / "_QuickPrecache.vpk", "core/quickprecache"),
        (VENDOR / "UPSTREAM", "."),
        (VENDOR / ".noportable", "."),
        (VENDOR / "LICENSE", "."),
    ]
    # Room for an optional studiomdl.exe fallback; empty upstream, and git keeps no empty folders.
    studio = VENDOR / "core" / "quickprecache" / "studio"
    if studio.is_dir() and any(studio.iterdir()):
        data.append((studio, "core/quickprecache/studio"))
    command = [
        sys.executable, "-m", "PyInstaller", str(ENGINE / "run.py"),
        "--name", "eternal-engine",
        "--onedir", "--noconfirm", "--clean", "--console",
        "--distpath", str(work / "dist"),
        "--workpath", str(work / "work"),
        "--specpath", str(work),
        "--paths", str(ENGINE),
        "--paths", str(VENDOR),
        "--collect-submodules", "core",
        "--collect-submodules", "eternal_engine",
        "--exclude-module", "PyQt6",
        "--exclude-module", "github",
        "--exclude-module", "tkinter",
    ]
    for source, target in data:
        command += ["--add-data", f"{source}{sep}{target}"]

    subprocess.run(command, check=True, cwd=ROOT)

    out = args.out.resolve()
    shutil.rmtree(out, ignore_errors=True)
    out.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(work / "dist" / "eternal-engine", out)
    print(f"frozen engine written to {out}")


if __name__ == "__main__":
    main()
