"""In-game squad reading and batch logic, with FM24 memory replaced by fakes."""

from __future__ import annotations

import collections
import datetime as dt
import struct
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
import native_profile
import native_squad
import squad_batch
from live_memory import MemoryUnavailable

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


class AlternateNameTests(unittest.TestCase):
    def test_decodes_any_first_name_length(self):
        # Live bytes of Tyler Hall's name record (ID twice, 09, Tyler, Hall).
        raw = (struct.pack("<II", 2000251984, 2000251984) + b"\x09" + struct.pack("<I", 5) + b"Tyler" +
               struct.pack("<I", 4) + b"Hall\x00" + b"\x00" * 30)
        self.assertEqual(native_profile._alternate_name(raw), "Tyler Hall")

    def test_rejects_corrupt_names(self):
        base = struct.pack("<II", 7, 7) + b"\x09" + struct.pack("<I", 6) + b"Lawson"
        for tail in (struct.pack("<I", 9) + b"Sunde\x00\x00\x00\x11\x00",   # control bytes
                     struct.pack("<I", 10) + b"Sunderland",                 # no terminator
                     struct.pack("<I", 0) + b"\x00"):                       # empty surname
            self.assertIsNone(native_profile._alternate_name(base + tail + b"\xff" * 4))


class FullSquadTests(unittest.TestCase):
    def run_reads(self, reads):
        scrolls = []
        reads = iter(reads)
        with patch.object(native_squad, "read_squad", side_effect=lambda *a, **k: next(reads)), \
                patch.object(native_squad.time, "sleep"):
            result = native_squad.read_full_squad(scrolls.append, pid=1)
        return result, scrolls

    def test_scrolls_up_then_down_until_every_row_is_read(self):
        result, scrolls = self.run_reads([
            state(range(4, 8), 9, (4, 7)),      # opened mid-table
            state(range(1, 5), 9, (1, 4)),      # up: top reached
            state(range(4, 8), 9, (4, 7)),      # down
            state(range(6, 10), 9, (6, 9)),     # down: bottom reached
        ])
        self.assertEqual(sorted(int(row.person_id) for row in result.rows), list(range(1, 10)))
        self.assertEqual(scrolls, [False, True, True])

    def test_incomplete_or_changing_table_is_refused(self):
        with self.assertRaisesRegex(MemoryUnavailable, "Read 4 of 9"):
            self.run_reads([state(range(1, 5), 9, (1, 4))] + [state(range(1, 5), 9, (1, 4))] * 20)
        with self.assertRaisesRegex(MemoryUnavailable, "changed"):
            self.run_reads([state(range(4, 8), 9, (4, 7)), state(range(1, 5), 10, (1, 4))])


class NativeBatchTests(unittest.TestCase):
    def setUp(self):
        self.full = state(range(1, 6), 5, (1, 5), indexed={"1", "2", "4"})
        self.dialogs = []
        for name, value in (("read_full_squad", lambda *a, **k: self.full),
                            ("squad_club", lambda *a: "Club"),
                            ("confirm_batch", lambda missing, total, preview_only=False: True),
                            ("SquadProgress", lambda *a: SimpleNamespace(stopped=threading.Event(),
                                                                         update=lambda *a: None, close=lambda: None)),
                            ("subprocess", SimpleNamespace(run=lambda args, **k: self.dialogs.append(args[-1])))):
            patcher = patch.object(squad_batch, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def run_batch(self, generate, profile=lambda pid, record, name, club, today: {"name": name}, preview=False):
        with patch.object(squad_batch, "squad_player_profile", side_effect=profile), \
                patch.object(squad_batch.time, "sleep"):
            squad_batch.run_native_squad_batch("0x1", SimpleNamespace(preview_only=preview), generate, pid=1)

    def test_generates_only_missing_players_by_id(self):
        calls = []
        self.run_batch(lambda person, profile: calls.append((person, profile["name"])) or
                       {"ok": True, "playerId": person, "refreshed": True})
        self.assertEqual(calls, [("3", "Player 3"), ("5", "Player 5")])
        self.assertIn("Generated 2 squad faces.", self.dialogs[-1])

    def test_every_prompt_is_validated_before_any_request(self):
        calls = []

        def profile(pid, record, name, club, today):
            if name == "Player 5":
                raise MemoryUnavailable("Invalid FM24 player age.")
            return {"name": name}

        self.run_batch(lambda person, profile: calls.append(person) or {"ok": True}, profile)
        self.assertEqual(calls, [])
        self.assertIn("Invalid FM24 player age", self.dialogs[-1])

    def test_stops_at_first_failed_or_unreloaded_face(self):
        calls = []
        self.run_batch(lambda person, profile: calls.append(person) or
                       {"ok": True, "playerId": person, "refreshed": False, "warning": "reload failed"})
        self.assertEqual(calls, ["3"])
        self.assertIn("reload failed", self.dialogs[-1])
