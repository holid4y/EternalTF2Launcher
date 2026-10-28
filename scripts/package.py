#!/usr/bin/env python3
"""Build a release folder and archive for one platform.

    python scripts/package.py --rid linux-x64 --freeze-engine
    python scripts/package.py --rid win-x64 --freeze-engine --portable

--freeze-engine bundles the engine as a standalone executable (needs
PyInstaller and the engine requirements in the running Python, and must run
on the target OS). Without it the engine ships as Python sources and the
launcher sets up its own virtual environment on first use.
"""

import argparse
import re
import shutil
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "Eternal.App" / "Eternal.App.csproj"
ENGINE = ROOT / "engine"
LINUX = ROOT / "packaging" / "linux"


def version() -> str:
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    return match.group(1) if match else "0.0.0"


def copy_engine_sources(target: Path) -> None:
    ignore = shutil.ignore_patterns("__pycache__", "*.pyc", "tests", ".venv")
    target.mkdir(parents=True, exist_ok=True)
    for name in ("run.py", "requirements.txt"):
        shutil.copy2(ENGINE / name, target / name)
    shutil.copytree(ENGINE / "eternal_engine", target / "eternal_engine", ignore=ignore)
    shutil.copytree(ENGINE / "vendor", target / "vendor", ignore=ignore)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", choices=["linux-x64", "win-x64"], required=True)
    parser.add_argument("--freeze-engine", action="store_true")
    parser.add_argument("--portable", action="store_true", help="also write a portable archive (Windows)")
    args = parser.parse_args()

    out = ROOT / "artifacts" / args.rid
    stage = out / "EternalLauncher"
    shutil.rmtree(out, ignore_errors=True)

    subprocess.run([
        "dotnet", "publish", str(APP), "-c", "Release", "-r", args.rid, "--self-contained",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=embedded", "-o", str(stage),
    ], check=True)

    engine = stage / "engine"
    if args.freeze_engine:
        subprocess.run([sys.executable, str(ROOT / "scripts" / "freeze-engine.py"), "--out", str(engine)], check=True)
    else:
        copy_engine_sources(engine)

    shutil.copy2(ROOT / "LICENSE", stage / "LICENSE.txt")
    shutil.copy2(ENGINE / "vendor" / "casual-pre-loader" / "LICENSE", stage / "LICENSE-casual-pre-loader.txt")
    shutil.copy2(ROOT / "src" / "Eternal.App" / "Assets" / "Fonts" / "OFL.txt", stage / "LICENSE-fonts.txt")

    name = f"EternalLauncher-{version()}-{args.rid}"
    if args.rid == "linux-x64":
        shutil.copy2(LINUX / "eternal-launcher.desktop", stage / "eternal-launcher.desktop")
        shutil.copy2(LINUX / "install.sh", stage / "install.sh")
        shutil.copy2(ROOT / "resources" / "Assets" / "logo_classic.png", stage / "eternal-launcher.png")
        (stage / "install.sh").chmod(0o755)
        archive = out / f"{name}.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(stage, arcname="EternalLauncher")
        print(archive)
        return

    archive = out / f"{name}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zipped:
        for file in stage.rglob("*"):
            zipped.write(file, Path("EternalLauncher") / file.relative_to(stage))
    print(archive)

    if args.portable:
        portable = out / f"{name}-portable.zip"
        shutil.copy2(archive, portable)
        with zipfile.ZipFile(portable, "a") as zipped:
            zipped.writestr("EternalLauncher/portable.txt", "Keep settings and mods in ./userdata next to the launcher.\n")
        print(portable)


if __name__ == "__main__":
    main()
