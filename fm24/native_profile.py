"""Read the open FM24 profile from validated game and UI memory records.

Offsets here were derived by comparing live FM24 24.4.2 records with several
independent player and staff profiles. Every pointer and value is checked before
it can be used in a prompt. This module never writes to the game process.
"""

from __future__ import annotations

import collections
import datetime as dt
import re
import struct

from live_memory import (MemoryUnavailable, PersonIdentity,
                         _record_addresses, find_game_pid, read_person_record)
from process_memory import MemoryScan, read_process_bytes


PHYSICAL = {"Acceleration": 34, "Agility": 46, "Balance": 42,
            "Jumping Reach": 39, "Natural Fitness": 50, "Pace": 38,
            "Stamina": 37, "Strength": 36}
TECHNICAL = {"Corners": 27, "Crossing": 0, "Dribbling": 1,
             "Finishing": 2, "First Touch": 22, "Free Kick Taking": 35,
             "Heading": 3, "Long Shots": 4, "Long Throws": 30,
             "Marking": 5, "Passing": 7, "Penalty Taking": 8,
             "Tackling": 9, "Technique": 23}
MENTAL = {"Aggression": 45, "Anticipation": 17, "Bravery": 43,
          "Composure": 52, "Concentration": 53, "Decisions": 18,
          "Determination": 51, "Flair": 26, "Leadership": 40,
          "Off The Ball": 6, "Positioning": 20, "Teamwork": 28,
          "Vision": 10, "Work Rate": 29}
GOALKEEPING = {"Aerial Reach": 12, "Command Of Area": 13,
               "Communication": 14, "Eccentricity": 31,
               "First Touch": 22, "Handling": 11, "Kicking": 15,
               "One On Ones": 19, "Passing": 7,
               "Punching (Tendency)": 33, "Reflexes": 21,
               "Rushing Out (Tendency)": 32, "Throwing": 16}
POSITIONS = ("Goalkeeper", "Sweeper", "Defender (Left)",
             "Defender (Center)", "Defender (Right)",
             "Defensive Midfielder", "Midfielder (Left)",
             "Midfielder (Center)", "Midfielder (Right)",
             "Attacking Midfielder (Left)", "Attacking Midfielder (Center)",
             "Attacking Midfielder (Right)", "Striker (Center)",
             "Wing Back (Left)", "Wing Back (Right)")
_TITLE = re.compile(rb"([^\x00\r\n]{3,100}) - ([^\x00\r\n]{3,80}) \(ID:([1-9][0-9]{0,9})\)")
_RICH_LINK = re.compile(rb"\x01[A-Za-z0-9+/=]{12,500}\x02")


def _u32(pid: int, address: int) -> int:
    return struct.unpack("<I", read_process_bytes(pid, address, 4))[0]


def _alternate_name(raw: bytes) -> str | None:
    """Decode "<id><id> 09 <u32 n><first> <u32 n><surname>" followed by 00,
    or by a third string, the common name (e.g. "Gabriel" "Fortes Chaves"
    "Gabriel Pec"). Returns "first surname", or None."""
    if len(raw) < 20 or raw[8] != 0x09:
        return None
    first_size = struct.unpack_from("<I", raw, 9)[0]
    if not 1 <= first_size <= 40 or 17 + first_size > len(raw):
        return None
    last_size = struct.unpack_from("<I", raw, 13 + first_size)[0]
    end = 17 + first_size + last_size
    if not 1 <= last_size <= 40 or end >= len(raw):
        return None
    texts = [raw[13:13 + first_size], raw[17 + first_size:end]]
    if raw[end] != 0:
        if end + 4 > len(raw):
            return None
        common_size = struct.unpack_from("<I", raw, end)[0]
        if not 1 <= common_size <= 40 or end + 4 + common_size >= len(raw):
            return None
        texts.append(raw[end + 4:end + 4 + common_size])
    try:
        first, last, *common = (text.decode("utf-8") for text in texts)
    except UnicodeDecodeError:
        return None
    if not all(text.isprintable() and text == text.strip() for text in [first, last, *common]):
        return None
    return first + " " + last


