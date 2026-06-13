#!/usr/bin/env python3
"""
whitelist_policy — resolve a file path against a TF2 sv_pure whitelist.

Parses a real `pure_server_*.txt` whitelist and reports, for a queried path,
which rule matches and whether the resulting policy is TRUSTED (must come from a
signed VPK — custom on-disk copies are ignored) or ANY (custom may load).

It implements Valve's documented grammar exactly (see the header comments in
`pure_server_whitelist_example.txt`):

  * directory\\*.*   - all files directly under the directory (one level)
  * directory\\...   - all files under the directory, recursively
  * directory\\file  - a single file
  * "If multiple rules apply, the rule listed later takes priority."

Mode aliases (also per Valve's file):
  TRUSTED := trusted_source | from_steam | check_crc | allow_from_disk+check_crc
  ANY     := any | allow_from_disk

Usage:
  python whitelist_policy.py <whitelist.txt> <query-path> [<query-path> ...]
  python whitelist_policy.py <whitelist.txt> --selftest
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

_TRUSTED = {"trusted_source", "from_steam", "check_crc", "allow_from_disk+check_crc"}
_ANY = {"any", "allow_from_disk"}


@dataclass
class Rule:
    raw_spec: str       # as written, e.g. "models\\..."
    spec: str           # normalized: lowercase, forward slashes
    mode: str           # normalized: "TRUSTED" | "ANY" | "UNKNOWN:<raw>"
    line_no: int


def _norm(p: str) -> str:
    return p.strip().replace("\\", "/").lower()


def _classify_mode(raw: str) -> str:
    low = raw.strip().lower()
    if low in _TRUSTED:
        return "TRUSTED"
    if low in _ANY:
        return "ANY"
    return f"UNKNOWN:{raw.strip()}"


def parse_whitelist(text: str) -> list[Rule]:
    """Parse `<pathspec> <mode>` rules, ignoring comments and braces."""
    rules: list[Rule] = []
    for i, line in enumerate(text.splitlines(), start=1):
        # strip // comments
        if "//" in line:
            line = line[: line.index("//")]
        line = line.strip()
        if not line or line in ("{", "}") or line.lower() == "whitelist":
            continue
        parts = line.split()
        if len(parts) < 2:
            continue
        spec_raw, mode_raw = parts[0], parts[-1]
        rules.append(Rule(spec_raw, _norm(spec_raw), _classify_mode(mode_raw), i))
    return rules


def _spec_matches(spec: str, query: str) -> bool:
    """Apply one normalized spec against a normalized query path."""
    if spec.endswith("/..."):
        base = spec[:-3]  # keep trailing slash
        return query.startswith(base)
    if spec.endswith("/*.*") or spec.endswith("/*"):
        base = spec.rsplit("/", 1)[0] + "/"
        if not query.startswith(base):
            return False
        return "/" not in query[len(base):]  # one level only
    return spec == query  # exact file


@dataclass
class Resolution:
    query: str
    winning: Optional[Rule]
    all_matches: list[Rule]

    @property
    def policy(self) -> str:
        return self.winning.mode if self.winning else "NO RULE (defaults to ANY — not pure-restricted)"

    @property
    def custom_can_load(self) -> bool:
        # No matching rule => not restricted by this whitelist => custom may load.
        return self.winning is None or self.winning.mode == "ANY"


def resolve(rules: list[Rule], query_path: str) -> Resolution:
    q = _norm(query_path)
    matches = [r for r in rules if _spec_matches(r.spec, q)]
    winner = matches[-1] if matches else None  # later rule wins
    return Resolution(q, winner, matches)


# ─────────────────────────── CLI ───────────────────────────
_SELFTEST_CASES = [
    # (query, expected spec that should win against pure_server_full.txt)
    ("models/player/scout.mdl", "models/..."),
    ("models/player/items/scout/bonk_helmet.mdl", "models/..."),
    ("materials/models/player/scout/scout_red.vmt", "materials/..."),
    ("materials/vgui/replay/thumbnails/usernamecipher/lucy/bodyred.vmt", "materials/..."),
    ("particles/explosion.pcf", "particles/..."),
]


def main(argv: Optional[list[str]] = None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass

    args = argv if argv is not None else sys.argv[1:]
    if len(args) < 2:
        print(__doc__)
        return 2

    wl_path = Path(args[0])
    if not wl_path.is_file():
        print(f"error: whitelist not found: {wl_path}", file=sys.stderr)
        return 2
    rules = parse_whitelist(wl_path.read_text(encoding="utf-8", errors="replace"))

    print("=" * 74)
    print(f" whitelist: {wl_path}")
    print(f" parsed {len(rules)} rule(s):")
    for r in rules:
        print(f"     {r.raw_spec:38} -> {r.mode}")
    print("=" * 74)

    if args[1] == "--selftest":
        ok = True
        for q, expect in _SELFTEST_CASES:
            res = resolve(rules, q)
            won = res.winning.spec if res.winning else None
            good = (won == expect)
            ok &= good
            print(f"  [{'PASS' if good else 'FAIL'}] {q}")
            print(f"         winning rule: {won}  policy={res.policy}")
        return 0 if ok else 1

    for q in args[1:]:
        res = resolve(rules, q)
        print(f"\n  query: {q}")
        if res.all_matches:
            for m in res.all_matches:
                tag = "  <- WINS (latest)" if m is res.winning else ""
                print(f"     matched: {m.raw_spec:36} {m.mode}  (line {m.line_no}){tag}")
        else:
            print("     matched: <no rule>")
        verdict = ("EXCLUDED — custom on-disk copy is ignored; the signed/official "
                   "VPK file is loaded") if not res.custom_can_load else (
                   "ALLOWED — a custom on-disk copy may load")
        print(f"     POLICY:  {res.policy}")
        print(f"     RESULT:  {verdict}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
