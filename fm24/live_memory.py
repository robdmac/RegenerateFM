"""Read and validate FM24 person identity from the running Proton process.

This is an independently derived, read-only parser for the FM24 build tested
on Linux/Proton. Unknown records fail closed. The native_profile module also
reads validated roles, clubs, attributes, and available biography text.
"""

from __future__ import annotations

import datetime as dt
import re
import struct
from dataclasses import dataclass

from process_memory import MemoryScan, fm24_image_base, read_process_bytes, running_executables


_RECORD_SIZE = 64
# Known record markers; byte 1 carries flags (0x40 also seen).
_MARKERS = (b"\x00\x02\x58\x10", b"\x00\x02\x58\x12", b"\x00\x02\x58\x18")


class MemoryUnavailable(Exception):
    """The running FM24 process or a supported person record was unavailable."""


@dataclass(frozen=True)
class PersonIdentity:
    person_id: int
    name: str
    birth_date: dt.date
    game_year: int
    kind: str

    @property
    def possible_ages(self) -> tuple[int, int]:
        difference = self.game_year - self.birth_date.year
        return difference - 1, difference


def _record_addresses(pid: int, person_id: int):
    needle = struct.pack("<II", person_id, person_id)
    for base, data in MemoryScan(pid, len(needle) - 1):
        offset = 0
        while (offset := data.find(needle, offset)) >= 0:
            if (base + offset) % 4 == 0:
                yield base + offset
            offset += 1


# Record type at +16: 4 and 5 are players with full detail; 0 and 1 are seen
# both for staff and for reserve or youth players. Players' records hold
# position ratings (1-20, 20 at their natural position) at -0x7c, staff
# records zeros, so the ratings say which a type 0 or 1 record is.
RECORD_TYPES = (0, 1, 4, 5)
POSITION_RATINGS = -0x7c
POSITION_COUNT = 15


def person_kind(pid: int, address: int, record_type: int) -> str | None:
    """"player", "staff", or None when the record cannot be classified."""
    if record_type in (4, 5):
        return "player"
    if record_type not in RECORD_TYPES:
        return None
    ratings = read_process_bytes(pid, address + POSITION_RATINGS, POSITION_COUNT)
    if not any(ratings):
        return "staff" if record_type == 0 else None
    if all(1 <= value <= 20 for value in ratings) and max(ratings) == 20:
        return "player"
    return None


def _parse_record(pid: int, address: int, person_id: int) -> PersonIdentity | None:
    try:
        raw = read_process_bytes(pid, address, _RECORD_SIZE)
        if raw[:8] != struct.pack("<II", person_id, person_id):
            return None
        # Marker byte 13 carries flags (0x02 and 0x40 seen); type 5 is a player too.
        if raw[12] or raw[14] != 0x58 or raw[15] not in (0x10, 0x12, 0x18):
            return None
        kind = person_kind(pid, address, struct.unpack_from("<I", raw, 16)[0])
        if kind is None:
            return None
        game_year = struct.unpack_from("<H", raw, 0x36)[0]
        birth_day, birth_year = struct.unpack_from("<HH", raw, 0x38)
        birth_date = dt.date(birth_year, 1, 1) + dt.timedelta(days=birth_day - 1)
        if not 1900 <= birth_year <= game_year - 14 or birth_date.year != birth_year:
            return None
        name_ptr = struct.unpack_from("<I", raw, 0x3c)[0]
        name_raw = read_process_bytes(pid, name_ptr, 100)
        length = struct.unpack_from("<I", name_raw)[0]
        if not 3 <= length <= 80 or name_raw[4 + length] != 0:
            return None
        name = name_raw[4:4 + length].decode("utf-8")
        if not re.fullmatch(r"[^\x00-\x1f\x7f]{3,80}", name):
            return None
        return PersonIdentity(person_id, name, birth_date, game_year, kind)
    except (OSError, ValueError, UnicodeDecodeError, struct.error, OverflowError):
        return None


def find_game_pid() -> int:
    candidates = []
    for pid, executable in running_executables():
        if not executable.replace("\\", "/").lower().endswith("/fm.exe"):
            continue
        try:
            fm24_image_base(pid)
        except (OSError, ValueError):
            continue
        candidates.append(pid)
    if len(candidates) != 1:
        raise MemoryUnavailable(f"Expected one running FM24 process; found {len(candidates)}.")
    return candidates[0]


def read_identity(person_id: str, pid: int | None = None) -> PersonIdentity:
    """Find one canonical person record for an ID shown on the FM24 screen."""
    if not re.fullmatch(r"[1-9][0-9]{0,9}", person_id):
        raise ValueError("Invalid FM24 person ID.")
    numeric_id = int(person_id)
    pid = pid or find_game_pid()
    try:
        records = {identity for address in _record_addresses(pid, numeric_id)
                   if (identity := _parse_record(pid, address, numeric_id)) is not None}
    except OSError as exc:
        raise MemoryUnavailable(f"Could not read FM24 memory: {exc}") from exc
    if len(records) != 1:
        raise MemoryUnavailable(f"Expected one validated FM24 person record; found {len(records)}.")
    return records.pop()


def read_person_record(person_id: str, pid: int | None = None) -> tuple[int, PersonIdentity]:
    """Return the unique canonical record address and validated identity.

    A caller decoding build-specific fields must not pick an arbitrary copy of
    a person ID from UI buffers or history tables.
    """
    if not re.fullmatch(r"[1-9][0-9]{0,9}", person_id):
        raise ValueError("Invalid FM24 person ID.")
    numeric_id = int(person_id)
    pid = pid or find_game_pid()
    try:
        records = [(address, identity) for address in _record_addresses(pid, numeric_id)
                   if (identity := _parse_record(pid, address, numeric_id)) is not None]
    except OSError as exc:
        raise MemoryUnavailable(f"Could not read FM24 memory: {exc}") from exc
    if len(records) != 1:
        raise MemoryUnavailable(f"Expected one canonical FM24 person record; found {len(records)}.")
    return records[0]