def _record_with_alternate_name(pid: int, person_id: str) -> tuple[int, PersonIdentity]:
    """Validate a canonical record whose full-name pointer is empty.

    Some FM24 players use a separate ID-indexed first/surname record instead
    of the canonical record's full-name string pointer. Both must agree on ID.
    """
    number = int(person_id)
    record_addresses = []
    names = set()
    for address in _record_addresses(pid, number):
        try:
            raw = read_process_bytes(pid, address, 128)
            # Marker byte 13 carries flags (0x02 and 0x40 seen); type 5 is a player too.
            if (raw[:8] == struct.pack("<II", number, number) and not raw[12] and raw[14] == 0x58 and
                    raw[15] in (0x10, 0x12, 0x18) and raw[16] in (0, 4, 5) and raw[17:20] == b"\0\0\0"):
                if _u32(pid, address + 0x3c) == 0:
                    year = struct.unpack_from("<H", raw, 0x36)[0]
                    day, birth_year = struct.unpack_from("<HH", raw, 0x38)
                    birth = dt.date(birth_year, 1, 1) + dt.timedelta(days=day - 1)
                    if 1900 <= birth_year <= year - 14 and birth.year == birth_year:
                        record_addresses.append((address, year, birth,
                                                 "staff" if raw[16] == 0 else "player"))
            if (name := _alternate_name(raw)) is not None:
                names.add(name)
        except (OSError, ValueError, UnicodeDecodeError, struct.error, OverflowError):
            continue
    if len(record_addresses) != 1 or len(names) != 1:
        raise MemoryUnavailable("Could not validate the alternate FM24 name record.")
    address, year, birth, kind = record_addresses[0]
    return address, PersonIdentity(number, names.pop(), birth, year, kind)


def _person_record(pid: int, person_id: str) -> tuple[int, PersonIdentity]:
    try:
        return read_person_record(person_id, pid)
    except MemoryUnavailable:
        return _record_with_alternate_name(pid, person_id)


def _text(pid: int, address: int, *, limit: int = 80) -> str:
    if not address:
        raise MemoryUnavailable("Missing FM24 string pointer.")
    size = _u32(pid, address)
    if not 1 <= size <= limit:
        raise MemoryUnavailable("Invalid FM24 string length.")
    raw = read_process_bytes(pid, address + 4, size + 1)
    if raw[-1] != 0:
        raise MemoryUnavailable("Unterminated FM24 string.")
    value = raw[:-1].decode("utf-8")
    if not value.isprintable():
        raise MemoryUnavailable("Invalid FM24 string content.")
    return value


def _collect_ui_chunk(data: bytes, titles: dict[str, list[str]], ages: set[int]) -> None:
    """Only count rendered UI text objects, not discarded formatted strings."""
    offset = 0
    while (offset := data.find(b"(ID:", offset)) >= 0:
        end_id = data.find(b")", offset + 4, offset + 16)
        digits = data[offset + 4:end_id] if end_id >= 0 else b""
        if 1 <= len(digits) <= 10 and digits.isdigit() and digits[0] != 48:
            person_id = digits.decode("ascii")
            beginning = max(0, offset - 190)
            title = next((candidate for candidate in _TITLE.finditer(data[beginning:end_id + 1])
                          if candidate.end() == end_id + 1 - beginning and candidate[3] == digits), None)
            title_start = beginning + title.start() if title else -1
            live_text = (title_start >= 8 and
                         data[title_start - 8:title_start - 4] == b"\x01\x00\x00\x00" and
                         struct.unpack_from("<I", data, title_start - 4)[0] == len(title[0]))
            if title and live_text:
                titles.setdefault(person_id, []).append(
                    (title[1] + b" - " + title[2]).decode("utf-8", errors="replace"))
        offset += 4
    offset = 0
    while (offset := data.find(b" years old", offset)) >= 0:
        before = data[max(0, offset - 2):offset]
        if len(before) == 2 and before.isdigit():
            ages.add(int(before))
        offset += 10


