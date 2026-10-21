"""Read the open FM24 screen's header and biography from its widget tree.

The UI manager's root widget (manager+0x58) is the open screen. Its tree is
walked through each widget's children vector, keeping only widgets of this
manager. The header ('head') holds the subtitle ('subs') whose text ends in
"(ID:n)" when screen IDs are enabled; without them, the person comes from the
screen's object browser (person_handles). The biography panel's 'biot'
widget holds the rich-text biography FM24 is displaying, a text widget the
person's "<n> years old", and the top bar's 'dtam' widgets the in-game date. Read-only; layouts are those
of the pinned 24.4.2 build, and anything unexpected raises MemoryUnavailable.
"""

from __future__ import annotations

import datetime as dt
import re
import struct
from dataclasses import dataclass

from layout import LAYOUT
from live_memory import MemoryUnavailable, find_game_pid
from person_handles import person_record_for_reference, screen_reference
from native_profile import _RICH_LINK
from native_squad import WIDGET_ID, WIDGET_MANAGER, _children, _u32, _u64, ui_manager
from process_memory import read_process_bytes

SCREEN_ROOT = 0x58
SUBTITLE_TEXT = 0x190      # widget part +0x158: {u32 length, UTF-8}
BIOGRAPHY_TEXT = 0x188     # 'biot' widget: {u32 length, rich text}
WIDGET_TEXT = 0x188        # a text widget's {u32 length, UTF-8}, as the biography's
MAX_WIDGETS = 20000
_ID = re.compile(r"\(ID:([1-9][0-9]{0,9})\)$")
_AGE = re.compile(rb"([1-9][0-9]?) years old")
_DATE = re.compile(rb"([1-9]|[12][0-9]|3[01]) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) ((?:19|20)[0-9]{2})")
_MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")


@dataclass(frozen=True)
class OpenScreen:
    person_id: str
    biography: str          # first paragraph shown, or "" when none is shown
    title: str = ""         # the header's "Role - Club" text, without its ID
    record: int = 0         # the person's record, when FM24's person table gave the person
    ages: frozenset[int] = frozenset()  # the "<n> years old" texts shown
    today: dt.date | None = None  # the in-game date in the top bar


def _skin_id(name: bytes) -> int:
    return int.from_bytes(name, "big")


def _tree(pid: int, manager: int, root: int) -> dict[int, list[int]]:
    """Widgets under ``root`` by skin ID, bounded and without cycles."""
    found: dict[int, list[int]] = {}
    seen, pending = set(), [root]
    while pending:
        widget = pending.pop()
        if widget in seen:
            continue
        seen.add(widget)
        if len(seen) > MAX_WIDGETS:
            raise MemoryUnavailable("The FM24 screen has too many widgets to read.")
        try:
            if _u64(pid, widget + WIDGET_MANAGER) != manager:
                continue
            skin_id = _u32(pid, widget + WIDGET_ID)
        except OSError:
            continue  # Not a widget: a leaf's data looked like a children vector.
        found.setdefault(skin_id, []).append(widget)
        try:
            pending.extend(_children(pid, widget))
        except (MemoryUnavailable, OSError):
            pass  # Not a container: other data where the children vector would be.
    return found


def _string(pid: int, widget: int, offset: int, limit: int) -> bytes:
    text = _u64(pid, widget + offset)
    if not text:
        return b""
    size = _u32(pid, text)
    if size > 1 << 20:
        raise MemoryUnavailable("Invalid FM24 widget text.")
    size = min(size, limit)
    return b"".join(read_process_bytes(pid, text + 4 + start, min(4096, size - start))
                    for start in range(0, size, 4096))


