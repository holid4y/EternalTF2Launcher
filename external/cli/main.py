"""
Casual Preloader headless CLI.

Output contract (--json mode):
  * stdout carries newline-delimited JSON (NDJSON) only:
      {"event": "progress", "stage": "...", "percent": 0-100, "message": "..."}
      {"event": "result", "command": "...", "success": bool, "data": {...},
       "warnings": [...], "errors": [...], "durationMs": int, "version": "..."}
  * logging goes to stderr and to the structured log file, never stdout.

Exit codes:
  0  success
  1  operation failed
  2  usage error (argparse)
  3  invalid game directory
  4  environment initialization failed
"""

import argparse
import datetime
import json
import logging
import sys
import time
import traceback
from pathlib import Path

# allow `python cli/main.py` as well as `python -m cli` from the install dir
_INSTALL_DIR = Path(__file__).resolve().parent.parent
if str(_INSTALL_DIR) not in sys.path:
    sys.path.insert(0, str(_INSTALL_DIR))

EXIT_OK = 0
EXIT_FAILURE = 1
EXIT_USAGE = 2
EXIT_INVALID_GAME_DIR = 3
EXIT_ENVIRONMENT = 4

log = logging.getLogger()


class JsonLogFormatter(logging.Formatter):
    """Machine-readable log lines: one JSON object per line."""

    def format(self, record: logging.LogRecord) -> str:
        entry = {
            "ts": datetime.datetime.now().isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        if record.exc_info:
            entry["traceback"] = "".join(traceback.format_exception(*record.exc_info))
        return json.dumps(entry, ensure_ascii=False)


class Emitter:
    """Writes progress/result events to stdout (NDJSON or human-readable)."""

    def __init__(self, as_json: bool, quiet: bool = False):
        self.as_json = as_json
        self.quiet = quiet

    def progress(self, percent: int, message: str):
        if self.as_json:
            self._write({
                "event": "progress",
                "stage": derive_stage(message),
                "percent": int(percent),
                "message": message,
            })
        elif not self.quiet:
            print(f"[{percent:3d}%] {message}", flush=True)

    def result(self, command: str, success: bool, data: dict | None = None,
               warnings: list[str] | None = None, errors: list[str] | None = None,
               duration_ms: int = 0):
        from core.version import VERSION
        if self.as_json:
            self._write({
                "event": "result",
                "command": command,
                "success": success,
                "data": data or {},
                "warnings": warnings or [],
                "errors": errors or [],
                "durationMs": duration_ms,
                "version": VERSION,
            })
        else:
            status = "OK" if success else "FAILED"
            print(f"{command}: {status} ({duration_ms} ms)")
            for w in warnings or []:
                print(f"  warning: {w}")
            for e in errors or []:
                print(f"  error: {e}")
            if data and not self.quiet:
                print(json.dumps(data, indent=2, ensure_ascii=False))

    @staticmethod
    def _write(obj: dict):
        sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
        sys.stdout.flush()


# maps keywords in service progress messages to stable stage identifiers —
# the messages themselves are NOT contractual, the stage names are
_STAGE_KEYWORDS = [
    ("validat", "Validation"),
    ("installing addons", "InstallingAddons"),
    ("sound", "ProcessingSounds"),
    ("particle", "ProcessingParticles"),
    ("paint", "ProcessingPaints"),
    ("custom vpk", "BuildingPreload"),
    ("relocating", "BuildingPreload"),
    ("precache", "Precache"),
    ("configuring", "Configuring"),
    ("finalizing", "Finalizing"),
    ("complete", "Finalizing"),
    ("extract", "Importing"),
    ("processing item", "Importing"),
    ("analyzing", "Importing"),
    ("restoring", "Uninstalling"),
]


def derive_stage(message: str) -> str:
    lowered = (message or "").lower()
    for keyword, stage in _STAGE_KEYWORDS:
        if keyword in lowered:
            return stage
    return "Working"


def setup_logging(log_file: str | None, verbose: bool):
    from core.folder_setup import folder_setup

    target = Path(log_file) if log_file else folder_setup.log_file
    target.parent.mkdir(parents=True, exist_ok=True)

    file_handler = logging.FileHandler(target, mode="a", encoding="utf-8")
    file_handler.setFormatter(JsonLogFormatter())

    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setFormatter(logging.Formatter("%(levelname)s %(message)s"))

    logging.basicConfig(
        level=logging.DEBUG if verbose else logging.INFO,
        handlers=[file_handler, stderr_handler],
        force=True,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="preloader-cli",
        description="Casual Preloader headless CLI",
    )
    parser.add_argument("--json", action="store_true",
                        help="emit NDJSON events on stdout (machine-readable)")
    parser.add_argument("--log-file", metavar="PATH",
                        help="structured log destination (default: preloader log file)")
    parser.add_argument("--verbose", action="store_true", help="debug-level logging")
    parser.add_argument("--quiet", action="store_true",
                        help="suppress progress output in human-readable mode")
    parser.add_argument("--tf-path", metavar="DIR",
                        help="game mod directory (tf/); defaults to the active profile, then auto-detection")
    parser.add_argument("--game-target", default="Team Fortress 2",
                        help='Steam game folder name (default: "Team Fortress 2")')

    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("version", help="print engine version")
    sub.add_parser("analyze", help="environment report: detection, validity, addon counts")
    sub.add_parser("validate", help="validate the target game directory")
    sub.add_parser("report", help="full machine-readable state report")
    sub.add_parser("conflicts", help="addon file-overwrite matrix for the active load order")

    p_compat = sub.add_parser("compat", help="scan addons for model-compatibility problems (IK-chain mismatch, etc.)")
    p_compat.add_argument("--addons", nargs="*", default=None, metavar="NAME",
                          help="addon directory names to scan (default: active profile selections)")

    p_import = sub.add_parser("import", help="import mods (VPK, ZIP, or folder)")
    p_import.add_argument("paths", nargs="+", help="files/folders to import")

    p_remove = sub.add_parser("remove", help="delete imported addons")
    p_remove.add_argument("names", nargs="+", help="addon directory names")

    p_enable = sub.add_parser("enable", help="add addons to the active profile load order")
    p_enable.add_argument("names", nargs="+", help="addon directory names")

    p_disable = sub.add_parser("disable", help="remove addons from the active profile load order")
    p_disable.add_argument("names", nargs="+", help="addon directory names")

    for name, help_text in (("install", "build and install the preload into the game directory"),
                            ("build", "alias of install (build+install is a single atomic operation)")):
        p = sub.add_parser(name, help=help_text)
        p.add_argument("--addons", nargs="*", default=None, metavar="NAME",
                       help="addon directory names to install (default: active profile selections)")
        p.add_argument("--disable-paint-colors", action=argparse.BooleanOptionalAction, default=None)
        p.add_argument("--show-console", action=argparse.BooleanOptionalAction, default=None)
        p.add_argument("--fix-mdl-paths", action=argparse.BooleanOptionalAction, default=None)
        p.add_argument("--skip-quickprecache", action=argparse.BooleanOptionalAction, default=None)
        p.add_argument("--no-rollback", action="store_true",
                       help="do not restore the game directory if installation fails")

    sub.add_parser("uninstall", help="remove all preloader content from the game directory")

    return parser


def run_command(args: argparse.Namespace, emitter: Emitter) -> tuple[bool, dict, list[str], list[str], int]:
    """Returns (success, data, warnings, errors, exit_code)."""
    from core.api import EngineError, PreloaderEngine

    engine = PreloaderEngine(progress_callback=emitter.progress)
    warnings: list[str] = []

    if args.command == "version":
        return True, {"version": engine.version()}, warnings, [], EXIT_OK

    if args.command == "analyze":
        detected = engine.discover_game(args.game_target)
        validation = engine.validate(args.tf_path)
        addons = engine.list_addons()
        from core.folder_setup import folder_setup
        data = {
            "version": engine.version(),
            "portable": folder_setup.portable,
            "install_dir": str(folder_setup.install_dir),
            "project_dir": str(folder_setup.project_dir),
            "settings_dir": str(folder_setup.settings_dir),
            "detected_tf_path": detected,
            "configured_tf_path": engine.get_tf_path(),
            "validation": validation,
            "addon_count": sum(len(v) for v in addons.values()),
            "enabled_addons": engine.get_enabled_addons(),
        }
        return True, data, warnings, [], EXIT_OK

    if args.command == "validate":
        data = engine.validate(args.tf_path)
        if not data["valid"]:
            return False, data, warnings, [f"Invalid game directory: {data['tf_path'] or '(none)'}"], EXIT_INVALID_GAME_DIR
        if data["legacy_conflicts"]:
            warnings.extend(f"Legacy conflict found: {c}" for c in data["legacy_conflicts"])
        return True, data, warnings, [], EXIT_OK

    if args.command == "report":
        return True, engine.report(), warnings, [], EXIT_OK

    if args.command == "conflicts":
        return True, {"conflicts": engine.detect_conflicts()}, warnings, [], EXIT_OK

    if args.command == "compat":
        data = engine.compatibility_report(args.addons)
        warnings.extend(i["message"] for i in data["issues"])
        return True, data, warnings, [], EXIT_OK

    if args.command == "import":
        paths = [Path(p) for p in args.paths]
        missing = [str(p) for p in paths if not p.exists()]
        if missing:
            return False, {}, warnings, [f"Path not found: {m}" for m in missing], EXIT_USAGE
        # particle mod preprocessing reads the vanilla staging folders
        init_error = engine.initialize()
        if init_error:
            return False, {}, warnings, [init_error], EXIT_ENVIRONMENT
        successful, failed = engine.import_mods(paths)
        data = {
            "imported": successful,
            "failed": [{"name": name, "error": error} for name, error in failed],
        }
        success = not failed
        errors = [f"{name}: {error}" for name, error in failed]
        return success, data, warnings, errors, EXIT_OK if success else EXIT_FAILURE

    if args.command == "remove":
        engine.remove_mods(args.names)
        return True, {"removed": args.names}, warnings, [], EXIT_OK

    if args.command == "enable":
        for name in args.names:
            engine.enable_addon(name)
        return True, {"enabled_addons": engine.get_enabled_addons()}, warnings, [], EXIT_OK

    if args.command == "disable":
        for name in args.names:
            engine.disable_addon(name)
        return True, {"enabled_addons": engine.get_enabled_addons()}, warnings, [], EXIT_OK

    if args.command in ("install", "build"):
        init_error = engine.initialize()
        if init_error:
            return False, {}, warnings, [init_error], EXIT_ENVIRONMENT
        validation = engine.validate(args.tf_path)
        if not validation["valid"]:
            return False, validation, warnings, [f"Invalid game directory: {validation['tf_path'] or '(none)'}"], EXIT_INVALID_GAME_DIR
        if validation["legacy_conflicts"]:
            warnings.extend(f"Legacy conflict found: {c}" for c in validation["legacy_conflicts"])

        # Surface model-compatibility problems (e.g. IK-chain mismatch) so a
        # mod that will fall back to vanilla in Casual is reported, never silent.
        try:
            compat = engine.compatibility_report(args.addons)
            warnings.extend(i["message"] for i in compat["issues"])
        except Exception:
            log.exception("compatibility scan failed (non-fatal)")

        engine.install(
            tf_path=validation["tf_path"],
            addons=args.addons,
            disable_paint_colors=args.disable_paint_colors,
            show_console_on_startup=args.show_console,
            fix_mdl_paths=args.fix_mdl_paths,
            skip_quickprecache=args.skip_quickprecache,
            game_target=args.game_target,
            rollback_on_failure=not args.no_rollback,
        )
        installed = args.addons if args.addons is not None else engine.get_enabled_addons()
        return True, {"tf_path": validation["tf_path"], "installed_addons": installed}, warnings, [], EXIT_OK

    if args.command == "uninstall":
        init_error = engine.initialize()
        if init_error:
            return False, {}, warnings, [init_error], EXIT_ENVIRONMENT
        resolved = engine.resolve_tf_path(args.tf_path)
        engine.uninstall(tf_path=resolved, game_target=args.game_target)
        return True, {"tf_path": resolved}, warnings, [], EXIT_OK

    return False, {}, warnings, [f"Unknown command: {args.command}"], EXIT_USAGE


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    setup_logging(args.log_file, args.verbose)
    emitter = Emitter(as_json=args.json, quiet=args.quiet)

    started = time.monotonic()
    try:
        success, data, warnings, errors, exit_code = run_command(args, emitter)
    except Exception as e:
        from core.api import EngineError
        if isinstance(e, EngineError):
            # expected operation failure — no traceback noise
            log.error(f"Command '{args.command}' failed: {e}")
        else:
            log.exception(f"Command '{args.command}' crashed")
        duration_ms = int((time.monotonic() - started) * 1000)
        emitter.result(args.command, False, errors=[str(e)], duration_ms=duration_ms)
        return EXIT_FAILURE

    duration_ms = int((time.monotonic() - started) * 1000)
    emitter.result(args.command, success, data=data, warnings=warnings,
                   errors=errors, duration_ms=duration_ms)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