def _scan_ui(pid: int) -> tuple[dict[str, list[str]], set[int]]:
    """Collect ID-bearing titles and ages from current game UI buffers.

    The scan is only a selection hint. Each candidate ID must also resolve to
    exactly one canonical person record; ambiguous screens fail closed.
    """
    titles: dict[str, list[str]] = {}
    ages: set[int] = set()
    for _, data in MemoryScan(pid, 256):
        _collect_ui_chunk(data, titles, ages)
    return titles, ages


def _rating(raw: int) -> int:
    value = (raw + 2) // 5
    if not 1 <= raw <= 100 or not 1 <= value <= 20:
        raise MemoryUnavailable("Invalid FM24 attribute value.")
    return value


def _attributes(raw: bytes, mapping: dict[str, int]) -> dict[str, int]:
    return {name: _rating(raw[offset]) for name, offset in mapping.items()}


def _height(raw_cm: int) -> str:
    if not 140 <= raw_cm <= 220:
        raise MemoryUnavailable("Invalid FM24 height.")
    inches = round(raw_cm / 2.54)
    return f"{inches // 12}'{inches % 12}\""


def _nation(pid: int, record: int) -> str:
    nation = _u32(pid, record + 0x64)
    if not 1 <= _u32(pid, nation + 8) <= 500:
        raise MemoryUnavailable("Invalid FM24 nation object.")
    return _text(pid, _u32(pid, nation + 0x30), limit=40)


def _club(pid: int, record: int) -> str:
    contract = _u32(pid, record + 0xbc)
    if _u32(pid, contract + 8) != record - 12:
        raise MemoryUnavailable("FM24 contract points to another person.")
    club = _u32(pid, contract + 0x10)
    club_id = _u32(pid, club + 12)
    if not 1 <= club_id <= 2_000_000_000 or club_id != _u32(pid, club + 16):
        raise MemoryUnavailable("Invalid FM24 club object.")
    details = _u32(pid, club + 0x30)
    if _u32(pid, details + 12) != club_id:
        raise MemoryUnavailable("FM24 club details do not match the contract.")
    return _text(pid, _u32(pid, details + 0xc8), limit=80)


def _position(pid: int, record: int) -> str:
    ratings = read_process_bytes(pid, record - 0x7c, len(POSITIONS))
    if any(not 1 <= value <= 20 for value in ratings):
        raise MemoryUnavailable("Invalid FM24 position ratings.")
    best = max(ratings)
    if best < 15:
        raise MemoryUnavailable("No validated FM24 player position.")
    return POSITIONS[ratings.index(best)]


