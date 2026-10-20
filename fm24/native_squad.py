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
from dataclasses import dataclass

from layout import LAYOUT
from live_memory import (MemoryUnavailable, _parse_record, find_game_pid, is_person_record, object_class,
                         person_kind, record_marker)
from native_profile import collect_object_names, _alternate_name
from process_memory import MemoryScan, fm24_image_base, read_process_bytes

MANAGER_GLOBAL_RVA = LAYOUT["manager_global"]
NAME_CELL_VTABLE_RVA = LAYOUT["name_cell_vtable"]
NAME_CELL_ID = int.from_bytes(b"Pnms", "big")
# A team's Overview squad list (a reserve or youth team in the Development
# Center) uses the same cell class named PnmP, two levels inside a row widget
# whose parent is the list; the list keeps rows, visible rows and its row
# model at the same offsets as the Squad table.
LIST_CELL_ID = int.from_bytes(b"PnmP", "big")
CELL_IDS = (NAME_CELL_ID, LIST_CELL_ID)
ROW_PERSON = 0x728
ROW_NAME = 0x1C8            # cell -> {u32 length, UTF-8} display name ("Ian Fray")
TABLE_ROWS = 0x3A0          # table widget: total rows, drawn or not
TABLE_VISIBLE = 0x394       # table widget: first and last visible row, 1-based
# Every row of the table, drawn or not: table -> row model, whose vector at
# +0x18 lists the row objects (the header row first). A row object starts with
# a vector of {fourcc key, value pointer} properties; its 'id  ' value holds
# a u32 at +8. A person row's id is 0x01000000 | the person's handle, the u32
# at person+8, and FM24 keeps person pointers in a heap table indexed by handle.
TABLE_MODEL = 0x6E8
MODEL_ROWS = 0x18
ROW_ID_KEY = int.from_bytes(b"id  ", "big")
HEADER_ROW_ID = int.from_bytes(b"HROW", "big")
PERSON_HANDLE = 8
PERSON_ROW = 0x01000000
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
    table: int = 0                      # the table widget, for the row model
    unreadable: tuple[str, ...] = ()    # undrawn players whose names FM24 hasn't loaded
    brief: tuple[str, ...] = ()         # players FM24 keeps only a brief record for

    @property
    def complete(self) -> bool:
        return len(self.rows) + len(self.unreadable) + len(self.brief) == self.total

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


class BriefRecord(MemoryUnavailable):
    """A row's person has only FM24's brief record (no age or attributes)."""

    def __init__(self, person_id: str):
        super().__init__(f"FM24 keeps only a brief record for person {person_id}.")
        self.person_id = person_id


def _player_id(pid: int, person: int) -> str:
    record = person + 12
    raw = read_process_bytes(pid, record, 20)
    number = struct.unpack_from("<I", raw)[0]
    if raw[:8] != struct.pack("<II", number, number) or not 1 <= number <= 2_147_483_647:
        raise MemoryUnavailable("A Squad row does not show a validated FM24 player.")
    # Rows FM24 lists as "Scouting Required" with no age or ability (players
    # of leagues it hasn't loaded) are brief objects (db::VIRTUAL_PLAYER)
    # with none of the fields a prompt needs. Where the layout doesn't map
    # that class, their record markers tell (00 02 00 10, 08 02 40 30).
    if LAYOUT["brief_vtables"] is not None:
        if object_class(pid, record) in LAYOUT["brief_vtables"]:
            raise BriefRecord(str(number))
        if not is_person_record(pid, record, raw):
            raise MemoryUnavailable("A Squad row does not show a validated FM24 player.")
    elif not record_marker(raw):
        raise BriefRecord(str(number))
    if person_kind(pid, record, raw[16]) != "player":
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


def _count_dates(data: bytes, dates: collections.Counter) -> None:
    """Count "d Mon 20YY" FM string objects in a chunk (the top bar shows today's)."""
    offset = 0
    while (offset := data.find(b" 20", offset)) >= 0:
        if data[offset + 3:offset + 5].isdigit() and data[offset + 5:offset + 6] == b"\0":
            for start in (offset - 5, offset - 6):
                text = data[start:offset + 5]
                match = _DATE.fullmatch(text)
                if (start >= 4 and match and match.group(2).decode() in _MONTHS and
                        struct.unpack_from("<I", data, start - 4)[0] == len(text)):
                    dates[text.decode()] += 1
        offset += 3


