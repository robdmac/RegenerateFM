"""Read the open FM24 Squad table and portrait index from game memory.

One read-only pass over FM24's writable memory finds:

* the player-name cells of the Squad table that is attached to the live UI
  tree, and the person record each row shows (the same rows FM24 draws,
  including loaned-in and reserve players listed on the screen), and
* FM24's graphics resource index, which lists every indexed
  ``graphics/pictures/person/<id>/portrait`` (built-in and FM Face pictures).

Layouts are those of the pinned FM24 24.4.2 build (see native_menu/README.md).
Anything unexpected raises MemoryUnavailable; callers must not guess.
"""

from __future__ import annotations

import collections
import datetime as dt
import re
import struct
import time
from dataclasses import dataclass

from live_memory import MemoryUnavailable, find_game_pid
from process_memory import MemoryScan, fm24_image_base, read_process_bytes

MANAGER_GLOBAL_RVA = 0x63635B8
NAME_CELL_VTABLE_RVA = 0x56807C8
NAME_CELL_ID = int.from_bytes(b"Pnms", "big")
ROW_PERSON = 0x728
ROW_NAME = 0x1C8            # cell -> {u32 length, UTF-8} display name ("Ian Fray")
TABLE_ROWS = 0x3A0          # table widget: total rows, drawn or not
TABLE_VISIBLE = 0x394       # table widget: first and last visible row, 1-based
WIDGET_MANAGER, WIDGET_PARENT, WIDGET_ID, WIDGET_CHILDREN = 0x48, 0x50, 0x5C, 0x100
_PERSON_PICTURE = b"graphics/pictures/person/"
_PORTRAIT = re.compile(rb"(?:r-)?([1-9][0-9]{0,9})/portrait")
_NO_PICTURE = b"graphics/pictures/person/none/male/player/portrait"
_MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")
_DATE = re.compile(rb"([1-9]|[12][0-9]|3[01]) ([A-Z][a-z]{2}) ((?:19|20)[0-9]{2})")


@dataclass(frozen=True)
class SquadRow:
    person_id: str
    record: int
    name: str


@dataclass(frozen=True)
class SquadState:
    rows: tuple[SquadRow, ...]   # drawn rows only; see total
    total: int                   # rows in the table, drawn or not
    indexed: frozenset[str]
    today: dt.date
    visible: tuple[int, int] = (0, 0)  # first and last visible row, 1-based

    @property
    def complete(self) -> bool:
        return len(self.rows) == self.total

    @property
    def missing(self) -> tuple[SquadRow, ...]:
        return tuple(row for row in self.rows if row.person_id not in self.indexed)


def _u64(pid: int, address: int) -> int:
    return struct.unpack("<Q", read_process_bytes(pid, address, 8))[0]


def _u32(pid: int, address: int) -> int:
    return struct.unpack("<I", read_process_bytes(pid, address, 4))[0]


def ui_manager(pid: int) -> int:
    """The UI manager FM24's own reload command uses (top of its screen stack)."""
    base = fm24_image_base(pid)
    stack = _u64(pid, _u64(pid, base + MANAGER_GLOBAL_RVA) + 0x68)
    begin, end = _u64(pid, stack + 0x18), _u64(pid, stack + 0x20)
    if not begin or end - begin < 8:
        raise MemoryUnavailable("FM24 has no active UI manager.")
    return _u64(pid, begin)


def _children(pid: int, widget: int) -> list[int]:
    begin, end = _u64(pid, widget + WIDGET_CHILDREN), _u64(pid, widget + WIDGET_CHILDREN + 8)
    if end < begin or (end - begin) % 8 or end - begin > 8 * 8192:
        raise MemoryUnavailable("Invalid FM24 widget children.")
    data = read_process_bytes(pid, begin, end - begin) if end > begin else b""
    return list(struct.unpack(f"<{len(data) // 8}Q", data))


def _attached(pid: int, widget: int, manager: int) -> bool:
    """True when the widget is a child of its parent, up to a parentless root."""
    for _ in range(64):
        if _u64(pid, widget + WIDGET_MANAGER) != manager:
            return False
        parent = _u64(pid, widget + WIDGET_PARENT)
        if not parent:
            return True
        if widget not in _children(pid, parent):
            return False
        widget = parent
    return False


