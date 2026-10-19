"""In-game squad reading and batch logic, with FM24 memory replaced by fakes."""

from __future__ import annotations

import base64
import collections
import dataclasses
import datetime as dt
import struct
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import fmface  # noqa: E402
import layout  # noqa: E402  The fixtures are records from the Windows fm.exe.
layout.LAYOUT.update(layout.WINDOWS)
import native_profile
import live_memory
import native_screen
import native_squad
import squad_batch
from live_memory import MemoryUnavailable

IMAGE = 0x140000000
PLAYER_VTABLE = next(vtable for vtable, kind in layout.WINDOWS["person_vtables"].items() if kind == "player")

TODAY = dt.date(2023, 1, 2)


def state(ids, total, visible, indexed=()):
    rows = tuple(native_squad.SquadRow(str(i), 0x1000 + i, f"Player {i}") for i in ids)
    return native_squad.SquadState(rows, total, frozenset(indexed), TODAY, visible)


class DateTests(unittest.TestCase):
    def test_most_common_date_label_is_today(self):
        dates = collections.Counter({"2 Jan 2023": 6, "1 Jan 2023": 1})
        self.assertEqual(native_squad._today(dates), TODAY)

    def test_ambiguous_or_missing_date_is_refused(self):
        for dates in (collections.Counter(), collections.Counter({"2 Jan 2023": 1}),
                      collections.Counter({"2 Jan 2023": 3, "9 Mar 2023": 2})):
            with self.assertRaises(MemoryUnavailable):
                native_squad._today(dates)


class ScreenTextTests(unittest.TestCase):
    def test_first_paragraph_keeps_link_text_only(self):
        link = b"\x01" + base64.b64encode(b"Click to view Lionel Messi's profile") + b"\x02"
        raw = b"\x01" + link + b"Lionel Messi\x01 is a  legend.\n\nLater paragraph.\x00junk"
        self.assertEqual(native_screen._first_paragraph(raw), "Lionel Messi is a legend.")
        self.assertEqual(native_screen._first_paragraph(b""), "")
        with self.assertRaises(MemoryUnavailable):
            native_screen._first_paragraph(b"bad \xff text")


