"""Reading mod VPKs without trusting the paths or sizes written inside them.

valve_parsers joins entry paths onto the output folder as they are, and its
extract_all skips files it fails to read, so neither is safe on its own for
archives made by someone else.
"""

import zlib
from pathlib import Path, PurePosixPath

MAX_ENTRIES = 200_000
MAX_BYTES = 8 * 1024 ** 3


class ArchiveError(Exception):
    pass


def safe_entry(name: str) -> bool:
    parts = name.replace("\\", "/").split("/")
    return "\0" not in name and all(part not in ("", ".", "..") and ":" not in part for part in parts)


def check_vpk(path: Path):
    """Opens a VPK and returns it with its entry names, or raises if any entry could land outside a folder."""
    from valve_parsers import VPKFile

    archive = VPKFile(path)
    names = archive.list_files()
    if len(names) > MAX_ENTRIES:
        raise ArchiveError(f"{path.name} has too many files ({len(names)})")
    total = 0
    for name in names:
        if not safe_entry(name):
            raise ArchiveError(f"{path.name} contains an unsafe path: {name}")
        info = archive.get_file_info(name)
        total += info["size"] + info["preload_bytes"]
    if total > MAX_BYTES:
        raise ArchiveError(f"{path.name} expands to more than {MAX_BYTES // 1024 ** 3} GiB")
    return archive, names


def extract_vpk(archive, names: list[str], root: Path, label: str) -> None:
    """Writes every entry under root, checking its size and CRC. Raises on the first file that doesn't match."""
    for name in names:
        info = archive.get_file_info(name)
        data = _read(archive, name, info)
        if data is None or len(data) != info["size"] + info["preload_bytes"] or (info["crc"] and zlib.crc32(data) != info["crc"]):
            raise ArchiveError(f"{name} in {label} is damaged")
        target = root.joinpath(*name.replace("\\", "/").split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)


def _read(archive, name: str, info: dict) -> bytes | None:
    if info["size"]:
        return archive.get_file_data(name)
    # get_file_data treats an empty archive part as missing; small files can live entirely in the tree.
    try:
        entry = archive.directory[info["extension"]][info["directory"]][PurePosixPath(name).stem]
    except KeyError:
        return None
    return entry.preload_data or b""