def _player_id(pid: int, person: int) -> str:
    record = person + 12
    raw = read_process_bytes(pid, record, 20)
    number = struct.unpack_from("<I", raw)[0]
    # Marker byte 13 carries flags (0x02, 0x40 seen); 14 is always 0x58.
    if (raw[:8] != struct.pack("<II", number, number) or raw[12] or raw[14] != 0x58 or
            raw[15] not in (0x10, 0x12, 0x18) or raw[16] not in (4, 5) or
            not 1 <= number <= 2_147_483_647):
        raise MemoryUnavailable("A Squad row does not show a validated FM24 player.")
    return str(number)


def _text(pid: int, address: int) -> str:
    size = _u32(pid, address)
    if not 1 <= size <= 80:
        raise MemoryUnavailable("Invalid FM24 row name.")
    value = read_process_bytes(pid, address + 4, size).decode("utf-8")
    if not value.isprintable() or value != value.strip():
        raise MemoryUnavailable("Invalid FM24 row name.")
    return value


def _scan(pid: int, manager: int, catalogue: bool = True) -> tuple[list[int], set[str]]:
    """One pass: name cells of this manager and, with ``catalogue``, the
    indexed portraits."""
    cell_vtable = fm24_image_base(pid) + NAME_CELL_VTABLE_RVA
    needle = struct.pack("<Q", manager)
    no_picture_key = b"\x01\x00\x00\x00" + struct.pack("<I", len(_NO_PICTURE)) + _NO_PICTURE + b"\x00"
    cells, indexed, no_picture = [], set(), False
    scan = MemoryScan(pid, 128)
    for base, data in scan:
        offset = 0
        while (offset := data.find(needle, offset)) >= 0:
            start = offset - WIDGET_MANAGER
            if (base + start) % 8 == 0 and start >= 0 and start + WIDGET_ID + 4 <= len(data) and \
                    struct.unpack_from("<Q", data, start)[0] == cell_vtable and \
                    struct.unpack_from("<I", data, start + WIDGET_ID)[0] == NAME_CELL_ID:
                cells.append(base + start)
            offset += 8
        if not catalogue:
            continue
        offset = 0
        while (offset := data.find(_PERSON_PICTURE, offset)) >= 0:
            # An indexed key is a complete FM string object:
            # {u32 1}{u32 length} "graphics/pictures/person/<id>/portrait" NUL.
            match = _PORTRAIT.match(data, offset + len(_PERSON_PICTURE), offset + len(_PERSON_PICTURE) + 24)
            end = match.end() if match else 0
            if (match and offset >= 8 and end < len(data) and data[end] == 0 and
                    data[offset - 8:offset - 4] == b"\x01\x00\x00\x00" and
                    struct.unpack_from("<I", data, offset - 4)[0] == end - offset):
                indexed.add(match.group(1).decode())
            offset += len(_PERSON_PICTURE)
        no_picture |= no_picture_key in data
    if not scan.complete or (catalogue and not no_picture):
        raise MemoryUnavailable("Could not read FM24's complete portrait index.")
    return cells, indexed


def _dates(pid: int, year: int) -> collections.Counter:
    """Count "d Mon <year>" FM string objects (the top bar shows today's)."""
    key = f" {year}\0".encode()
    dates = collections.Counter()
    for _, data in MemoryScan(pid, 16):
        offset = 0
        while (offset := data.find(key, offset)) >= 0:
            for start in (offset - 5, offset - 6):
                text = data[start:offset + 5]
                match = _DATE.fullmatch(text)
                if (start >= 4 and match and match.group(2).decode() in _MONTHS and
                        struct.unpack_from("<I", data, start - 4)[0] == len(text)):
                    dates[text.decode()] += 1
            offset += len(key)
    return dates


def _today(dates: collections.Counter) -> dt.date:
    """The in-game date shown in FM24's top bar ("2 Jan 2023")."""
    if not dates:
        raise MemoryUnavailable("Could not find FM24's current date.")
    (text, count), *others = dates.most_common(2)
    if count < 2 or (others and others[0][1] * 2 > count):
        raise MemoryUnavailable("Could not identify FM24's current date.")
    day, month, year = text.split()
    return dt.date(int(year), _MONTHS.index(month) + 1, int(day))