def _first_paragraph(raw: bytes) -> str:
    """The first paragraph of a rich-text biography, links reduced to their text."""
    first = raw.split(b"\n\n", 1)[0].split(b"\x00", 1)[0]
    try:
        text = _RICH_LINK.sub(b"", first).replace(b"\x01", b"").decode("utf-8")
    except UnicodeDecodeError as exc:
        raise MemoryUnavailable("Invalid FM24 biography text.") from exc
    text = " ".join(text.split())
    if len(text) > 2000 or not text.isprintable():
        raise MemoryUnavailable("Invalid FM24 biography text.")
    return text


def read_open_screen(pid: int | None = None) -> OpenScreen:
    """The person ID in the open screen's header, and the biography shown."""
    pid = pid or find_game_pid()
    try:
        manager = ui_manager(pid)
        widgets = _tree(pid, manager, _u64(pid, manager + SCREEN_ROOT))
        heads = widgets.get(_skin_id(b"head"), [])
        if len(heads) != 1:
            raise MemoryUnavailable(f"Expected one FM24 profile header; found {len(heads)}.")
        subtitles = _tree(pid, manager, heads[0]).get(_skin_id(b"subs"), [])
        if len(subtitles) != 1:
            raise MemoryUnavailable("Could not find the FM24 profile subtitle.")
        subtitle = _string(pid, subtitles[0], SUBTITLE_TEXT, 256).decode("utf-8")
        match = _ID.search(subtitle)
        if match:
            person_id, title, record = match.group(1), subtitle[:match.start()].strip(), 0
        elif LAYOUT["browser_reference"] is not None:
            person_id, record = person_record_for_reference(pid, screen_reference(pid, manager, widgets))
            title = subtitle.strip()
        else:
            raise MemoryUnavailable("The FM24 profile header shows no ID; enable screen IDs.")
        biographies = widgets.get(_skin_id(b"biot"), [])
        if len(biographies) > 1:
            raise MemoryUnavailable("The FM24 screen shows more than one biography.")
        biography = (_first_paragraph(_string(pid, biographies[0], BIOGRAPHY_TEXT, 16384))
                     if biographies else "")
    except (OSError, ValueError, struct.error) as exc:
        raise MemoryUnavailable(f"Could not read the open FM24 screen: {exc}") from exc
    return OpenScreen(person_id, biography, title, record, _ages(pid, widgets), shown_date(pid, widgets))


def shown_date(pid: int, widgets: dict[int, list[int]]) -> dt.date | None:
    """The in-game date FM24's top bar shows ("16 Sep 2023", its 'dtam'
    widgets), or None if it isn't shown or its widgets disagree."""
    dates = set()
    for widget in widgets.get(_skin_id(b"dtam"), []):
        try:
            text = _u64(pid, widget + WIDGET_TEXT)
            if text and 10 <= _u32(pid, text) <= 11:
                if match := _DATE.fullmatch(read_process_bytes(pid, text + 4, _u32(pid, text))):
                    day, month, year = match.groups()
                    dates.add(dt.date(int(year), _MONTHS.index(month.decode()) + 1, int(day)))
        except (OSError, ValueError, struct.error):
            continue
    return dates.pop() if len(dates) == 1 else None


def screen_date(pid: int) -> dt.date | None:
    """The in-game date shown on the open screen, or None."""
    try:
        manager = ui_manager(pid)
        return shown_date(pid, _tree(pid, manager, _u64(pid, manager + SCREEN_ROOT)))
    except (MemoryUnavailable, OSError, ValueError, struct.error):
        return None

def _ages(pid: int, widgets: dict[int, list[int]]) -> frozenset[int]:
    """The ages in the screen's "<n> years old" texts: the profile's, so its
    age needn't be found by searching all of FM24's memory (seconds on macOS)."""
    ages = set()
    for widget in (widget for group in widgets.values() for widget in group):
        try:
            text = _u64(pid, widget + WIDGET_TEXT)
            if text and 10 <= _u32(pid, text) <= 16:
                if match := _AGE.fullmatch(read_process_bytes(pid, text + 4, _u32(pid, text))):
                    ages.add(int(match.group(1)))
        except (OSError, ValueError, struct.error):
            continue
    return frozenset(ages)
