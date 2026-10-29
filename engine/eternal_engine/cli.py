"""Command line front end used by the launcher.

With --json, stdout carries one JSON object per line and nothing else:

    {"event": "progress", "stage": "...", "percent": 0-100, "message": "..."}
    {"event": "result", "command": "...", "success": bool, "data": {...},
     "warnings": [...], "errors": [...], "durationMs": int, "version": "..."}

Anything upstream prints is diverted to stderr. Exit codes: 0 ok, 1 failed,
2 usage, 3 invalid game folder, 4 broken environment.
"""

import argparse
import importlib
import json
import logging
import os
import sys
import time
import traceback
from datetime import datetime
from pathlib import Path
from typing import TextIO

from . import ADAPTER_VERSION

EXIT_OK, EXIT_FAILED, EXIT_USAGE, EXIT_GAME_DIR, EXIT_ENVIRONMENT = 0, 1, 2, 3, 4

DEPENDENCIES = ("valve_parsers", "cappa", "packaging", "more_itertools", "platformdirs")

STAGES = (
    ("validat", "Validating"),
    ("installing addons", "CopyingFiles"),
    ("sound", "Sounds"),
    ("particle", "Particles"),
    ("paint", "Paints"),
    ("custom vpk", "Packing"),
    ("relocating", "Packing"),
    ("precache", "Precache"),
    ("configuring", "Finishing"),
    ("finalizing", "Finishing"),
    ("complete", "Finishing"),
    ("extract", "Importing"),
    ("processing item", "Importing"),
    ("analyzing", "Importing"),
    ("restor", "Restoring"),
)

log = logging.getLogger("eternal_engine")


def stage_of(message: str) -> str:
    lowered = (message or "").lower()
    return next((stage for key, stage in STAGES if key in lowered), "Working")


class Emitter:
    def __init__(self, stream: TextIO, as_json: bool):
        self.stream = stream
        self.as_json = as_json
        self._last = (-1, "")

    def _write(self, payload: dict) -> None:
        self.stream.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self.stream.flush()

    def progress(self, percent: int, message: str) -> None:
        percent = max(0, min(100, int(percent)))
        if (percent, message) == self._last:
            return
        self._last = (percent, message)
        if self.as_json:
            self._write({"event": "progress", "stage": stage_of(message), "percent": percent, "message": message})
        else:
            print(f"[{percent:3d}%] {message}", file=self.stream, flush=True)

    def result(self, command: str, success: bool, data: dict, warnings: list[str], errors: list[str],
               duration_ms: int) -> None:
        if self.as_json:
            self._write({
                "event": "result",
                "command": command,
                "success": success,
                "data": data,
                "warnings": warnings,
                "errors": errors,
                "durationMs": duration_ms,
                "version": ADAPTER_VERSION,
            })
            return
        print(f"{command}: {'ok' if success else 'FAILED'} ({duration_ms} ms)", file=self.stream)
        for line in warnings:
            print(f"  warning: {line}", file=self.stream)
        for line in errors:
            print(f"  error: {line}", file=self.stream)
        if data:
            print(json.dumps(data, indent=2, ensure_ascii=False), file=self.stream)


class JsonLogFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        entry = {
            "ts": datetime.now().isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        if record.exc_info:
            entry["traceback"] = "".join(traceback.format_exception(*record.exc_info))
        return json.dumps(entry, ensure_ascii=False)


def setup_logging(log_file: Path | None, verbose: bool) -> None:
    stderr = logging.StreamHandler(sys.stderr)
    stderr.setLevel(logging.DEBUG if verbose else logging.WARNING)
    stderr.setFormatter(logging.Formatter("%(levelname)s %(name)s: %(message)s"))
    handlers: list[logging.Handler] = [stderr]
    if log_file:
        try:
            log_file.parent.mkdir(parents=True, exist_ok=True)
            handler = logging.FileHandler(log_file, encoding="utf-8")
            handler.setFormatter(JsonLogFormatter())
            handlers.append(handler)
        except OSError as exc:
            print(f"cannot open log file {log_file}: {exc}", file=sys.stderr)
    logging.basicConfig(level=logging.DEBUG if verbose else logging.INFO, handlers=handlers, force=True)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="eternal-engine", description="Headless casual-pre-loader for Eternal Launcher")
    parser.add_argument("--json", action="store_true", help="emit NDJSON events on stdout")
    parser.add_argument("--log-file", type=Path)
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--data-dir", type=Path, required=True, help="writable folder for the mod library")
    parser.add_argument("--cache-dir", type=Path, help="scratch space (default: <data-dir>/temp)")

    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("version")
    sub.add_parser("library")
    sub.add_parser("recover")

    doctor = sub.add_parser("doctor")
    doctor.add_argument("--tf-path", type=Path)

    conflicts = sub.add_parser("conflicts")
    conflicts.add_argument("--addon", dest="addons", action="append", default=[])
    conflicts.add_argument("--particle", dest="particles", action="append", default=[])

    compat = sub.add_parser("compat")
    compat.add_argument("--addon", dest="addons", action="append", default=[])

    importer = sub.add_parser("import")
    importer.add_argument("paths", nargs="*", type=Path)
    importer.add_argument("--extracted", action="append", type=Path, default=[],
                          help="folder unpacked from an archive; its name is used as the mod name")

    remove = sub.add_parser("remove")
    remove.add_argument("--addon", action="append", default=[])
    remove.add_argument("--particle", action="append", default=[])

    install = sub.add_parser("install")
    install.add_argument("--tf-path", type=Path, required=True)
    install.add_argument("--addon", dest="addons", action="append", default=[],
                         help="enabled addon, highest priority first (repeatable)")
    install.add_argument("--particle", dest="particles", action="append", default=[],
                         help="enabled particle mod, highest priority first (repeatable)")
    install.add_argument("--disable-paint-colors", action="store_true")
    install.add_argument("--hide-console", action="store_true")
    install.add_argument("--no-fix-mdl-paths", action="store_true")
    install.add_argument("--skip-quickprecache", action="store_true")

    uninstall = sub.add_parser("uninstall")
    uninstall.add_argument("--tf-path", type=Path, required=True)

    migrate = sub.add_parser("migrate-userdata")
    migrate.add_argument("--from", dest="source", type=Path, required=True)

    return parser


def doctor(args, config_error: str | None) -> tuple[bool, dict, list[str], list[str], int]:
    from .bootstrap import upstream_info
    from .build import status, wine_available

    deps = {}
    for name in DEPENDENCIES:
        try:
            importlib.import_module(name)
            deps[name] = True
        except Exception:
            deps[name] = False

    data = {
        "python": ".".join(map(str, sys.version_info[:3])),
        "python_ok": sys.version_info >= (3, 12),
        "frozen": bool(getattr(sys, "frozen", False)),
        "platform": sys.platform,
        "adapter": ADAPTER_VERSION,
        "dependencies": deps,
        "wine": wine_available(),
        "data_dir": str(args.data_dir),
        "writable": os.access(args.data_dir, os.W_OK),
        "upstream": upstream_info() if config_error is None else {},
    }
    errors = []
    if not data["python_ok"]:
        errors.append(f"Python {data['python']} is too old; the preloader needs 3.12 or newer.")
    missing = [name for name, ok in deps.items() if not ok]
    if missing:
        errors.append(f"Missing Python packages: {', '.join(missing)}")
    if config_error:
        errors.append(config_error)
    else:
        data.update(status(args.tf_path))

    return not errors, data, [], errors, EXIT_OK if not errors else EXIT_ENVIRONMENT