class PlayingClubTests(unittest.TestCase):
    def test_a_free_agent_is_made_in_a_plain_shirt(self):
        record = 0x1000
        identity = live_memory.PersonIdentity(28004396, "Gylfi Thor Sigurdsson", dt.date(1989, 9, 8), 2023, "player")
        words = {record + layout.WINDOWS["contract"]: 0}  # no contract
        fields = {"_nation": "Icelandic", "_position": "Attacking Midfielder (Center)", "_height": "6'1\""}
        with patch.object(native_profile, "_u32", side_effect=lambda pid, address: words.get(address, 0)), \
                patch.object(native_profile, "read_process_bytes", side_effect=lambda pid, address, size: bytes(
                    [60] * size)), \
                patch.multiple(native_profile, **{name: lambda *args, value=value: value
                                                  for name, value in fields.items()}):
            with self.assertRaises(native_profile.NoContract):
                native_profile._club(1, record)
            # His header names his national team, not a club.
            profile = native_profile._profile(1, record, identity, ["Attacking Midfielder (Center) - Iceland"],
                                              33, False)
        self.assertEqual((profile["club"], profile["position"]), ("Free agent", "Attacking Midfielder (Center)"))
        self.assertIn("who is a free agent", fmface.profile_prompt(profile))
        self.assertIn("plain unbranded football shirt", fmface.profile_prompt(profile))

    TEAM, CLUB = layout.WINDOWS["team_vtable"], layout.WINDOWS["club_vtable"]

    def playing_club(self, words, classes, names, fallback="Inter Miami"):
        with patch.object(native_profile, "_u32", side_effect=lambda pid, address: words.get(address, 0)), \
                patch.object(native_profile, "_pointer_full",
                             side_effect=lambda pid, address: IMAGE + classes.get(address, 0)), \
                patch.object(native_profile, "fm24_image_base", return_value=IMAGE), \
                patch.object(native_profile, "_text", side_effect=lambda pid, address, limit=80: names[address]):
            return native_profile._playing_club(1, 0x1000, fallback)

    def test_loaned_player_uses_the_loan_club(self):
        record = 0x1000
        # A db::TEAM: its ID at +12, club details (db::CLUB, same ID, name at +0xc8) at +0x30.
        words = {record + native_profile.PLAYING_CLUB: 0x5000,
                 0x5000 + 12: 77, 0x5000 + 16: 77, 0x5000 + 0x30: 0x6000, 0x6000 + 12: 77, 0x6000 + 0xc8: 0x7000}
        classes = {0x5000: self.TEAM, 0x6000: self.CLUB}
        names = {0x7000: "Las Vegas"}
        self.assertEqual(self.playing_club(words, classes, names), "Las Vegas")
        words[record + native_profile.PLAYING_CLUB] = 0  # staff, or no current club
        self.assertEqual(self.playing_club(words, classes, names), "Inter Miami")
        words[record + native_profile.PLAYING_CLUB] = 0x5000
        for broken in ({0x5000: self.CLUB, 0x6000: self.CLUB}, {0x5000: self.TEAM, 0x6000: self.TEAM}):
            with self.assertRaises(MemoryUnavailable):
                self.playing_club(words, broken, names)

    def test_a_clubs_other_teams_are_named_by_themselves_or_their_club(self):
        record = 0x1000
        # Reading Under 21s (2026-10-19): an FM-made ID, a squad index (not
        # the ID) at +16, no name of its own, and Reading's club details.
        words = {record + native_profile.PLAYING_CLUB: 0x5000, 0x5000 + 12: 2_000_339_974, 0x5000 + 16: 18120,
                 0x5000 + 0x30: 0x6000, 0x6000 + 12: 702, 0x6000 + 0xc8: 0x7000}
        classes = {0x5000: self.TEAM, 0x6000: self.CLUB}
        names = {0x7000: "Reading", 0x7100: "Jong Sparta Rotterdam"}
        self.assertEqual(self.playing_club(words, classes, names), "Reading")
        # Jong Sparta Rotterdam: its own name at +0x18, Sparta's club details.
        words.update({0x5000 + 12: 37_060_577, 0x5000 + 16: 37_060_577, 0x5000 + 0x18: 0x7100})
        self.assertEqual(self.playing_club(words, classes, names), "Jong Sparta Rotterdam")

    def test_a_youth_team_is_named_as_its_header_names_it(self):
        identity = live_memory.PersonIdentity(2000104361, "John Clarke", dt.date(2004, 4, 24), 2023, "player")
        fields = {"_nation": "English", "_club": "Reading", "_playing_club": "Reading",
                  "_position": "Defender (Left)", "_height": "5'10\""}
        with patch.object(native_profile, "read_process_bytes", side_effect=lambda pid, address, size: bytes(
                    [60] * size)), \
                patch.multiple(native_profile, **{name: lambda *args, value=value: value
                                                  for name, value in fields.items()}):
            profile = native_profile._profile(1, 0x1000, identity, ["Defender/Wing Back (Left) - Reading Under 21s"],
                                              19, False)
            self.assertEqual(profile["club"], "Reading Under 21s")
            with self.assertRaises(MemoryUnavailable):  # another club's team
                native_profile._profile(1, 0x1000, identity, ["Defender (Left) - Readingham Under 21s"], 19, False)

