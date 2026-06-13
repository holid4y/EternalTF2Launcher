# Preloader Integration Contract

Contract version: **1.0**
Engine: Casual Preloader ≥ 2.2.1 (vendored in `external/`)
Consumer: Eternal Launcher (`Services/PreloaderService.cs`)

Eternal Launcher communicates with Casual Preloader **only** through this CLI + JSON
contract. It never imports Python internals.

---

## Invocation

```text
python -m cli [global options] <command> [command options]
```

- Working directory **must** be the preloader install dir (`external/` in this repo) so
  relative bundled data resolves; alternatively `cli/main.py` self-inserts its parent
  directory into `sys.path`, so `python <install>/cli/main.py ...` also works from any cwd.
- A frozen build (`preloader-cli.exe`) is invocation-compatible: same arguments, same output.

### Global options

| Option | Meaning |
|---|---|
| `--json` | Machine mode: stdout carries NDJSON events only (REQUIRED for integration) |
| `--log-file PATH` | Structured JSON-lines log destination (default: engine's own log file) |
| `--tf-path DIR` | Game mod directory (`.../Team Fortress 2/tf`). Fallback: active engine profile, then auto-detection |
| `--game-target NAME` | Steam game folder name, default `Team Fortress 2` |
| `--verbose` | Debug-level logging |
| `--quiet` | Suppress progress prints (human mode only) |

## Commands

| Command | Effect | `data` payload |
|---|---|---|
| `version` | none | `{"version": "2.2.1"}` |
| `analyze` | scans addon metadata | environment report: paths, `detected_tf_path`, `configured_tf_path`, `validation`, `addon_count`, `enabled_addons` |
| `validate` | none | `{"tf_path", "valid", "is_modified", "legacy_conflicts": []}` |
| `report` | scans addon metadata | full state: version, dirs, `active_profile`, `tf_path`, `enabled_addons`, `addons` (grouped by type), `particle_mods` (mod → particle files), `conflicts` |
| `conflicts` | none | `{"conflicts": {addon: {overwritten_addon: [files]}}}` |
| `import <paths...>` | imports VPK/ZIP/folder mods into the engine library (initializes the staging environment first) | `{"imported": [names], "failed": [{"name", "error"}]}` |
| `remove <names...>` | deletes mods from the library + load order — handles both regular addons and particle mods | `{"removed": [names]}` |
| `enable <names...>` | appends addons to the active load order | `{"enabled_addons": [...]}` |
| `disable <names...>` | removes addons from the active load order | `{"enabled_addons": [...]}` |
| `install` | builds **and** installs the preload into the game dir (atomic; rolls back on failure unless `--no-rollback`) | `{"tf_path", "installed_addons"}` |
| `build` | alias of `install` — build+install is a single atomic operation in this engine | same as `install` |
| `uninstall` | restores the game dir to a clean state | `{"tf_path"}` |

### `install` / `build` options

`--addons NAME...` (default: active profile selections) ·
`--disable-paint-colors/--no-disable-paint-colors` ·
`--show-console/--no-show-console` ·
`--fix-mdl-paths/--no-fix-mdl-paths` ·
`--skip-quickprecache/--no-skip-quickprecache` ·
`--no-rollback`

Tri-state flags default to the active engine profile's saved values when omitted.

**Precondition:** TF2 must not be running (the engine checks VPK writability and fails
with a clear error otherwise).

**Particle mods:** particle mods (imported VPKs/folders containing `.pcf` files) have no
enable toggle — when the active profile has no saved particle matrix selections (the
normal headless case), `install` automatically applies every particle from every
imported particle mod; per-particle conflicts resolve deterministically to the
alphabetically-last mod name.

**Profiles:** commands that write profile state (`enable`, `disable`) auto-create a
"Default" profile when none exists.

## Output stream (`--json`)

Newline-delimited JSON on **stdout**. Two event types:

```json
{"event": "progress", "stage": "ProcessingParticles", "percent": 62, "message": "Processing particle files... (5/12)"}
```

```json
{"event": "result", "command": "install", "success": true, "data": {…},
 "warnings": [], "errors": [], "durationMs": 4321, "version": "2.2.1"}
```

- Exactly one `result` event is emitted, always last, even on crash.
- `stage` is one of a **stable set**: `Validation`, `Importing`, `InstallingAddons`,
  `ProcessingSounds`, `ProcessingParticles`, `ProcessingPaints`, `BuildingPreload`,
  `Precache`, `Configuring`, `Finalizing`, `Uninstalling`, `Working` (fallback).
  Consumers must tolerate unknown stages (render as generic progress).
- `message` is human-readable free text — display it, never parse it.
- stderr carries log lines only; consumers should capture it for diagnostics, never parse it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | operation failed (details in `errors`) |
| 2 | usage error (bad arguments; argparse may print to stderr without a `result` event) |
| 3 | invalid game directory |
| 4 | environment initialization failed (staging/bundled files broken) |

The `result` event is authoritative for failure details; the exit code is authoritative
for success/failure classification.

## Logging

`--log-file` writes JSON lines:

```json
{"ts": "2026-06-10T14:03:22.117", "level": "INFO", "logger": "root", "message": "...", "traceback": "..."}
```

Eternal Launcher passes `--log-file <launcher AppData>/logs/preloader.log`.

## Versioning & backward compatibility

- Every `result` carries the engine `version` (semver).
- This contract is **additive-only** within major engine version 2.x:
  - new fields, commands, and stages may be added;
  - existing fields, command semantics, stage names, and exit codes will not change meaning;
  - consumers must ignore unknown fields/stages.
- A breaking change requires a new contract version documented here and a major
  engine version bump.

## Consumer responsibilities (Eternal Launcher)

1. Always pass `--json` and `--tf-path` explicitly.
2. Read stdout line-by-line; dispatch on `event`.
3. Treat a missing `result` event (process died) as failure.
4. Surface `warnings` to the user non-fatally.
5. Never invoke the engine concurrently with itself or while TF2 runs.
