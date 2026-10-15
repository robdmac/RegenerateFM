"""Read the open FM24 screen's header and biography from its widget tree.

The UI manager's root widget (manager+0x58) is the open screen. Its tree is
walked through each widget's children vector, keeping only widgets of this
manager. The header ('head') holds the subtitle ('subs') whose text ends in
"(ID:n)" when screen IDs are enabled; the biography panel's 'biot' widget
holds the rich-text biography FM24 is displaying. Read-only; layouts are those
of the pinned 24.4.2 build, and anything unexpected raises MemoryUnavailable.
"""

from __future__ import annotations

import re
import struct
from dataclasses import dataclass

from live_memory import MemoryUnavailable, find_game_pid
from native_profile import _RICH_LINK
from native_squad import WIDGET_ID, WIDGET_MANAGER, _children, _u32, _u64, ui_manager
from process_memory import read_process_bytes

SCREEN_ROOT = 0x58
SUBTITLE_TEXT = 0x190      # widget part +0x158: {u32 length, UTF-8}
BIOGRAPHY_TEXT = 0x188     # 'biot' widget: {u32 length, rich text}
MAX_WIDGETS = 20000
_ID = re.compile(r"\(ID:([1-9][0-9]{0,9})\)$")


@dataclass(frozen=True)
class OpenScreen:
    person_id: str
    biography: str          # first paragraph shown, or "" when none is shown


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
        if not match:
            raise MemoryUnavailable("The FM24 profile header shows no ID; enable screen IDs.")
        biographies = widgets.get(_skin_id(b"biot"), [])
        if len(biographies) > 1:
            raise MemoryUnavailable("The FM24 screen shows more than one biography.")
        biography = (_first_paragraph(_string(pid, biographies[0], BIOGRAPHY_TEXT, 16384))
                     if biographies else "")
    except (OSError, ValueError, struct.error) as exc:
        raise MemoryUnavailable(f"Could not read the open FM24 screen: {exc}") from exc
    return OpenScreen(match.group(1), biography)
