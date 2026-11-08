"""Many hit or kill sounds at once.

TF2 picks one sound from a soundscript entry that uses rndwave. Upstream can't
carry that on its own: it deletes any sound the game doesn't ship, and it skips
soundscripts that come with a mod. So the launcher's numbered files are held
back while upstream sorts out the other sounds, then the two entries TF2 plays
for hits and kills are rewritten to list them.
"""

import logging
import re
import shutil
import tempfile
from pathlib import Path

log = logging.getLogger(__name__)

FOLDER = Path("sound/ui/eternal")
SCRIPT = "game_sounds_weapons.txt"
ENTRIES = {"hit": "Player.HitSoundDefaultDing", "kill": "Player.KillSoundDefaultDing"}
_WAVE = re.compile(r"(hit|kill)_\d{4}\.wav")
_installed = False


def set_list(text: str, entry: str, waves: list[str]) -> str:
    """Replaces one soundscript entry with a random list of `waves`."""
    start = text.find(f'"{entry}"')
    opening = text.find("{", start)
    if start < 0 or opening < 0:
        raise ValueError(f"{entry} is not in the soundscript")
    depth = 0
    for position in range(opening, len(text)):
        depth += {"{": 1, "}": -1}.get(text[position], 0)
        if depth == 0:
            lines = "\n".join(f'\t\t"wave"\t")ui/eternal/{wave}"' for wave in waves)
            block = (f'"{entry}"\n{{\n\t"channel"\t\t"CHAN_STATIC"\n\t"volume"\t\t"1"\n\t"soundlevel"\t"SNDLVL_NONE"\n'
                     f'\t"pitch"\t\t\t"PITCH_NORM"\n\n\t"rndwave"\n\t{{\n{lines}\n\t}}\n}}')
            return text[:start] + block + text[position + 1:]
    raise ValueError(f"{entry} never closes in the soundscript")


def _waves(folder: Path) -> dict[str, list[str]]:
    found: dict[str, list[str]] = {}
    for wave in sorted(p.name for p in folder.iterdir() if p.is_file() and _WAVE.fullmatch(p.name)):
        found.setdefault(wave.split("_", 1)[0], []).append(wave)
    return found


def _write_script(mods: Path, backup_scripts: Path, waves: dict[str, list[str]]) -> None:
    scripts = mods / "scripts"
    target = scripts / SCRIPT
    if not target.exists():
        scripts.mkdir(parents=True, exist_ok=True)
        shutil.copy2(backup_scripts / SCRIPT, target)
    text = target.read_text(encoding="utf-8", errors="ignore")
    for kind, names in waves.items():
        text = set_list(text, ENTRIES[kind], names)
    target.write_text(text, encoding="utf-8")
    log.info("listed %s in %s", {kind: len(names) for kind, names in waves.items()}, SCRIPT)


def wrap(original):
    def process(self, temp_mods_dir: Path, backup_scripts_dir: Path, vpk_paths: list[Path]):
        folder = temp_mods_dir / FOLDER
        if not folder.is_dir():
            return original(self, temp_mods_dir, backup_scripts_dir, vpk_paths)

        waves = _waves(folder)
        with tempfile.TemporaryDirectory(dir=temp_mods_dir.parent) as tmp:
            held = Path(tmp) / "held"
            shutil.move(str(folder), str(held))
            try:
                result = original(self, temp_mods_dir, backup_scripts_dir, vpk_paths)
            finally:
                folder.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(held), str(folder))
        _write_script(temp_mods_dir, backup_scripts_dir, waves)
        return result

    return process


def install() -> None:
    global _installed
    if _installed:
        return
    from core.handlers.sound_handler import SoundHandler

    original = getattr(SoundHandler, "process_temp_sound_mods", None)
    if not callable(original):
        raise RuntimeError("casual-pre-loader no longer has SoundHandler.process_temp_sound_mods; update eternal_engine/sounds.py")
    SoundHandler.process_temp_sound_mods = wrap(original)
    _installed = True