class RecordCacheTests(unittest.TestCase):
    def test_record_address_is_reused_until_its_header_changes(self):
        native_profile._records.clear()
        identity = live_memory.PersonIdentity(7458500, "Lionel Messi", dt.date(1987, 6, 24), 2023, "player")
        header = {0x5000: b"H" * native_profile._RECORD_HEADER}
        with patch.object(native_profile, "read_person_record", return_value=(0x5000, identity)) as search, \
                patch.object(native_profile, "read_process_bytes",
                             side_effect=lambda pid, address, size: header[address][:size]):
            self.assertEqual(native_profile._person_record(1, "7458500"), (0x5000, identity))
            self.assertEqual(native_profile._person_record(1, "7458500"), (0x5000, identity))
            self.assertEqual(search.call_count, 1)
            header[0x5000] = b"X" * native_profile._RECORD_HEADER  # moved or replaced: search again
            native_profile._person_record(1, "7458500")
            self.assertEqual(search.call_count, 2)
            native_profile._person_record(2, "7458500")  # another game process
            self.assertEqual(search.call_count, 3)
        native_profile._records.clear()


class DateCountTests(unittest.TestCase):
    def test_counts_only_fm_date_strings(self):
        def fm_string(text):
            return struct.pack("<I", len(text)) + text + b"\0"
        data = (b"xx" + fm_string(b"2 Jan 2023") + b"yy" + fm_string(b"12 Feb 2024") +
                b"2 Jan 2023\0" +                          # no length prefix: ignored
                fm_string(b"2 Foo 2023") + b" 2023 tail")   # not a month: ignored
        dates = collections.Counter()
        native_squad._count_dates(data, dates)
        self.assertEqual(dates, {"2 Jan 2023": 1, "12 Feb 2024": 1})


class AlternateNameTests(unittest.TestCase):
    def test_decodes_any_first_name_length(self):
        # Live bytes of Tyler Hall's name record (ID twice, 09, Tyler, Hall).
        raw = (struct.pack("<II", 2000251984, 2000251984) + b"\x09" + struct.pack("<I", 5) + b"Tyler" +
               struct.pack("<I", 4) + b"Hall\x00" + b"\x00" * 30)
        self.assertEqual(native_profile._alternate_name(raw), "Tyler Hall")

    def test_accepts_a_trailing_common_name(self):
        # Live bytes of Gabriel Pec's name record: first, surname, common name.
        raw = (struct.pack("<II", 19397595, 19397595) + b"\x09" + struct.pack("<I", 7) + b"Gabriel" +
               struct.pack("<I", 13) + b"Fortes Chaves" + struct.pack("<I", 11) + b"Gabriel Pec" +
               b"\x01\x00\x02X\x10\x04" + b"\x00" * 30)
        self.assertEqual(native_profile._alternate_name(raw), "Gabriel Fortes Chaves")
        corrupt = raw.replace(b"Gabriel Pec", b"Gabriel\x11Pec")
        self.assertIsNone(native_profile._alternate_name(corrupt))

    def test_rejects_corrupt_names(self):
        base = struct.pack("<II", 7, 7) + b"\x09" + struct.pack("<I", 6) + b"Lawson"
        for tail in (struct.pack("<I", 9) + b"Sunde\x00\x00\x00\x11\x00",   # control bytes
                     struct.pack("<I", 10) + b"Sunderland",                 # no terminator
                     struct.pack("<I", 0) + b"\x00"):                       # empty surname
            self.assertIsNone(native_profile._alternate_name(base + tail + b"\xff" * 4))


class FakeMemory:
    """Sparse little-endian process memory for the row-model reader."""

    def __init__(self):
        self.bytes = {}

    def put(self, address, data):
        for offset, value in enumerate(data):
            self.bytes[address + offset] = value

    def q(self, address, *values):
        self.put(address, struct.pack(f"<{len(values)}Q", *values))

    def read(self, pid, address, size):
        try:
            return bytes(self.bytes[address + offset] for offset in range(size))
        except KeyError:
            raise OSError(14, "Bad address") from None

    def scan(self, pid, overlap):
        start, end = min(self.bytes), max(self.bytes) + 1
        data = bytes(self.bytes.get(address, 0) for address in range(start, end))
        return iter([(start, data)])


