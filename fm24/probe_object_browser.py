"""Read-only probe: where FM24's object browser keeps the open person.

The screen's CLIENT_OBJECT_BROWSER holds a 32-bit reference to the object it
shows: 0x01000000 | the person's handle for a person. On the macOS build it
is at +0x878. Run this with a person's profile open in FM24 and "Show screen
IDs in Title Bar" ticked: it reads the shown ID, finds that person's handle,
and prints every offset in every live object browser that holds the person's
reference. Nothing is written to the game.
"""

from __future__ import annotations

import argparse
import struct
import sys

from layout import LAYOUT
from live_memory import MemoryUnavailable, find_game_pid
from native_profile import _person_record
from native_screen import read_open_screen
from process_memory import MemoryScan, fm24_image_base, read_process_bytes

BROWSER_SIZE = 0x1000


def browsers(pid: int, vtable: int) -> list[int]:
    """Addresses of objects whose vtable pointer is the object browser's."""
    needle = struct.pack("<Q", vtable)
    found = set()
    for base, data in MemoryScan(pid, 7):
        offset = 0
        while (offset := data.find(needle, offset)) >= 0:
            if (base + offset) % 8 == 0:
                found.add(base + offset)
            offset += 1
    return sorted(found)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pid", type=int, help="the FM24 process (found by default)")
    args = parser.parse_args()
    try:
        pid = args.pid or find_game_pid()
        person_id = read_open_screen(pid).person_id
        record, identity = _person_record(pid, person_id)
        handle = struct.unpack("<I", read_process_bytes(pid, record - 4, 4))[0]
        reference = 0x01000000 | handle
        vtable = fm24_image_base(pid) + LAYOUT["object_browser_vtable"]
        print(f"Open profile: {identity.name} (ID {person_id}), handle {handle:#x}, reference {reference:#010x}")
        found = browsers(pid, vtable)
        print(f"{len(found)} object browser(s)")
        for browser in found:
            data = b"".join(read_process_bytes(pid, browser + start, 0x200)
                            for start in range(0, BROWSER_SIZE, 0x200))
            offsets = [offset for offset in range(0, BROWSER_SIZE - 3, 4)
                       if struct.unpack_from("<I", data, offset)[0] == reference]
            print(f"  {browser:#x}: reference at " + (", ".join(f"+{o:#x}" for o in offsets) or "no offset"))
    except (MemoryUnavailable, OSError, ValueError) as exc:
        sys.exit(f"FM24 probe failed: {exc}")


if __name__ == "__main__":
    main()
