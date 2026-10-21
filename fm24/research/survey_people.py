#!/usr/bin/env python3
"""Read-only survey of every person in a running FM24 save (Windows, Proton or macOS).

For each entry in FM24's people vector it names the object's class from its
RTTI (MSVC's on fm.exe, the Itanium ABI's on the Mac build), then runs every player and staff member through the companion's
record parser (live_memory._parse_record) and the readers its profile
builder uses (nation, contract club, playing club, position). It also checks
the handle index (person_handles._scan) against the vector, and finds a sample
of people by ID with the memory search (live_memory.read_person_record).

    python fm24/research/survey_people.py [--sample 300] [--json out.json]

FM24 must be running with a save loaded. Nothing is written to the game.
"""

from __future__ import annotations

import argparse
import collections
import json
import random
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import live_memory  # noqa: E402
import native_profile  # noqa: E402
import person_handles  # noqa: E402
from layout import LAYOUT  # noqa: E402
from process_memory import MemoryScan, fm24_image_base, read_process_bytes  # noqa: E402

FACE_CLASSES = {".?AVACTUAL_PLAYER@db@@": "player",
                ".?AVACTUAL_NON_PLAYER@db@@": "staff",
                ".?AVACTUAL_PLAYER_AND_NON_PLAYER@db@@": "staff",
                # The Mac build's Itanium names.
                "N2db13ACTUAL_PLAYERE": "player",
                "N2db17ACTUAL_NON_PLAYERE": "staff",
                "N2db28ACTUAL_PLAYER_AND_NON_PLAYERE": "staff"}


def u64(pid: int, address: int) -> int:
    return struct.unpack("<Q", read_process_bytes(pid, address, 8))[0]


def rtti_name(pid: int, image: int, vtable: int, cache: dict[int, str]) -> str:
    """The class name of an object with this vtable, from its MSVC complete
    object locator, or on macOS its Itanium type_info (the vtable's -8 entry,
    whose +8 is the mangled name; arm64 sets the top bit of a name it doesn't
    guarantee unique)."""
    if vtable in cache:
        return cache[vtable]
    if sys.platform == "darwin":
        try:
            name = u64(pid, u64(pid, vtable - 8) + 8) & 0x7FFFFFFFFFFFFFFF
            cache[vtable] = read_process_bytes(pid, name, 96).split(b"\0", 1)[0].decode("ascii")
        except (OSError, ValueError, UnicodeDecodeError, struct.error):
            cache[vtable] = "?unreadable"
        return cache[vtable]
    try:
        locator = u64(pid, vtable - 8)
        signature, _, _, descriptor = struct.unpack("<IIII", read_process_bytes(pid, locator, 16))
        name = read_process_bytes(pid, image + descriptor + 16, 96).split(b"\0", 1)[0].decode("ascii")
        cache[vtable] = name if signature == 1 else f"?sig{signature}"
    except (OSError, UnicodeDecodeError, struct.error):
        cache[vtable] = "?unreadable"
    return cache[vtable]