def read_squad(pid: int | None = None, attempts: int = 3, catalogue: bool = True) -> SquadState:
    """Return the open Squad table's drawn players and FM24's indexed portraits.

    Only drawn rows have cells; ``complete`` says whether that is every row.
    """
    pid = pid or find_game_pid()
    try:
        manager = ui_manager(pid)
        for attempt in range(attempts):
            try:
                cells, indexed = _scan(pid, manager, catalogue)
                break
            except MemoryUnavailable:
                # FM24 can unmap memory during a scan; a later pass is complete.
                if attempt + 1 == attempts:
                    raise
        tables: dict[int, list[int]] = {}
        for cell in set(cells):
            if _attached(pid, cell, manager):
                tables.setdefault(_u64(pid, cell + WIDGET_PARENT), []).append(cell)
        if len(tables) != 1:
            raise MemoryUnavailable(f"Expected one open FM24 Squad table; found {len(tables)}.")
        table, rows = next(iter(tables.items()))
        total = struct.unpack("<i", read_process_bytes(pid, table + TABLE_ROWS, 4))[0]
        visible = struct.unpack("<ii", read_process_bytes(pid, table + TABLE_VISIBLE, 8))
        if not len(rows) <= total <= 500 or not 1 <= visible[0] <= visible[1] <= total:
            raise MemoryUnavailable("Invalid FM24 Squad table row count.")
        order = {child: index for index, child in enumerate(_children(pid, table))}
        seen, result = set(), []
        for cell in sorted(rows, key=order.__getitem__):
            person = _u64(pid, cell + ROW_PERSON)
            person_id = _player_id(pid, person)
            if person_id not in seen:
                seen.add(person_id)
                result.append(SquadRow(person_id, person + 12, _text(pid, _u64(pid, cell + ROW_NAME))))
    except (OSError, ValueError, struct.error) as exc:
        raise MemoryUnavailable(f"Could not read the FM24 Squad table: {exc}") from exc
    if not result:
        raise MemoryUnavailable("The open FM24 Squad table has no players.")
    today = dt.date.min
    if catalogue:
        # Person records carry the current game year at +0x36.
        years = {struct.unpack("<H", read_process_bytes(pid, row.record + 0x36, 2))[0] for row in result}
        if len(years) != 1 or not 1900 < min(years) < 2200:
            raise MemoryUnavailable("Could not read FM24's current year.")
        today = _today(_dates(pid, years.pop()))
    return SquadState(tuple(result), total, frozenset(indexed), today, visible)


def read_full_squad(scroll, pid: int | None = None, steps: int = 60) -> SquadState:
    """Read every Squad row, scrolling the table until all have been drawn.

    ``scroll(down: bool)`` scrolls the table a few rows. The table's own
    visible-row range tells when its top and bottom are reached, and its row
    count when every row has been read; otherwise this raises.
    """
    pid = pid or find_game_pid()
    state = read_squad(pid)
    rows = {row.person_id: row for row in state.rows}

    def step(down: bool):
        nonlocal state
        before = state.visible
        scroll(down)
        for _ in range(10):  # FM24 redraws the table shortly after scrolling.
            later = read_squad(pid, catalogue=False)
            if later.total != state.total:
                raise MemoryUnavailable("The FM24 Squad table changed while it was being read.")
            if later.visible != before:
                break
            time.sleep(.2)
        rows.update((row.person_id, row) for row in later.rows)
        state = SquadState(state.rows, state.total, state.indexed, state.today, later.visible)
        return later.visible != before

    for _ in range(steps):
        if len(rows) == state.total or state.visible[0] == 1 or not step(False):
            break
    for _ in range(steps):
        if len(rows) == state.total or state.visible[1] == state.total or not step(True):
            break
    if len(rows) != state.total:
        raise MemoryUnavailable(f"Read {len(rows)} of {state.total} FM24 Squad rows.")
    return SquadState(tuple(rows.values()), state.total, state.indexed, state.today, state.visible)
