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
    "record_name": 0x3C,           # pointer to {u32 length, UTF-8} full name, if the person has one
    # Pointers to the first-name and surname objects, which point to their
    # text: the name of the many people (60% of players in a 2026-10-19 Reading
    # save) whose full-name pointer is empty.
    "record_name_parts": (0x4C, 0x54),
    # Bytes 12-15 of a record are flags, not a kind: a survey of every person
    # in a 2026-10-19 Reading save found players with 0x78, 0x58, 0x60, 0x18,
    # 0x40 and 0x00 in byte 14 and many values in 13 and 15. With
    # person_vtables these are used only to find candidate records quickly.
    "record_flags": tuple(range(8)),     # byte 12 of the record
    "record_kinds": (0x58, 0x50, 0x78),  # byte 14
    "name_records_aligned": True,  # first/surname records start on 4-byte boundaries
    "position_ratings": -0x7C,     # 15 bytes, 1-20
    "attributes": -0x6D,           # 54 bytes, 1-100
    "height": -0x136,              # u16 cm
    "nation": 0x64,                # pointer to the nationality's nation object
    "playing_club": -0x154,        # pointer to the club played for now
    "contract": 0xBC,              # pointer to the contract
    "pointer": 4,                  # record and object pointers read (Wine keeps them below 4 GB)
    # The person object's class, from its vtable 12 bytes before the record
    # (MSVC RTTI names). Player-coaches keep no player ratings or attributes
    # in the record, so they are made as staff.
    "person_vtables": {0x5A4EE78: "player",   # db::ACTUAL_PLAYER
                       0x5A4C358: "staff",    # db::ACTUAL_NON_PLAYER
                       0x5C7ECB8: "staff"},   # db::ACTUAL_PLAYER_AND_NON_PLAYER
    # Squad rows of players FM24 keeps only briefly ("Scouting Required").
    "brief_vtables": {0x5C7D4F8},             # db::VIRTUAL_PLAYER
    # Player-coaches, which a squad lists but can't describe as players.
    "player_coach_vtables": {0x5C7ECB8},      # db::ACTUAL_PLAYER_AND_NON_PLAYER
    # db::TEAM, db::CLUB (a team's club details) and db::NATION.
    "team_vtable": 0x5A78848,
    "club_vtable": 0x5A51F38,
    "nation_vtable": 0x5A6C858,
    "object_browser_vtable": 0x56BAE48,  # CLIENT_OBJECT_BROWSER, from fm.exe's RTTI
    "browser_reference": 0x880,    # cleared by a skin reload, like the Mac build's
    "person_table": (0x6426D90, 0x920),  # database global, first slot tried (person_handles)
    "sidebar_vtable": 0x57E3B48,   # SIDEBAR_MENU_TABLE, as in the DLL
    "sidebar_reference": 0x280,
    "table_model": 0x6E8,          # a Squad table widget's row model (native_squad)
}

MACOS = {
    "manager_global": 0x525D3D0,
    "name_cell_vtable": 0x5093B18,
    "record_year": -0x13A,
    "record_birth": -0x138,
    "record_name": -0x134,
    # As on Windows, 0x10 and 0x18 after the full-name pointer: the name of
    # most people (16,985 of 30,943 in a 2026-10-21 MK Dons save have no
    # full-name string).
    "record_name_parts": (-0x124, -0x11C),
    "record_flags": tuple(range(8)),  # 0 for most people; 1, 2, 4, 5 and 6 seen
    "record_kinds": (0x58, 0x50, 0x78),  # 0x50 on staff and reserve records (types 0 and 1); 0x78 as on Windows
    "name_records_aligned": False,    # first/surname records can start anywhere
    "position_ratings": -0x1B4,
    "attributes": -0x1A5,
    "height": -0x26E,
    "nation": -0x10C,
    "playing_club": -0x28C,
    "contract": -0xB4,
    "pointer": 8,
    # The person object's class, from its vtable 12 bytes before the record
    # (Itanium RTTI names, fm24/research/survey_people.py). As on Windows,
    # player-coaches are made as staff.
    "person_vtables": {0x51E6FD0: "player",   # db::ACTUAL_PLAYER
                       0x51E1150: "staff",    # db::ACTUAL_NON_PLAYER
                       0x51ED1C8: "staff"},   # db::ACTUAL_PLAYER_AND_NON_PLAYER
    "brief_vtables": {0x521B770},             # db::VIRTUAL_PLAYER
    "player_coach_vtables": {0x51ED1C8},
    # db::TEAM, db::CLUB and db::NATION.
    "team_vtable": 0x5216EE8,
    "club_vtable": 0x51F9BA8,
    "nation_vtable": 0x52099F0,
    # The open screen's CLIENT_OBJECT_BROWSER and its reference to the object
    # shown: type << 24 | index, 0x01000000 | handle for a person.
    "object_browser_vtable": 0x4F77650,
    "browser_reference": 0x878,
    # The global (from the image header) pointing at the game's database,
    # and the slot where its vector of person pointers, indexed by handle,
    # was seen (+0x318). person_handles searches the other slots when it
    # isn't there: on fm.exe the slot changes between runs.
    "person_table": (0x5304848, 0x318),
    # The sidebar menu keeps the screen's object reference, also after a skin
    # reload, when the object browser's stops being updated.
    "sidebar_vtable": 0x5060DC0,
    "sidebar_reference": 0x278,
    "table_model": 0x6C8,
}

# A copy, so tests with records from one build can select its layout on any
# system (before importing the modules that read memory).
LAYOUT = dict(MACOS if sys.platform == "darwin" else WINDOWS)
EXECUTABLE_SUFFIX = "/fm.app/contents/macos/fm" if sys.platform == "darwin" else "/fm.exe"
