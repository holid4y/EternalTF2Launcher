# Engine tools

Standalone diagnostics for the bundled preloader. Stdlib-only, no engine import.

## `model_depscan.py`

Scans a TF2 mod folder (or a single `.mdl`) and prints the full Source-engine
dependency chain of every model: `$includemodel` animation references, material
search paths (`$cdmaterials`) and which materials resolve, the VVD/VTX/PHY
geometry set with checksum verification, and any non-standard paths.

It exists to answer one question without launching the game repeatedly: **is a
custom player model falling back to vanilla because of a broken asset, or not?**
The engine only rejects a studio model (and falls back) on a few conditions:

- a `.vvd`/`.vtx` whose internal checksum does not match the `.mdl` (the set
  was not compiled together), or
- a missing required geometry lump on a model that has render geometry.

Everything else degrades gracefully (purple textures, T-pose) rather than
substituting the vanilla model. The scanner checks the fatal conditions
directly, so a clean run means the asset set is internally valid and a vanilla
result in-game must come from somewhere other than the files.

### Usage

```bash
python tools/model_depscan.py <mod-folder-or-mdl> [--player-only] [--json out.json]
```

- `<mod-folder-or-mdl>` — a folder containing `models/`, or a single `.mdl`.
- `--player-only` — only scan models under `models/player/`.
- `--json PATH` — also write the full structured result.

### Exit codes

| code | meaning |
|------|---------|
| 0 | scan completed, no fatal asset problem found |
| 1 | scan completed, at least one fatal problem (would fall back to vanilla) |
| 2 | usage / IO error |

### Severity legend

- `[X] FATAL` — the engine rejects the model and falls back to vanilla.
- `[!] WARNING` — model loads but content is missing (purple / T-pose).
- `[i] INFO` — noteworthy (relocated path, animation-only model), not a problem.

## `whitelist_policy.py`

Resolves a file path against a real TF2 `sv_pure` whitelist
(`tf/cfg/pure_server_full.txt` etc.), implementing Valve's documented path-spec
grammar (`dir\*.*`, `dir\...`, `dir\file`; later rule wins). Reports whether a
path's policy is `TRUSTED` (custom on-disk copy ignored; signed VPK loads) or
`ANY` (custom may load), and which rule decided it.

```bash
python tools/whitelist_policy.py <whitelist.txt> <query-path> [query-path ...]
python tools/whitelist_policy.py <whitelist.txt> --selftest
```

Use it to answer "is this path pure-restricted?" against the exact whitelist a
server ships, instead of reasoning about the grammar by hand.

## `casual_policy.py`

An **empirical model** of what Valve Casual effectively allows, per asset-path
category, as `LIKELY_WORKS` / `UNCERTAIN` / `LIKELY_BLOCKED`. It is **not** the
server's authoritative whitelist (that's sent over the network and can't be read
locally) — every rule carries an explicit confidence and an evidence basis
(`MEASURED` from a shipped file or a real-match capture, vs `INFERRED`), so a
consumer can show *why* a verdict was reached and never present a guess as fact.

```bash
python tools/casual_policy.py --matrix          # the category matrix
python tools/casual_policy.py <asset-path> ...  # classify specific paths
```

This is the core a "Casual Compatibility" badge in the launcher would build on.
Upgrading any `INFERRED` cell to `MEASURED` requires the per-category
offline-vs-Casual experiment (import one mod of that category, observe).
