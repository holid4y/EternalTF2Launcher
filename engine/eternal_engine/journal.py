"""Records game-file mutations so an interrupted one can be detected and undone.

Upstream patches tf2_misc_dir.vpk and gameinfo.txt in place. If the process dies
halfway, the next run sees a "running" entry whose owner is gone and restores
the vanilla state with upstream's uninstall before building again.
"""

import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path


def _now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


class Journal:
    def __init__(self, path: Path):
        self.path = path

    def read(self) -> dict | None:
        try:
            return json.loads(self.path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return None
        except (OSError, ValueError):
            return {"operation": "unknown", "state": "running", "pid": 0, "corrupt": True}

    def _write(self, entry: dict) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as fd:
            json.dump(entry, fd, indent=2)
            fd.flush()
            os.fsync(fd.fileno())
        os.replace(tmp, self.path)

    def begin(self, operation: str, tf_path: Path, **details) -> None:
        self._write({
            "operation": operation,
            "state": "running",
            "tf_path": str(tf_path),
            "pid": os.getpid(),
            "started": _now(),
            **details,
        })

    def finish(self, state: str, **details) -> None:
        entry = self.read() or {}
        entry.update(state=state, finished=_now(), **details)
        entry.pop("pid", None)
        self._write(entry)

    def interrupted(self) -> dict | None:
        entry = self.read()
        if entry and entry.get("state") == "running" and not _pid_alive(int(entry.get("pid") or 0)):
            return entry
        return None
