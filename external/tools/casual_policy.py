#!/usr/bin/env python3
"""
casual_policy — empirical Casual-compatibility model for TF2 asset paths.

This is NOT the authoritative server whitelist. Valve Casual loads
`pure_server_full.txt` plus a *server-side* custom whitelist ("Custom pure
server rules") that is sent to the client and is not present on disk, so it
cannot be read directly. This module encodes the **effective** policy inferred
from two things we CAN see:

  1. the shipped `pure_server_full.txt` / `pure_server_minimal.txt` (measured),
  2. behaviour observed in a real Casual match (measured), filled out with
     community knowledge where no direct observation exists (inferred).

Every rule therefore carries an explicit confidence and an evidence basis, so a
consumer (e.g. a "Casual Compatibility" badge in the launcher) can show *why* a
verdict was reached and never present an inference as a fact.

Verdicts:
  LIKELY_WORKS   (+)  custom file is expected to load in Valve Casual
  UNCERTAIN      (?)  no direct evidence either way
  LIKELY_BLOCKED (-)  custom file is expected to be ignored (vanilla loads)

Usage:
  python casual_policy.py <asset-path> [<asset-path> ...]
  python casual_policy.py --matrix         # print the category matrix
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from enum import Enum
from typing import Optional


class Verdict(str, Enum):
    WORKS = "LIKELY_WORKS"
    UNCERTAIN = "UNCERTAIN"
    BLOCKED = "LIKELY_BLOCKED"

    @property
    def mark(self) -> str:
        return {"LIKELY_WORKS": "+", "UNCERTAIN": "?", "LIKELY_BLOCKED": "-"}[self.value]


class Confidence(str, Enum):
    HIGH = "HIGH"
    MEDIUM = "MEDIUM"
    LOW = "LOW"


@dataclass(frozen=True)
class Rule:
    pattern: str            # "dir/...", exact "dir/file", or "player_base" sentinel
    verdict: Verdict
    confidence: Confidence
    basis: str              # MEASURED / INFERRED + one-line justification


# The 9 base player body models — the specific files observed to stay vanilla.
_PLAYER_CLASSES = ("scout", "soldier", "pyro", "demo", "heavy",
                   "engineer", "medic", "sniper", "spy")
_PLAYER_BASE_BODIES = {f"models/player/{c}.mdl" for c in _PLAYER_CLASSES}

# The gameplay-critical particles locked by pure_server_minimal.txt — these are
# the ones Valve enforces even at the most permissive pure level.
_MINIMAL_LOCKED_PARTICLES = {
    "particles/bullet_tracers.pcf", "particles/burningplayer.pcf",
    "particles/burningplayer_dx80.pcf", "particles/cig_smoke.pcf",
    "particles/cig_smoke_dx80.pcf", "particles/explosion.pcf",
    "particles/explosion_dx80.pcf", "particles/explosion_dx90_slow.pcf",
    "particles/explosion_high.pcf", "particles/disguise.pcf",
    "particles/flag_particles.pcf", "particles/nemesis.pcf",
    "particles/player_recent_teleport.pcf",
    "particles/player_recent_teleport_dx80.pcf", "particles/speechbubbles.pcf",
    "particles/teleported_fx.pcf", "particles/teleport_status.pcf",
    "particles/water.pcf", "particles/water_dx80.pcf",
}

# Ordered general -> specific. Last matching rule wins (mirrors whitelist grammar).
_RULES: tuple[Rule, ...] = (
    # ---- broad defaults (weak) ----
    Rule("models/...", Verdict.UNCERTAIN, Confidence.LOW,
         "INFERRED: models/... is TRUSTED in full.txt; Casual's custom rules open "
         "some of it but coverage per-subtree is unverified"),
    Rule("materials/...", Verdict.WORKS, Confidence.LOW,
         "INFERRED: custom materials broadly load in Casual (skins, map textures), "
         "though full.txt's baseline is TRUSTED"),
    Rule("particles/...", Verdict.WORKS, Confidence.MEDIUM,
         "INFERRED: non-critical custom particles load; the preloader also patches "
         "them into the game VPK rather than relying on whitelist ANY"),
    Rule("sound/...", Verdict.BLOCKED, Confidence.MEDIUM,
         "INFERRED: full.txt = TRUSTED; custom sounds are a known Casual gap and "
         "the preloader lists sounds as unsupported"),
    Rule("scripts/...", Verdict.WORKS, Confidence.HIGH,
         "MEASURED: full.txt lists no rule for general scripts -> ANY (custom HUD "
         "scripts load); validated with whitelist_policy.py"),

    # ---- weapons (community knowledge, not observed this session) ----
    Rule("models/weapons/...", Verdict.WORKS, Confidence.LOW,
         "INFERRED: custom weapon/viewmodel mods are widely used in Casual; not "
         "directly observed in our capture"),
    Rule("materials/models/weapons/...", Verdict.WORKS, Confidence.MEDIUM,
         "INFERRED: weapon skins / war paints render in Casual"),

    # ---- player materials ----
    Rule("materials/models/player/...", Verdict.WORKS, Confidence.MEDIUM,
         "DOCUMENTED+INFERRED: example whitelist's canonical carve-out is "
         "materials\\models\\player\\... any; player skins render in Casual"),
    Rule("materials/vgui/replay/...", Verdict.WORKS, Confidence.HIGH,
         "MEASURED: the relocated Lucy body VMTs under vgui/replay/thumbnails were "
         "parsed in-match (console.log) — this is the preloader's relocation target"),

    # ---- player models ----
    Rule("models/player/...", Verdict.UNCERTAIN, Confidence.LOW,
         "INFERRED: mixed; depends on the specific subtree (items vs base body)"),
    Rule("models/player/items/...", Verdict.WORKS, Confidence.MEDIUM,
         "MEASURED: custom cosmetic models rendered in-match (the SetupBones errors "
         "are custom cosmetics bonemerged onto the vanilla skeleton)"),

    # ---- the specific, strongest findings (most specific -> evaluated last) ----
    Rule("__player_base__", Verdict.BLOCKED, Confidence.HIGH,
         "MEASURED: models/player/<class>.mdl stayed vanilla in a real Casual match "
         "even though a valid custom copy was baked into tf/custom"),
    Rule("__minimal_particle__", Verdict.BLOCKED, Confidence.HIGH,
         "MEASURED: enumerated in pure_server_minimal.txt (TRUSTED at every pure "
         "level) — gameplay-critical particles Valve always enforces"),
)


def _matches(rule: Rule, path: str) -> bool:
    if rule.pattern == "__player_base__":
        return path in _PLAYER_BASE_BODIES
    if rule.pattern == "__minimal_particle__":
        return path in _MINIMAL_LOCKED_PARTICLES
    if rule.pattern.endswith("/..."):
        return path.startswith(rule.pattern[:-3])
    return path == rule.pattern


@dataclass
class Classification:
    path: str
    rule: Optional[Rule]

    @property
    def verdict(self) -> Verdict:
        return self.rule.verdict if self.rule else Verdict.UNCERTAIN

    @property
    def confidence(self) -> Confidence:
        return self.rule.confidence if self.rule else Confidence.LOW

    @property
    def basis(self) -> str:
        return self.rule.basis if self.rule else "no rule matched (unknown asset class)"


def classify(path: str) -> Classification:
    norm = path.strip().replace("\\", "/").lower()
    winner: Optional[Rule] = None
    for rule in _RULES:          # later match wins
        if _matches(rule, norm):
            winner = rule
    return Classification(norm, winner)


# ─────────────────────────── matrix / CLI ───────────────────────────
_MATRIX_CATEGORIES = [
    ("models/player/items/*", "models/player/items/scout/bonk.mdl"),
    ("models/player/<class>.mdl", "models/player/scout.mdl"),
    ("materials/models/player/items/*", "materials/models/player/items/scout/bonk.vmt"),
    ("materials/models/player/*", "materials/models/player/scout/body.vmt"),
    ("materials/vgui/replay/*", "materials/vgui/replay/thumbnails/x/y.vmt"),
    ("models/weapons/*", "models/weapons/c_models/c_scattergun/c_scattergun.mdl"),
    ("materials/models/weapons/*", "materials/models/weapons/c_items/scattergun.vmt"),
    ("particles/* (critical)", "particles/explosion.pcf"),
    ("particles/* (custom fx)", "particles/my_unusual_effect.pcf"),
    ("sound/*", "sound/ui/hitsound.wav"),
    ("scripts/* (HUD)", "scripts/hudlayout.res"),
]


def _print_matrix() -> None:
    print("=" * 92)
    print(" Casual Compatibility Matrix  (effective policy, NOT the server's authoritative whitelist)")
    print("=" * 92)
    print(f" {'category':34} {'verdict':16} {'conf':7} basis")
    print(" " + "-" * 90)
    for label, sample in _MATRIX_CATEGORIES:
        c = classify(sample)
        print(f" [{c.verdict.mark}] {label:30} {c.verdict.value:16} {c.confidence.value:7} {c.basis}")
    print("=" * 92)
    print(" + LIKELY_WORKS   ? UNCERTAIN   - LIKELY_BLOCKED")
    print(" MEASURED = from a shipped file or the real-match capture; INFERRED = deduction/community knowledge")


def main(argv: Optional[list[str]] = None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass
    args = argv if argv is not None else sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    if args[0] == "--matrix":
        _print_matrix()
        return 0
    for p in args:
        c = classify(p)
        print(f"[{c.verdict.mark}] {p}")
        print(f"    verdict:    {c.verdict.value}")
        print(f"    confidence: {c.confidence.value}")
        print(f"    basis:      {c.basis}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