def _scan(pid: int, manager: int, catalogue: bool = True) -> tuple[list[int], set[str], collections.Counter]:
    """One pass: name cells of this manager and, with ``catalogue``, the
    indexed portraits and the dates shown in FM strings."""
    cell_vtable = fm24_image_base(pid) + NAME_CELL_VTABLE_RVA
    needle = struct.pack("<Q", manager)
    no_picture_key = b"\x01\x00\x00\x00" + struct.pack("<I", len(_NO_PICTURE)) + _NO_PICTURE + b"\x00"
    cells, indexed, no_picture, dates = [], set(), False, collections.Counter()
    scan = MemoryScan(pid, 128)
    for base, data in scan:
        offset = 0
        while (offset := data.find(needle, offset)) >= 0:
            start = offset - WIDGET_MANAGER
            if (base + start) % 8 == 0 and start >= 0 and start + WIDGET_ID + 4 <= len(data) and \
                    struct.unpack_from("<Q", data, start)[0] == cell_vtable and \
                    struct.unpack_from("<I", data, start + WIDGET_ID)[0] in CELL_IDS:
                cells.append((base + start, struct.unpack_from("<I", data, start + WIDGET_ID)[0]))
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
        _count_dates(data, dates)
    if not scan.complete or (catalogue and not no_picture):
        raise MemoryUnavailable("Could not read FM24's complete portrait index.")
    return cells, indexed, dates


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
                cells, indexed, dates = _scan(pid, manager, catalogue)
                break
            except MemoryUnavailable:
                # FM24 can unmap memory during a scan; a later pass is complete.
                if attempt + 1 == attempts:
                    raise
        # Table -> {its child holding a row: that row's name cell}, per cell kind.
        tables: dict[int, dict[int, dict[int, int]]] = {NAME_CELL_ID: {}, LIST_CELL_ID: {}}
        for cell, kind in set(cells):
            if _attached(pid, cell, manager):
                row = cell if kind == NAME_CELL_ID else _u64(pid, _u64(pid, cell + WIDGET_PARENT) + WIDGET_PARENT)
                tables[kind].setdefault(_u64(pid, row + WIDGET_PARENT), {})[row] = cell
        # The Squad table when there is one, so a list elsewhere on the screen
        # can't make the first team's Squad command ambiguous.
        found = tables[NAME_CELL_ID] or tables[LIST_CELL_ID]
        if not found:
            raise MemoryUnavailable("No squad player list is open; open the team's Squad view and try again.")
        if len(found) != 1:
            raise MemoryUnavailable(f"Expected one open FM24 Squad table; found {len(found)}.")
        table, rows = next(iter(found.items()))
        total = struct.unpack("<i", read_process_bytes(pid, table + TABLE_ROWS, 4))[0]
        visible = struct.unpack("<ii", read_process_bytes(pid, table + TABLE_VISIBLE, 8))
        if not len(rows) <= total <= 500 or not 1 <= visible[0] <= visible[1] <= total:
            raise MemoryUnavailable("Invalid FM24 Squad table row count.")
        order = {child: index for index, child in enumerate(_children(pid, table))}
        seen, result, brief = set(), [], []
        for cell in (rows[row] for row in sorted(rows, key=order.__getitem__)):
            person = _u64(pid, cell + ROW_PERSON)
            try:
                person_id = _player_id(pid, person)
            except BriefRecord as exc:
                if exc.person_id not in brief:
                    brief.append(exc.person_id)
                continue
            if person_id not in seen:
                seen.add(person_id)
                result.append(SquadRow(person_id, person + 12, _text(pid, _u64(pid, cell + ROW_NAME))))
    except (OSError, ValueError, struct.error) as exc:
        raise MemoryUnavailable(f"Could not read the FM24 Squad table: {exc}") from exc
    if not result:
        raise MemoryUnavailable(
            "FM24 keeps only brief records for the players shown (no age or ability), so FM Face "
            "can't describe them." if brief else "The open FM24 Squad table has no players.")
    today = dt.date.min
    if catalogue:
        # Person records carry the current game year (layout "record_year").
        years = {struct.unpack("<H", read_process_bytes(pid, row.record + LAYOUT["record_year"], 2))[0]
                 for row in result}
        if len(years) != 1 or not 1900 < min(years) < 2200:
            raise MemoryUnavailable("Could not read FM24's current year.")
        year = str(years.pop())
        today = _today(collections.Counter({text: count for text, count in dates.items()
                                            if text.endswith(" " + year)}))
    return SquadState(tuple(result), total, frozenset(indexed), today, visible, table, brief=tuple(brief))


def _row_ids(pid: int, table: int) -> list[int]:
    """The 'id  ' of every data row in the table's row model, in table order."""
    model = _u64(pid, table + TABLE_MODEL)
    begin, end = _u64(pid, model + MODEL_ROWS), _u64(pid, model + MODEL_ROWS + 8)
    if end <= begin or (end - begin) % 8 or end - begin > 8 * 501:
        raise MemoryUnavailable("Invalid FM24 Squad row model.")
    ids = []
    for row in struct.unpack(f"<{(end - begin) // 8}Q", read_process_bytes(pid, begin, end - begin)):
        first, last = _u64(pid, row), _u64(pid, row + 8)
        if last < first or (last - first) % 16 or last - first > 16 * 32:
            raise MemoryUnavailable("Invalid FM24 Squad row.")
        properties = read_process_bytes(pid, first, last - first) if last > first else b""
        values = [value for key, value in struct.iter_unpack("<QQ", properties) if key & 0xFFFFFFFF == ROW_ID_KEY]
        if len(values) != 1:
            raise MemoryUnavailable("An FM24 Squad row has no single row ID.")
        ids.append(_u32(pid, values[0] + 8))
    if not ids or ids[0] != HEADER_ROW_ID or any(row >> 24 != PERSON_ROW >> 24 for row in ids[1:]):
        raise MemoryUnavailable("The FM24 Squad row model does not list people.")
    return ids[1:]


