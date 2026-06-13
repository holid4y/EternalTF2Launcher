import logging
from pathlib import Path
import struct
from typing import Set

from valve_parsers import VPKFile

from core.constants import QUICKPRECACHE_FILE_SUFFIXES, QUICKPRECACHE_MODEL_LIST

log = logging.getLogger()


def make_precache_list(game_path: str) -> Set[str]:
    # get list of files to precache from custom
    model_list = set()
    custom_folder = Path(game_path) / "tf" / "custom"

    if custom_folder.is_dir():
        for file in custom_folder.iterdir():
            if file.is_dir() and "disabled" not in file.name:
                model_list.update(manage_folder(file))
            elif file.is_file() and file.name.endswith(".vpk"):
                model_list.update(manage_vpk(file))

    # filter out cosmetics and other non-gameplay models
    exclusions = ["decompiled ", "competitive_badge", "gameplay_cosmetic", "player/items/", "workshop/player/items/"]
    return {model for model in model_list if not any(exclusion in model for exclusion in exclusions)}


def _should_quickprecache(file_path: str) -> bool:
    file_path_lower = file_path.lower()
    return any(keyword in file_path_lower for keyword in QUICKPRECACHE_MODEL_LIST)


def _process_file_to_model_path(file_path: str) -> str:
    for suffix in QUICKPRECACHE_FILE_SUFFIXES:
        if file_path.endswith(suffix):
            return file_path[:-(len(suffix))] + ".mdl"
    return file_path


# studiohdr_t field offsets (Source MDL "IDST" header), all little-endian int32.
# Used to reject structurally broken community models before they enter the
# $includemodel precache chain — bad counts crash the engine at startup
# (e.g. "GroupStudioHdr(): invalid index", the femspy/femmepyro crash).
_MDL_OFF_NUMBONES = 156
_MDL_OFF_NUMTEXTURES = 204
_MDL_HEADER_MIN = _MDL_OFF_NUMTEXTURES + 4  # need 208 bytes to read both counts

# Garbage headers read as huge or negative; real TF2 models stay well under this.
# Generous on purpose so heavily-rigged workshop models are never false-skipped.
_MDL_SANE_MAX = 4096


def _studiohdr_is_sane(data: bytes, source: str) -> bool:
    """Heuristic validity check on a studiohdr_t header given its leading bytes.
    Returns False only when the header is provably broken; when the buffer is
    too short to inspect we accept (can't prove it bad)."""
    if len(data) < 4:
        return True               # can't inspect — give it the benefit of the doubt
    if data[:4] != b'IDST':
        return False              # not a real MDL header — don't precache it
    if len(data) < _MDL_HEADER_MIN:
        return True               # magic OK but too short to read the counts

    numbones = struct.unpack_from('<i', data, _MDL_OFF_NUMBONES)[0]
    numtextures = struct.unpack_from('<i', data, _MDL_OFF_NUMTEXTURES)[0]

    if not (0 <= numbones <= _MDL_SANE_MAX) or not (0 <= numtextures <= _MDL_SANE_MAX):
        log.warning(
            f"Skipping structurally broken MDL (numbones={numbones}, "
            f"numtextures={numtextures}): {source}")
        return False
    return True


def validate_mdl(filepath: Path) -> bool:
    """Validate an on-disk .mdl by reading just its header."""
    try:
        if not filepath.exists() or filepath.stat().st_size < _MDL_HEADER_MIN:
            return False
        with open(filepath, 'rb') as f:
            return _studiohdr_is_sane(f.read(_MDL_HEADER_MIN), str(filepath))
    except Exception as e:
        log.warning(f"Failed to validate MDL {filepath}: {e}")
        return False


def manage_folder(folder_path: Path) -> Set[str]:
    model_set = set()
    validated_models: dict[str, bool] = {}

    for file_path in folder_path.glob("**/*"):
        if not file_path.is_file():
            continue

        relative_path = str(file_path.relative_to(folder_path))

        if not _should_quickprecache(relative_path):
            continue

        if any(relative_path.endswith(suffix) for suffix in QUICKPRECACHE_FILE_SUFFIXES):
            model_path = Path(_process_file_to_model_path(relative_path)).as_posix().lower()

            # Validate once per model (the .mdl), cached across its sibling
            # .vvd/.vtx files which all resolve to the same model_path.
            if model_path not in validated_models:
                mdl_file_path = folder_path / model_path
                # If the .mdl isn't shipped here it may be vanilla — don't skip.
                validated_models[model_path] = (
                    not mdl_file_path.exists() or validate_mdl(mdl_file_path))
            if not validated_models[model_path]:
                continue

            # The QC generator strips a leading models/ itself, but keep folder
            # and VPK output in the same shape.
            if model_path.startswith("models/"):
                model_path = model_path[7:]
            model_set.add(model_path)

    return model_set


def manage_vpk(vpk_path: Path) -> Set[str]:
    # extract model paths from a VPK file
    model_set = set()
    validated_models: dict[str, bool] = {}

    try:
        vpk_file = VPKFile(vpk_path)

        # find all files in the models directory in the vpk
        model_files = vpk_file.find_files("models/")

        for file_path in model_files:
            if not _should_quickprecache(file_path):
                continue
            if not any(file_path.endswith(suffix) for suffix in QUICKPRECACHE_FILE_SUFFIXES):
                continue
            if not file_path.startswith("models/"):
                continue

            relative_path = file_path[7:]
            model_path = _process_file_to_model_path(relative_path).lower()

            if model_path not in validated_models:
                validated_models[model_path] = _validate_vpk_mdl(
                    vpk_file, "models/" + model_path, vpk_path)
            if not validated_models[model_path]:
                continue

            model_set.add(model_path)

    except Exception:
        log.exception(f"Failed to process VPK {vpk_path}")

    return model_set


def _validate_vpk_mdl(vpk_file, mdl_vpk_path: str, vpk_path: Path) -> bool:
    """Validate an .mdl stored inside a VPK; absent entries are treated as
    vanilla (precache allowed)."""
    try:
        if not vpk_file.file_exists(mdl_vpk_path):
            return True
        return _studiohdr_is_sane(vpk_file.get_file_data(mdl_vpk_path), f"{mdl_vpk_path} in {vpk_path}")
    except Exception as e:
        log.warning(f"Failed to validate VPK MDL {mdl_vpk_path} in {vpk_path}: {e}")
        # Don't drop a model from precache just because validation hit an error —
        # only provably broken headers are excluded.
        return True