class RowModelTests(unittest.TestCase):
    TABLE, MODEL, ROWS, PERSONS = 0x10000, 0x11000, 0x12000, 0x20000

    def build(self, players, drawn, row_ids=None):
        """``players``: (person ID, handle); rows in table order, header first."""
        memory = FakeMemory()
        memory.q(self.TABLE + native_squad.TABLE_MODEL, self.MODEL)
        rows = [self.ROWS + 0x100 * index for index in range(len(players) + 1)]
        memory.q(self.MODEL + native_squad.MODEL_ROWS, 0x13000, 0x13000 + 8 * len(rows))
        memory.q(0x13000, *rows)
        ids = row_ids or [native_squad.HEADER_ROW_ID] + [0x01000000 | handle for _, handle in players]
        for row, row_id in zip(rows, ids):
            memory.q(row, row + 0x40, row + 0x50)
            memory.q(row + 0x40, native_squad.ROW_ID_KEY, row + 0x60)
            memory.put(row + 0x68, struct.pack("<I", row_id))
        people = {}
        for index, (person_id, handle) in enumerate(players):
            person = 0x30000 + 0x100 * index
            memory.q(person, IMAGE + PLAYER_VTABLE)  # a db::ACTUAL_PLAYER object
            memory.put(person + 8, struct.pack("<I", handle))
            # A validated player record: ID twice, then the 00 xx 58 10 marker and type 4.
            memory.put(person + 12, struct.pack("<II", person_id, person_id) + bytes(4) +
                       bytes([0, 0, 0x58, 0x10, 4, 0, 0, 0]))
            memory.q(self.PERSONS + 8 * handle, person)
            people[str(person_id)] = person
        state = native_squad.SquadState(
            tuple(native_squad.SquadRow(str(person_id), people[str(person_id)] + 12, f"Shown {person_id}")
                  for person_id in drawn),
            len(players), frozenset(), TODAY, (1, len(drawn)), self.TABLE)
        return memory, state

    def read(self, memory, state, names=None):
        with patch.object(native_squad, "read_process_bytes", memory.read), \
                patch.object(live_memory, "read_process_bytes", memory.read), \
                patch.object(live_memory, "fm24_image_base", return_value=IMAGE), \
                patch.object(native_squad, "MemoryScan", memory.scan), \
                patch.object(native_squad, "read_squad", return_value=state), \
                patch.object(native_squad, "_undrawn_names",
                             side_effect=lambda pid, rows: {key: f"Record {key}" for key in rows}):
            return native_squad.read_squad_rows(1)

    def test_reads_undrawn_rows_through_the_person_table(self):
        players = [(101, 7), (102, 3), (103, 9), (104, 1), (105, 5), (106, 2)]
        memory, state = self.build(players, drawn=[103, 104, 105, 106, 102])
        result = self.read(memory, state)
        self.assertEqual([row.person_id for row in result.rows], ["101", "102", "103", "104", "105", "106"])
        self.assertEqual(result.rows[0].name, "Record 101")
        self.assertEqual(result.rows[2].name, "Shown 103")
        self.assertTrue(result.complete)

    def test_refuses_a_model_that_does_not_match_the_table(self):
        players = [(101, 7), (102, 3), (103, 9), (104, 1), (105, 5), (106, 2)]
        memory, state = self.build(players, drawn=[103, 104, 105, 106, 102])
        for broken in (dataclasses.replace(state, total=7),
                       # A drawn row whose player the model does not list.
                       dataclasses.replace(state, rows=state.rows[:4] + (
                           dataclasses.replace(state.rows[4], person_id="999"),))):
            with self.assertRaises(MemoryUnavailable):
                self.read(memory, broken)
        memory, state = self.build(players, drawn=[103, 104, 105, 106, 102],
                                   row_ids=[native_squad.HEADER_ROW_ID] + [0x02000007] * 6)
        with self.assertRaisesRegex(MemoryUnavailable, "does not list people"):
            self.read(memory, state)



class Progress:
    """Stands in for FM24's progress dialog (the DLL's NativeProgress)."""
    made = []

    def __init__(self, port, token, title, text):
        self.stopped = threading.Event()
        self.shown = [(port, title, text)]
        self.result = None
        Progress.made.append(self)

    def update(self, message, percent=None):
        self.shown.append(message)

    def finish(self, text, fallback):
        self.result = text

    def close(self):
        self.shown.append("closed")