def _handle_table(pid: int, people: list[int]) -> int:
    """Base of FM24's person table, found from drawn rows' person pointers.

    One pass finds where the first person's pointer is stored; the base is the
    one that also holds every other drawn person at their own handles. With at
    least five people that cannot be a coincidence, so the pass stops there.
    """
    if len(people) < 2:
        raise MemoryUnavailable("Too few drawn FM24 Squad rows to locate the person table.")
    handles = [_u32(pid, person + PERSON_HANDLE) for person in people]
    cached = _person_tables.get(pid)
    if cached:
        # The table stays put while the game runs; check it still holds every drawn person.
        try:
            if all(_u64(pid, cached + 8 * handle) == person for person, handle in zip(people, handles)):
                return cached
        except OSError:
            pass
    _person_tables[pid] = base = _find_handle_table(pid, people, handles)
    return base


_person_tables: dict[int, int] = {}


def _find_handle_table(pid: int, people: list[int], handles: list[int]) -> int:
    needle = struct.pack("<Q", people[0])
    bases = set()
    for base, data in MemoryScan(pid, 8):
        offset = 0
        while (offset := data.find(needle, offset)) >= 0:
            candidate = base + offset - 8 * handles[0]
            if (base + offset) % 8 == 0 and candidate > 0:
                try:
                    if all(_u64(pid, candidate + 8 * handle) == person
                           for person, handle in zip(people[1:], handles[1:])):
                        bases.add(candidate)
                except OSError:
                    pass
            offset += 8
        if len(bases) == 1 and len(people) >= 5:
            break
    if len(bases) != 1:
        raise MemoryUnavailable(f"Expected one FM24 person table; found {len(bases)}.")
    return bases.pop()


def _undrawn_names(pid: int, rows: dict[str, int]) -> dict[str, str]:
    """Names of players without a drawn cell, from their person records.

    ``rows`` maps person ID to record. Players whose record has no full-name
    string use FM24's separate first/surname record, or its "object: <id> -
    <name>" text, found in one pass. Players with neither are left out.
    """
    names, alternate = {}, {}
    for person_id, record in rows.items():
        identity = _parse_record(pid, record, int(person_id))
        if identity is not None:
            names[person_id] = identity.name
        else:
            alternate[struct.pack("<II", int(person_id), int(person_id)) + b"\x09"] = person_id
    found: dict[str, set[str]] = {}
    if alternate:
        wanted = set(alternate.values())
        for _, data in MemoryScan(pid, 128):
            for needle, person_id in alternate.items():
                offset = 0
                while (offset := data.find(needle, offset)) >= 0:
                    if (name := _alternate_name(data[offset:offset + 128])) is not None:
                        found.setdefault(person_id, set()).add(name)
                    offset += 1
            collect_object_names(data, wanted, found)
    for person_id in alternate.values():
        if len(found.get(person_id, ())) == 1:
            names[person_id] = found[person_id].pop()
    return names


def read_squad_rows(pid: int | None = None) -> SquadState:
    """Every Squad row, drawn or not, without scrolling the table.

    Drawn rows keep their displayed name; the others use the person record's
    name. Each row resolves through the person table to a validated player,
    and every drawn row must be among them.
    """
    pid = pid or find_game_pid()
    state = read_squad(pid)
    if state.complete:
        return state
    try:
        ids = _row_ids(pid, state.table)
        if len(ids) != state.total or len(set(ids)) != len(ids):
            raise MemoryUnavailable("The FM24 Squad row model does not match the table.")
        base = _handle_table(pid, [row.record - 12 for row in state.rows])
        drawn = {row.person_id: row for row in state.rows}
        records, brief = [], []
        for row_id in ids:
            handle = row_id & 0xFFFFFF
            person = _u64(pid, base + 8 * handle)
            if _u32(pid, person + PERSON_HANDLE) != handle:
                raise MemoryUnavailable("An FM24 Squad row does not resolve to its person.")
            try:
                records.append((_player_id(pid, person), person + 12))
            except BriefRecord as exc:
                brief.append(exc.person_id)
    except (OSError, ValueError, struct.error) as exc:
        raise MemoryUnavailable(f"Could not read the FM24 Squad rows: {exc}") from exc
    if not drawn.keys() <= {person_id for person_id, _ in records}:
        raise MemoryUnavailable("The FM24 Squad row model does not contain the drawn rows.")
    names = _undrawn_names(pid, {person_id: record for person_id, record in records
                                 if person_id not in drawn})
    rows = tuple(drawn.get(person_id) or SquadRow(person_id, record, names[person_id])
                 for person_id, record in records if person_id in drawn or person_id in names)
    unreadable = tuple(person_id for person_id, _ in records if person_id not in drawn and person_id not in names)
    return SquadState(rows, state.total, state.indexed, state.today, state.visible, state.table, unreadable,
                      tuple(brief))
