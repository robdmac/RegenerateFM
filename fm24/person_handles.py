"""Find a person from FM24's object reference to them, without screen IDs.

FM24 refers to an object by a 32-bit reference, type << 24 | index; a person
is 0x01000000 | their handle, the u32 just before their record (person + 8;
see native_squad). The open screen's object browser holds the reference of
what it shows (layout "browser_reference"), so the library can report the
person on a profile when the header shows no "(ID:n)". Where the layout
maps FM24's person table (a vector of person pointers indexed by handle,
"person_table"), a lookup is two reads. Otherwise, or if the table read
fails, the companion maps handles to people with one pass over the game's
memory, and checks each mapping again before it is used.
"""

from __future__ import annotations

import struct
import threading
import time

from layout import LAYOUT
from live_memory import MemoryUnavailable, record_marker
from process_memory import MemoryScan, fm24_image_base, read_process_bytes

PERSON_HANDLE = 8

PERSON_TYPE = 1
_INDEX_MASK = 0x7FFFFF
# Bytes 14-15 of a full person record (live_memory.record_marker): found with
# bytes.find, which is far faster than a regular expression over the heap.
_MARKERS = tuple(bytes([kind, last]) for kind in (0x50, 0x58) for last in (0x10, 0x12, 0x18))
_REBUILD_AFTER = 10.0
DATABASE_SCAN = 0x2000
_table_slots: dict[int, int] = {}           # pid -> database slot of the person vector

_lock = threading.Lock()
_index: dict[int, dict[int, int]] = {}      # pid -> handle -> record address
_built: dict[int, float] = {}               # pid -> time of the last pass
_building: set[int] = set()
_built_event = threading.Condition(_lock)


def person_handle(reference: int) -> int:
    """The handle in a person reference; ValueError for any other kind of object."""
    if reference >> 24 != PERSON_TYPE or (reference & _INDEX_MASK) in (0, _INDEX_MASK):
        raise ValueError(f"FM24 reference {reference:#010x} is not a person.")
    return reference & _INDEX_MASK


def _scan(pid: int) -> dict[int, int]:
    found: dict[int, int] = {}
    for base, data in MemoryScan(pid, 24):
        for marker in _MARKERS:
            offset = data.find(marker, 18)
            while offset >= 0:
                record = offset - 14
                if ((base + record) % 4 == 0 and data[record:record + 4] == data[record + 4:record + 8] and
                        data[record:record + 4] != b"\0\0\0\0" and record_marker(data[record:record + 16])):
                    handle = struct.unpack_from("<I", data, record - 4)[0]
                    if 0 < handle < _INDEX_MASK:
                        found.setdefault(handle, base + record)
                offset = data.find(marker, offset + 1)
    return found


def _check(pid: int, handle: int, record: int) -> str | None:
    """The person ID if the record at this address still carries this handle."""
    try:
        raw = read_process_bytes(pid, record - 4, 20)
    except OSError:
        return None
    number = struct.unpack_from("<I", raw, 4)[0]
    if (struct.unpack_from("<I", raw)[0] != handle or raw[4:8] != raw[8:12] or
            not 1 <= number <= 2_147_483_647 or not record_marker(raw[4:])):
        return None
    return str(number)


def _slot_person(pid: int, database: int, slot: int, handle: int) -> str | None:
    """The person ID at this handle in the database's vector at ``slot``, or None."""
    try:
        begin, end = struct.unpack("<QQ", read_process_bytes(pid, database + slot, 16))
        if not begin or end <= begin or (end - begin) % 8 or handle >= (end - begin) // 8:
            return None
        person = struct.unpack("<Q", read_process_bytes(pid, begin + 8 * handle, 8))[0]
        return _check(pid, handle, person + PERSON_HANDLE + 4) if person else None
    except (OSError, ValueError, struct.error):
        return None


