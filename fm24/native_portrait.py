"""Read FM24's in-memory portrait resource index without touching game state.

The resource index is distinct from UI lookup requests: FM24 can request
``pictures/person/<id>/portrait`` while displaying the no-picture silhouette.
The indexed ``graphics/pictures/person/<id>/portrait`` key matched visible
built-in and user graphics in the tested FM24 24.4.2 build. Absence still
requires a rendered-portrait check before making a billed image request.
"""

from __future__ import annotations

import re
import struct

from live_memory import MemoryUnavailable, find_game_pid
from process_memory import MemoryScan

_SENTINEL = b"graphics/pictures/person/none/male/player/portrait"


def _indexed(data: bytes, key: bytes) -> bool:
    """Accept a complete FM string object, never a bare UI lookup string."""
    offset = 0
    while (offset := data.find(key, offset)) >= 0:
        if (offset >= 8 and offset + len(key) < len(data) and
                data[offset - 8:offset - 4] == b"\x01\x00\x00\x00" and
                struct.unpack_from("<I", data, offset - 4)[0] == len(key) and
                data[offset + len(key)] == 0):
            return True
        offset += len(key)
    return False


def read_portrait_state(person_id: str, pid: int | None = None) -> str:
    """Return ``present``, ``missing``, or ``unknown`` from FM's resource index.

    An absent key is useful only after a complete scan that also sees the
    standard no-picture asset. Callers should verify a ``missing`` result
    against the rendered portrait before making a billed image request.
    """
    if not re.fullmatch(r"[1-9][0-9]{0,9}", person_id):
        raise ValueError("Invalid FM24 person ID.")
    pid = pid or find_game_pid()
    keys = (f"graphics/pictures/person/{person_id}/portrait".encode(),
            f"graphics/pictures/person/r-{person_id}/portrait".encode())
    mapped = False
    sentinel = False
    scan = MemoryScan(pid, 128)
    try:
        for _, data in scan:
            mapped |= any(_indexed(data, key) for key in keys)
            sentinel |= _indexed(data, _SENTINEL)
    except OSError as exc:
        raise MemoryUnavailable(f"Could not read the FM24 graphics index: {exc}") from exc
    if not scan.scanned or not sentinel:
        return "unknown"
    if mapped:
        return "present"
    return "missing" if scan.complete else "unknown"