def read_open_profile(pid: int | None = None, *, identity_only: bool = False) -> tuple[str, dict]:
    """Read native FM24 prompt fields for the uniquely selected profile.

    Biography rich text is decoded separately after the selected person has
    been verified from the canonical record. ``identity_only`` stops after the
    verified name, age and club, which is all "Update face" needs; another
    club's player can have attributes that are not fully known.
    """
    pid = pid or find_game_pid()
    titles, ages = _scan_ui(pid)
    people = []
    for person_id, names in titles.items():
        try:
            address, identity = _person_record(pid, person_id)
        except (MemoryUnavailable, ValueError):
            continue  # Club/team IDs are also present in the title cache.
        if names:
            people.append((person_id, address, identity, names))
    if len(people) != 1:
        raise MemoryUnavailable(f"Expected one open FM24 person profile; found {len(people)}.")
    person_id, record, identity, names = people[0]
    matching_ages = ages.intersection(identity.possible_ages)
    if len(matching_ages) != 1:
        raise MemoryUnavailable("Could not verify the open FM24 person's age.")
    age = matching_ages.pop()
    nation = _nation(pid, record)
    club = _club(pid, record)
    if not any(title.endswith(" - " + club) for title in names):
        raise MemoryUnavailable("FM24 title and live contract club disagree.")
    if identity.kind == "staff":
        jobs = {title.rsplit(" - ", 1)[0].split("\x00")[-1].strip()
                for title in names if title.endswith(" - " + club)}
        if len(jobs) != 1 or not next(iter(jobs)):
            raise MemoryUnavailable("Could not verify the FM24 staff job title.")
        if identity_only:
            return person_id, {"type": "staff", "name": identity.name, "age": age, "club": club}
        return person_id, {"type": "staff", "name": identity.name,
                           "age": age, "nationality": nation,
                           "jobTitle": jobs.pop(), "club": club}
    if identity_only:
        return person_id, {"type": "player", "name": identity.name, "age": age, "club": club}
    raw = read_process_bytes(pid, record - 0x6d, 54)
    height_cm = struct.unpack("<H", read_process_bytes(pid, record - 0x136, 2))[0]
    rated_position = _position(pid, record)
    roles = {title.rsplit(" - ", 1)[0].strip() for title in names
             if title.endswith(" - " + club)}
    if len(roles) != 1:
        raise MemoryUnavailable("Could not verify the FM24 position title.")
    position = roles.pop()
    family = rated_position.split(" (")[0]
    if family not in position and not (family == "Midfielder" and "M (" in position):
        raise MemoryUnavailable("FM24 position ratings and title disagree.")
    profile = {"type": "player", "name": identity.name, "age": age,
               "nationality": nation, "position": position, "club": club,
               "height": _height(height_cm),
               "physical": _attributes(raw, PHYSICAL),
               "mental": _attributes(raw, MENTAL)}
    section = "goalkeeping" if rated_position == "Goalkeeper" else "technical"
    profile[section] = _attributes(raw, GOALKEEPING if section == "goalkeeping" else TECHNICAL)
    return person_id, profile


def squad_club(pid: int, records: list[int]) -> str:
    """The club most Squad rows are contracted to (loanees belong elsewhere)."""
    clubs = collections.Counter()
    for record in records:
        try:
            clubs[_club(pid, record)] += 1
        except (MemoryUnavailable, OSError, ValueError, UnicodeDecodeError, struct.error):
            continue
    if not clubs:
        raise MemoryUnavailable("Could not read the FM24 Squad club.")
    club, count = clubs.most_common(1)[0]
    if count * 2 <= len(records):
        raise MemoryUnavailable("Could not identify the FM24 Squad club.")
    return club


def squad_player_profile(pid: int, record: int, name: str, club: str, today: dt.date) -> dict:
    """Prompt fields for a Squad row's player, read by record without opening it.

    ``name`` is the row's display name and ``club`` the Squad's club. The
    position comes from the player's position ratings.
    """
    raw = read_process_bytes(pid, record, 0x40)
    number = struct.unpack_from("<I", raw)[0]
    if raw[:8] != struct.pack("<II", number, number) or raw[16] not in (4, 5):
        raise MemoryUnavailable("Not a validated FM24 player record.")
    day, birth_year = struct.unpack_from("<HH", raw, 0x38)
    birth = dt.date(birth_year, 1, 1) + dt.timedelta(days=day - 1)
    if birth.year != birth_year:
        raise MemoryUnavailable("Invalid FM24 birth date.")
    age = today.year - birth.year - ((today.month, today.day) < (birth.month, birth.day))
    if not 14 <= age <= 60:
        raise MemoryUnavailable("Invalid FM24 player age.")
    ratings = read_process_bytes(pid, record - 0x6d, 54)
    height_cm = struct.unpack("<H", read_process_bytes(pid, record - 0x136, 2))[0]
    position = _position(pid, record)
    profile = {"type": "player", "name": name, "age": age,
               "nationality": _nation(pid, record), "position": position, "club": club,
               "height": _height(height_cm),
               "physical": _attributes(ratings, PHYSICAL),
               "mental": _attributes(ratings, MENTAL)}
    section = "goalkeeping" if position == "Goalkeeper" else "technical"
    profile[section] = _attributes(ratings, GOALKEEPING if section == "goalkeeping" else TECHNICAL)
    return profile
