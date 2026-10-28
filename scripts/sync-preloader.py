#!/usr/bin/env python3
"""Vendor cueki/casual-pre-loader into engine/vendor/casual-pre-loader.

Only upstream's engine files are copied (core/, data/, backup/ and the
license). Nothing under vendor/ is edited by hand: launcher-specific behaviour
lives in engine/eternal_engine, so updating is just running this script again.

    python scripts/sync-preloader.py                  # latest main
    python scripts/sync-preloader.py --ref v2.3.1     # a tag, branch or commit
    python scripts/sync-preloader.py --source ~/src/casual-pre-loader
"""

import argparse
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import UTC, datetime
from pathlib import Path

REPO_URL = "https://github.com/cueki/casual-pre-loader.git"
ROOT = Path(__file__).resolve().parent.parent
VENDOR = ROOT / "engine" / "vendor" / "casual-pre-loader"
COPIED = ("core", "data", "backup", "LICENSE", "README.md")
IGNORED = shutil.ignore_patterns("__pycache__", "*.pyc", "*.pyo")


def git(*args: str, cwd: Path | None = None) -> str:
    result = subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)
    return result.stdout.strip()


def checkout(ref: str, workdir: Path) -> Path:
    clone = workdir / "casual-pre-loader"
    git("clone", "--quiet", "--filter=blob:none", REPO_URL, str(clone))
    git("checkout", "--quiet", ref, cwd=clone)
    return clone


def copy_tree(source: Path) -> None:
    missing = [name for name in COPIED if not (source / name).exists()]
    if missing:
        sys.exit(f"{source} does not look like casual-pre-loader (missing {', '.join(missing)})")

    staging = VENDOR.with_name(VENDOR.name + ".new")
    shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True)

    for name in COPIED:
        src = source / name
        if src.is_dir():
            shutil.copytree(src, staging / name, ignore=IGNORED)
        else:
            shutil.copy2(src, staging / name)

    # Upstream's own switch for packagers: keeps userdata out of the install dir.
    (staging / ".noportable").touch()

    version = re.search(r"VERSION\s*=\s*['\"]([^'\"]+)", (source / "core" / "version.py").read_text())
    commit = git("rev-parse", "HEAD", cwd=source)
    committed = git("show", "-s", "--format=%cI", "HEAD", cwd=source)
    (staging / "UPSTREAM").write_text(
        f"repository: {REPO_URL}\n"
        f"commit: {commit}\n"
        f"committed: {committed}\n"
        f"version: {version.group(1) if version else 'unknown'}\n"
        f"synced: {datetime.now(UTC).isoformat(timespec='seconds')}\n",
        encoding="utf-8",
    )

    shutil.rmtree(VENDOR, ignore_errors=True)
    staging.rename(VENDOR)
    print(f"vendored casual-pre-loader {commit[:10]} into {VENDOR.relative_to(ROOT)}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ref", default="main", help="tag, branch or commit to vendor (default: main)")
    parser.add_argument("--source", type=Path, help="use an existing local clone instead of cloning")
    args = parser.parse_args()

    if args.source:
        copy_tree(args.source.expanduser().resolve())
        return

    with tempfile.TemporaryDirectory() as tmp:
        copy_tree(checkout(args.ref, Path(tmp)))


if __name__ == "__main__":
    main()