class NativeBatchTests(unittest.TestCase):
    def setUp(self):
        Progress.made = []
        self.full = state(range(1, 6), 5, (1, 5), indexed={"1", "2", "4"})
        self.notices = []
        for name, value in (("read_squad_rows", lambda *a, **k: self.full),
                            ("squad_club", lambda *a: "Club"),
                            ("shown_team", lambda *a: "Club II"),
                            ("confirm_batch", lambda missing, total, preview_only=False, native=None, skipped=0, brief=0, team="": True),
                            ("NativeProgress", Progress),
                            ("notify", lambda port, token, text, fallback: self.notices.append(text))):
            patcher = patch.object(squad_batch, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def run_batch(self, generate, profile=lambda pid, record, name, club, today: {"name": name},
                  preview=False, port=8770):
        handler = SimpleNamespace(preview_only=preview, reload_port=port, token="t" * 43)
        with patch.object(squad_batch, "squad_player_profile", side_effect=profile), \
                patch.object(squad_batch.time, "sleep"):
            squad_batch.run_native_squad_batch(handler, generate, pid=1)

    def test_generates_only_missing_players_by_id_with_progress_in_fm24(self):
        calls = []
        self.run_batch(lambda person, profile: calls.append((person, profile["name"])) or
                       {"ok": True, "playerId": person, "refreshed": True})
        self.assertEqual(calls, [("3", "Player 3"), ("5", "Player 5")])
        progress = Progress.made[0]
        self.assertEqual(progress.shown[0], (8770, "FM Face squad faces", "Generating Player 3 (1/2)"))
        self.assertIn("Loaded Player 5 (2/2)", progress.shown)
        # The progress dialog itself becomes the result message.
        self.assertEqual(progress.result, "Generated 2 squad faces.")

    def test_every_prompt_is_validated_before_any_request(self):
        calls = []

        def profile(pid, record, name, club, today):
            if name == "Player 5":
                raise MemoryUnavailable("Invalid FM24 player age.")
            return {"name": name}

        self.run_batch(lambda person, profile: calls.append(person) or {"ok": True}, profile)
        self.assertEqual(calls, [])
        self.assertIn("Invalid FM24 player age", self.notices[-1])

    def test_stop_reports_how_many_faces_were_made(self):
        calls = []

        def generate(person, profile):
            calls.append(person)
            Progress.made[0].stopped.set()  # Stop chosen during the first face
            return {"ok": True, "playerId": person, "refreshed": True}

        self.run_batch(generate)
        self.assertEqual(calls, ["3"])
        self.assertEqual(Progress.made[0].result, "Stopped after 1 of 2 squad faces.")

    def test_stops_at_first_failed_or_unreloaded_face(self):
        calls = []
        self.run_batch(lambda person, profile: calls.append(person) or
                       {"ok": True, "playerId": person, "refreshed": False, "warning": "reload failed"})
        self.assertEqual(calls, ["3"])
        self.assertIn("reload failed", Progress.made[0].result)

    def test_without_the_dll_nothing_is_generated(self):
        calls = []
        self.run_batch(lambda person, profile: calls.append(person), port=None)
        self.assertEqual(calls, [])
        self.assertIn("native DLL", self.notices[-1])


class ConfirmationTests(unittest.TestCase):
    def test_unavailable_dialog_counts_as_cancel(self):
        with patch.object(squad_batch, "show_dialog", side_effect=squad_batch.DialogUnavailable("busy")), \
                patch("builtins.print") as printed:
            self.assertFalse(squad_batch.confirm_batch(2, 5, native=(8770, "t")))
        self.assertIn("no image request was made", printed.call_args.args[0])
        with patch.object(squad_batch, "show_dialog", return_value="yes"):
            self.assertTrue(squad_batch.confirm_batch(2, 5, native=(8770, "t")))
        with patch.object(squad_batch, "show_dialog", return_value="no") as dialog:
            self.assertFalse(squad_batch.confirm_batch(2, 5, native=(8770, "t")))
        self.assertIn("2 of 5 squad players have no portrait", dialog.call_args.args[4])
        self.assertFalse(squad_batch.confirm_batch(2, 5, native=None))
        with patch.object(squad_batch, "show_dialog", return_value="no") as dialog:
            squad_batch.confirm_batch(2, 5, native=(8770, "t"), skipped=1)
        self.assertIn("1 player was left out because FM24 hasn't loaded their names", dialog.call_args.args[4])

    def test_names_the_team_and_players_with_brief_records(self):
        with patch.object(squad_batch, "show_dialog", return_value="no") as dialog:
            squad_batch.confirm_batch(2, 23, native=(8770, "t"), brief=11, team="Inter Miami II")
        text = dialog.call_args.args[4]
        self.assertIn("2 of the 12 Inter Miami II players read have no portrait", text)
        self.assertIn("11 players FM24 lists without an age or ability (Scouting Required) were left out", text)
        notices = []
        with patch.object(squad_batch, "notify", lambda port, token, text, fallback: notices.append(text)):
            self.assertFalse(squad_batch.confirm_batch(0, 23, native=(8770, "t"), brief=11, team="Inter Miami II"))
        self.assertIn("All 12 Inter Miami II players read already have portraits.", notices[0])


class RatingTests(unittest.TestCase):
    def test_lowest_raw_ratings_show_as_one(self):
        self.assertEqual([native_profile._rating(raw) for raw in (1, 2, 3, 8, 100)], [1, 1, 1, 2, 20])
        for raw in (0, 101):
            with self.assertRaises(MemoryUnavailable):
                native_profile._rating(raw)


class BriefRecordTests(unittest.TestCase):
    def player_id(self, vtable, marker=(0, 2, 0x58, 0x10), ids=(2002070373, 2002070373), brief_vtables=True):
        record = struct.pack("<II", *ids) + bytes(4) + bytes(marker) + bytes([4, 0, 0, 0])

        def read(pid, address, size):
            return struct.pack("<Q", IMAGE + vtable)[:size] if address == 0x1000 else record[:size]
        layout_patch = {} if brief_vtables else {"brief_vtables": None}
        with patch.object(native_squad, "read_process_bytes", side_effect=read), \
                patch.object(live_memory, "read_process_bytes", side_effect=read), \
                patch.object(live_memory, "fm24_image_base", return_value=IMAGE), \
                patch.dict(native_squad.LAYOUT, layout_patch):
            return native_squad._player_id(1, 0x1000)

    def test_brief_records_are_told_apart_from_unknown_ones(self):
        # "Scouting Required" rows (seen on Inter Miami II and its academy) are db::VIRTUAL_PLAYER.
        for marker in ((0, 2, 0, 0x10), (8, 2, 0x40, 0x30), (0, 2, 0x58, 0x10)):
            with self.assertRaises(native_squad.BriefRecord) as raised:
                self.player_id(next(iter(layout.WINDOWS["brief_vtables"])), marker)
            self.assertEqual(raised.exception.person_id, "2002070373")
        # A full player, whatever its flag bytes.
        for marker in ((0, 0, 0x78, 0), (0, 0, 0x60, 0), (0x80, 0, 0, 0)):
            self.assertEqual(self.player_id(PLAYER_VTABLE, marker), "2002070373")
        # Not a person record at all, or not a player's.
        for vtable, ids in ((PLAYER_VTABLE, (5, 6)), (0x5A6D858, (7, 7))):
            with self.assertRaises(MemoryUnavailable) as raised:
                self.player_id(vtable, ids=ids)
            self.assertNotIsInstance(raised.exception, native_squad.BriefRecord)
        # Without a mapped brief class (the macOS layout), the marker tells.
        with self.assertRaises(native_squad.BriefRecord):
            self.player_id(PLAYER_VTABLE, (8, 2, 0x40, 0x30), brief_vtables=False)