def _from_table(pid: int, handle: int) -> str | None:
    """The person ID at this handle in FM24's person table, or None.

    The database object keeps one vector per kind of object, each indexed by
    handle ({begin, end, capacity, allocator}), and which slot holds people
    changes from one run of FM24 to the next. The layout's slot is tried
    first, then every slot in the first DATABASE_SCAN bytes; the people's is
    the one whose entry is a person record carrying this handle.
    """
    if LAYOUT["person_table"] is None:
        return None
    pointer, hint = LAYOUT["person_table"]
    try:
        database = struct.unpack("<Q", read_process_bytes(pid, fm24_image_base(pid) + pointer, 8))[0]
    except (OSError, ValueError, struct.error):
        return None
    first = _table_slots.get(pid, hint)
    if person := _slot_person(pid, database, first, handle):
        return person
    found = {slot: person for slot in range(0, DATABASE_SCAN, 8)
             if slot != first and (person := _slot_person(pid, database, slot, handle))}
    if len(set(found.values())) != 1:
        return None
    _table_slots[pid] = min(found)
    return next(iter(found.values()))


def build(pid: int) -> None:
    """Map every person record's handle (one memory pass, about 10 s). A
    pass already running for this game is waited for, not repeated."""
    with _lock:
        if pid in _building:
            while pid in _building:
                _built_event.wait()
            return
        _building.add(pid)
    try:
        found = _scan(pid)
        with _lock:
            _index[pid] = found
            _built[pid] = time.monotonic()
    finally:
        with _lock:
            _building.discard(pid)
            _built_event.notify_all()


def build_in_background(pid: int) -> None:
    with _lock:
        if pid in _building:
            return

    def run():
        try:
            build(pid)
        except (MemoryUnavailable, OSError, ValueError):
            pass
    threading.Thread(target=run, name="fm24-person-handles", daemon=True).start()


def person_for_reference(pid: int, reference: int, *, wait: bool = True) -> str:
    """The person ID for a person reference. Without ``wait``, raises
    RuntimeError while the index is still being built."""
    if LAYOUT["browser_reference"] is None:
        raise MemoryUnavailable("This FM24 build's object browser isn't mapped; enable screen IDs.")
    handle = person_handle(reference)
    if person := _from_table(pid, handle):
        return person
    with _lock:
        record = _index.get(pid, {}).get(handle)
        last = _built.get(pid)
    if record is not None and (person := _check(pid, handle, record)):
        return person
    # A person the index doesn't know yet (a newgen, say), or a moved record.
    if last is None or time.monotonic() - last > _REBUILD_AFTER:
        if not wait:
            build_in_background(pid)
            raise RuntimeError("FM Face is still indexing FM24's people.")
        build(pid)
        with _lock:
            record = _index.get(pid, {}).get(handle)
        if record is not None and (person := _check(pid, handle, record)):
            return person
    raise MemoryUnavailable(f"No FM24 person has handle {handle:#x}.")


def screen_reference(pid: int, manager: int, widgets: dict[int, list[int]]) -> int:
    """The open screen's object reference: the sidebar menu's, which FM24 keeps
    after a skin reload, else the object browser's (exactly one)."""
    image = fm24_image_base(pid)
    if LAYOUT["sidebar_vtable"] is not None:
        sidebars = [widget for widget in widgets.get(int.from_bytes(b"side", "big"), [])
                    if struct.unpack("<Q", read_process_bytes(pid, widget, 8))[0] == image + LAYOUT["sidebar_vtable"]]
        if len(sidebars) == 1:
            reference = struct.unpack("<I", read_process_bytes(pid, sidebars[0] + LAYOUT["sidebar_reference"], 4))[0]
            try:
                person_handle(reference)
                return reference
            except ValueError:
                pass
    vtable = image + LAYOUT["object_browser_vtable"]
    browsers = [widget for found in widgets.values() for widget in found
                if struct.unpack("<Q", read_process_bytes(pid, widget, 8))[0] == vtable]
    if len(browsers) != 1:
        raise MemoryUnavailable(f"Expected one FM24 object browser; found {len(browsers)}.")
    return struct.unpack("<I", read_process_bytes(pid, browsers[0] + LAYOUT["browser_reference"], 4))[0]
