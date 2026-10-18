"""FM24 24.4.2 memory layouts: the Windows fm.exe (on Windows and under Proton)
and the macOS arm64 build. The companion reads the same game objects on both;
the Mac build lays some of them out differently (clang orders base classes
differently from MSVC, and its heap pointers need all 64 bits).

Person-record offsets are from the record that starts with the person's ID
twice (<id><id>), 12 bytes into the person object. Addresses are offsets
from the image header.
"""

from __future__ import annotations

import sys

WINDOWS = {
    "manager_global": 0x63635B8,
    "name_cell_vtable": 0x56807C8,
    "record_year": 0x36,           # u16 current game year
    "record_birth": 0x38,          # u16 day of year, u16 year
    "record_name": 0x3C,           # pointer to {u32 length, UTF-8} full name
    "record_flags": (0,),          # byte 12 of the record
    "record_kinds": (0x58,),       # byte 14 of the record
    "name_records_aligned": True,  # first/surname records start on 4-byte boundaries
    "position_ratings": -0x7C,     # 15 bytes, 1-20
    "attributes": -0x6D,           # 54 bytes, 1-100
    "height": -0x136,              # u16 cm
    "nation": 0x64,                # pointer to the nationality's nation object
    "playing_club": -0x154,        # pointer to the club played for now
    "contract": 0xBC,              # pointer to the contract
    "pointer": 4,                  # record and object pointers read (Wine keeps them below 4 GB)
    "person_vtables": None,        # the kind comes from the record type and position ratings
    "object_browser_vtable": 0x56BAE48,  # CLIENT_OBJECT_BROWSER, from fm.exe's RTTI
    "browser_reference": None,     # not yet confirmed on fm.exe (probe_object_browser.py)
    "person_table": None,          # not mapped on fm.exe; person_handles scans memory instead
    "sidebar_vtable": None,        # SIDEBAR_MENU_TABLE; not mapped on fm.exe
    "sidebar_reference": None,
}

MACOS = {
    "manager_global": 0x525D3D0,
    "name_cell_vtable": 0x5093B18,
    "record_year": -0x13A,
    "record_birth": -0x138,
    "record_name": -0x134,
    "record_flags": tuple(range(8)),  # 0 for most people; 1, 2, 4, 5 and 6 seen
    "record_kinds": (0x58, 0x50),     # 0x50 seen on staff and reserve records (types 0 and 1)
    "name_records_aligned": False,    # first/surname records can start anywhere
    "position_ratings": -0x1B4,
    "attributes": -0x1A5,
    "height": -0x26E,
    "nation": -0x10C,
    "playing_club": -0x28C,
    "contract": -0xB4,
    "pointer": 8,
    # The person object's class, from its vtable 12 bytes before the record:
    # db::ACTUAL_PLAYER and db::ACTUAL_NON_PLAYER. Player-coaches
    # (db::ACTUAL_PLAYER_AND_NON_PLAYER) keep their player fields elsewhere
    # and are refused. The bytes where a player's ratings are hold other data
    # for staff, so they can't tell staff apart as they do on Windows.
    "person_vtables": {0x51E6FD0: "player", 0x51E1150: "staff"},
    # The open screen's CLIENT_OBJECT_BROWSER and its reference to the object
    # shown: type << 24 | index, 0x01000000 | handle for a person.
    "object_browser_vtable": 0x4F77650,
    "browser_reference": 0x878,
    # The global (from the image header) pointing at the game's database,
    # whose vector of person pointers, indexed by handle, starts at +0x318.
    "person_table": (0x5304848, 0x318),
    # The sidebar menu keeps the screen's object reference, also after a skin
    # reload, when the object browser's stops being updated.
    "sidebar_vtable": 0x5060DC0,
    "sidebar_reference": 0x278,
}

# A copy, so tests with records from one build can select its layout on any
# system (before importing the modules that read memory).
LAYOUT = dict(MACOS if sys.platform == "darwin" else WINDOWS)
EXECUTABLE_SUFFIX = "/fm.app/contents/macos/fm" if sys.platform == "darwin" else "/fm.exe"