def run(args, progress) -> tuple[bool, dict, list[str], list[str], int]:
    from . import build, compat, library

    command = args.command

    if command == "version":
        from .bootstrap import upstream_info

        return True, {"adapter": ADAPTER_VERSION, "upstream": upstream_info(),
                      "python": ".".join(map(str, sys.version_info[:3]))}, [], [], EXIT_OK

    if command == "library":
        return True, library.list_library(), [], [], EXIT_OK

    if command == "conflicts":
        return True, library.conflicts(args.addons, args.particles), [], [], EXIT_OK

    if command == "compat":
        from core.config import config

        issues = compat.scan(config.addons_dir, args.addons)
        return True, {"issues": [vars(issue) for issue in issues]}, [i.message for i in issues], [], EXIT_OK

    if command == "import":
        if not args.paths and not args.extracted:
            return False, {}, [], ["Nothing to import."], EXIT_USAGE
        build.journal()  # make sure the data dir is initialised before staging writes
        results = library.import_items(args.paths, args.extracted, progress)
        errors = [f"{r['source']}: {r['message']}" for r in results if not r["ok"]]
        # Partial success is still success for the items that worked; callers read per-item results.
        return any(r["ok"] for r in results), {"results": results}, [], errors, EXIT_OK if not errors else EXIT_FAILED

    if command == "remove":
        removed, missing = library.remove(args.addon, args.particle)
        errors = [f"Not in the library: {name}" for name in missing]
        return not missing, {"removed": removed}, [], errors, EXIT_OK if not missing else EXIT_FAILED

    if command == "install":
        options = build.BuildOptions(
            disable_paint_colors=args.disable_paint_colors,
            show_console=not args.hide_console,
            fix_mdl_paths=not args.no_fix_mdl_paths,
            skip_quickprecache=args.skip_quickprecache,
        )
        data, warnings = build.install(args.tf_path, args.addons, args.particles, options, progress)
        try:
            for issue in compat.scan(Path(build._config().addons_dir), args.addons):
                warnings.append(issue.message)
        except Exception:
            log.exception("compat scan after install failed")
        return True, data, warnings, [], EXIT_OK

    if command == "uninstall":
        return True, build.uninstall(args.tf_path, progress), [], [], EXIT_OK

    if command == "recover":
        return True, build.recover(progress), [], [], EXIT_OK

    if command == "migrate-userdata":
        from core.services.setup import import_userdata

        ok, warnings = import_userdata(args.source.expanduser().resolve())
        if not ok:
            return False, {"source": str(args.source)}, [], warnings, EXIT_FAILED
        enabled: list[str] = []
        try:
            from core.settings import settings

            enabled = [str(name) for name in settings.addon_selections]
        except Exception:
            log.warning("imported userdata has no readable load order", exc_info=True)
        return True, {"source": str(args.source), "enabled": enabled}, warnings, [], EXIT_OK

    return False, {}, [], [f"Unknown command {command}"], EXIT_USAGE


def main(argv: list[str] | None = None) -> int:
    # The launcher reads UTF-8; on Windows a pipe would get the ANSI code page,
    # which can't encode most mod names.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)

    real_stdout = sys.stdout
    sys.stdout = sys.stderr
    emitter = Emitter(real_stdout, args.json)
    setup_logging(args.log_file, args.verbose)

    started = time.monotonic()
    elapsed = lambda: int((time.monotonic() - started) * 1000)  # noqa: E731

    from . import bootstrap
    from .build import BuildError
    from .library import LibraryError

    config_error = None
    try:
        args.data_dir = args.data_dir.expanduser().resolve()
        bootstrap.configure(args.data_dir, args.cache_dir or args.data_dir / "temp", args.verbose)
    except Exception as exc:
        log.exception("engine environment failed to initialise")
        config_error = f"Engine environment failed to initialise: {exc}"

    try:
        if args.command == "doctor":
            success, data, warnings, errors, code = doctor(args, config_error)
        elif config_error:
            success, data, warnings, errors, code = False, {}, [], [config_error], EXIT_ENVIRONMENT
        else:
            success, data, warnings, errors, code = run(args, emitter.progress)
    except (BuildError, LibraryError) as exc:
        log.error("%s failed: %s", args.command, exc)
        invalid_dir = "not a TF2 game folder" in str(exc)
        success, data, warnings, errors = False, {}, [], [str(exc)]
        if isinstance(exc, BuildError):
            data = {"game_files": exc.game_files}
        code = EXIT_GAME_DIR if invalid_dir else EXIT_FAILED
    except Exception as exc:
        log.exception("%s crashed", args.command)
        success, data, warnings, errors, code = False, {}, [], [f"{type(exc).__name__}: {exc}"], EXIT_FAILED

    emitter.result(args.command, success, data, warnings, errors, elapsed())
    sys.stdout = real_stdout
    return code
