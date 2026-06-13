import sys
from pathlib import Path
from typing import Any, Optional


def auto_detect_sourcemod(game_target: str = "Team Fortress 2") -> str | None:
    """Auto-detect a Source mod installation by looking for a subdirectory with gameinfo.txt.

    Args:
        game_target: The Steam game folder name. Defaults to "Team Fortress 2".

    Returns:
        The path to the mod directory, or None if not found.
    """
    if sys.platform == 'win32':
        steam_paths = [
            Path("C:/Program Files (x86)/Steam/steamapps/common"),
            Path("D:/Program Files (x86)/Steam/steamapps/common"),
        ]
    else:
        steam_paths = [
            Path("~/.steam/steam/steamapps/common").expanduser(),
            Path("~/.local/share/Steam/steamapps/common").expanduser(),
        ]

    game_target_lower = game_target.lower()

    for steam_path in steam_paths:
        if not steam_path.exists():
            continue

        for game_folder in steam_path.iterdir():
            if not game_folder.is_dir() or game_folder.name.lower() != game_target_lower:
                continue

            for subdir in game_folder.iterdir():
                if subdir.is_dir() and (subdir / "gameinfo.txt").exists():
                    return str(subdir)

    return None


def validate_game_directory(directory: str | None, validation_label: Optional[Any] = None) -> bool:
    """Validate a Source mod directory by checking for gameinfo.txt."""
    if not directory:
        if validation_label:
            validation_label.setText("")
        return False

    path = Path(directory)

    if not path.exists():
        if validation_label:
            validation_label.setText("Directory does not exist!")
            validation_label.setStyleSheet("color: red;")
        return False

    if not (path / "gameinfo.txt").exists():
        if validation_label:
            validation_label.setText("gameinfo.txt not found - this doesn't appear to be a valid Source mod directory")
            validation_label.setStyleSheet("color: red;")
        return False

    if validation_label:
        validation_label.setText("Valid Source mod directory detected!")
        validation_label.setStyleSheet("color: green;")

    return True