def people_vector(pid: int, sample_handle: int) -> tuple[int, int]:
    """{begin, end} of the database vector that holds people (found as the companion does)."""
    if person_handles._from_table(pid, sample_handle) is None:
        raise SystemExit("The people vector was not found from the database global.")
    pointer, hint = LAYOUT["person_table"]
    database = u64(pid, fm24_image_base(pid) + pointer)
    slot = person_handles._table_slots.get(pid, hint)
    begin, end = struct.unpack("<QQ", read_process_bytes(pid, database + slot, 16))
    return begin, end


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--sample", type=int, default=300, help="people looked up by ID with the memory search")
    parser.add_argument("--json", type=Path, help="write the full results here")
    args = parser.parse_args()

    pid = live_memory.find_game_pid()
    image = fm24_image_base(pid)
    started = time.time()
    index = person_handles._scan(pid)
    index_seconds = time.time() - started
    print(f"Handle index: {len(index)} person objects found by vtable in {index_seconds:.1f} s")

    begin, end = people_vector(pid, next(iter(index)))
    count = (end - begin) // 8
    raw = b"".join(read_process_bytes(pid, begin + offset, min(4096, count * 8 - offset))
                   for offset in range(0, count * 8, 4096))
    entries = struct.unpack(f"<{count}Q", raw)
    print(f"People vector: {count} entries")

    classes: collections.Counter[str] = collections.Counter()
    class_vtables: dict[str, set[int]] = collections.defaultdict(set)
    linked: collections.Counter[str] = collections.Counter()   # classes of nations, contract clubs, playing clubs
    cache: dict[int, str] = {}
    results: collections.Counter[str] = collections.Counter()
    failures: dict[str, list[str]] = collections.defaultdict(list)
    index_mismatch = []
    vector_people: dict[int, int] = {}
    kinds: collections.Counter[str] = collections.Counter()
    no_full_name = free_agents = 0
    for handle, obj in enumerate(entries):
        if not obj:
            classes["(empty)"] += 1
            continue
        vtable = u64(pid, obj)
        name = rtti_name(pid, image, vtable, cache)
        classes[name] += 1
        class_vtables[name].add(vtable - image)
        if name not in FACE_CLASSES:
            continue
        record = obj + 12
        vector_people[handle] = record
        _linked_classes(pid, image, record, cache, linked, class_vtables)
        if index.get(handle) != record:
            index_mismatch.append(handle)
        try:
            number = struct.unpack("<I", read_process_bytes(pid, record, 4))[0]
            identity = live_memory._parse_record(pid, record, number)
            if identity is None:
                raise live_memory.MemoryUnavailable("record parser refused it")
            if live_memory.read_pointer(pid, record + LAYOUT["record_name"]) == 0:
                no_full_name += 1
            nation = native_profile._nation(pid, record)
            try:
                club = native_profile._club(pid, record)
            except native_profile.NoContract:
                club = None
                free_agents += 1
            if identity.kind == "player":
                native_profile._playing_club(pid, record, club or native_profile.FREE_AGENT)
                native_profile._position(pid, record)
            if not nation:
                raise live_memory.MemoryUnavailable("no nation")
            kinds[f"{FACE_CLASSES[name]}->{identity.kind}"] += 1
            results["read"] += 1
        except Exception as exc:  # noqa: BLE001 - a survey records every failure
            key = f"{type(exc).__name__}: {exc}"
            results["failed"] += 1
            if len(failures[key]) < 8:
                failures[key].append(f"handle {handle} record {record:#x}")

    print("\nClasses in the people vector (vtable from the image header):")
    for name, number in classes.most_common():
        print(f"  {number:7d}  {name}  {', '.join(hex(v) for v in sorted(class_vtables[name]))}")
    print("\nClasses a person's record points to:")
    for name, number in linked.most_common():
        print(f"  {number:7d}  {name}")
    for name in sorted({key.split(" ", 1)[1] for key in linked}):
        print(f"  {name}: {', '.join(hex(v) for v in sorted(class_vtables[name]))}")
    print(f"\nPlayers and staff: {sum(results.values())}; read in full: {results['read']}; failed: {results['failed']}")
    print(f"  kinds (class -> parsed kind): {dict(kinds)}")
    print(f"  no full-name string (read from name objects): {no_full_name}")
    print(f"  free agents (no contract): {free_agents}")
    for key, where in sorted(failures.items(), key=lambda item: -len(item[1])):
        print(f"  FAILED {key}: {', '.join(where)}")
    extra = set(index) - set(vector_people)
    print(f"\nHandle index vs vector: {len(vector_people) - len(index_mismatch)} of {len(vector_people)} agree; "
          f"{len(index_mismatch)} differ; {len(extra)} indexed handles not people in the vector")

    # The companion's search by ID (live_memory.read_person_record), for the
    # whole sample in one pass: every aligned {id}{id} pair the parser accepts.
    rng = random.Random(24)
    sample = rng.sample(sorted(vector_people), min(args.sample, len(vector_people)))
    wanted = {struct.unpack("<I", read_process_bytes(pid, vector_people[handle], 4))[0]: vector_people[handle]
              for handle in sample}
    started = time.time()
    accepted: dict[int, list[int]] = collections.defaultdict(list)
    for base, data in MemoryScan(pid, 7):
        for number in wanted:
            needle = struct.pack("<II", number, number)
            offset = data.find(needle)
            while offset >= 0:
                if (base + offset) % 4 == 0 and live_memory._parse_record(pid, base + offset, number):
                    accepted[number].append(base + offset)
                offset = data.find(needle, offset + 1)
    found = sum(1 for number, record in wanted.items() if accepted.get(number) == [record])
    wrong = sum(1 for number, record in wanted.items() if accepted.get(number) and accepted[number] != [record])
    missing = sum(1 for number in wanted if not accepted.get(number))
    print(f"Memory search by ID, {len(sample)} people: {found} found exactly their own record, "
          f"{wrong} found another or more than one, {missing} none ({time.time() - started:.0f} s)")

    if args.json:
        args.json.write_text(json.dumps({
            "classes": classes, "results": results, "kinds": kinds, "failures": failures,
            "no_full_name": no_full_name, "free_agents": free_agents,
            "index_mismatch": index_mismatch[:200], "extra_index": sorted(extra)[:200],
            "id_search": {"sample": len(sample), "found": found, "wrong": wrong, "missing": missing},
        }, indent=2))


def _linked_classes(pid: int, image: int, record: int, cache: dict[int, str],
                    linked: collections.Counter, class_vtables: dict[str, set[int]]) -> None:
    """Count the classes of the objects the profile readers follow from a
    record: its nation, its contract's club and its playing club."""
    def note(what: str, address: int) -> None:
        if not address:
            return
        try:
            vtable = u64(pid, address)
        except (OSError, ValueError, struct.error):
            linked[f"{what} ?unreadable"] += 1
            return
        name = rtti_name(pid, image, vtable, cache)
        linked[f"{what} {name}"] += 1
        class_vtables[name].add(vtable - image)

    try:
        note("nation", native_profile._pointer(pid, record + LAYOUT["nation"]))
        note("playing", native_profile._pointer(pid, record + LAYOUT["playing_club"]))
        contract = native_profile._pointer(pid, record + LAYOUT["contract"])
        if contract:
            club = native_profile._pointer(pid, contract + 0x10)
            note("contract", club)
            if club:
                note("details", native_profile._pointer(pid, club + 0x30))
    except (OSError, ValueError, struct.error):
        linked["?unreadable"] += 1


if __name__ == "__main__":
    main()
