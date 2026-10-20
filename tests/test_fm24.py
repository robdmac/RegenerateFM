"""FM24 companion HTTP boundaries and memory readers; no billed requests are made."""

from __future__ import annotations

import json
import ctypes
import datetime as dt
import os
import re
import struct
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
import layout  # noqa: E402  The fixtures are records from the Windows fm.exe.
layout.LAYOUT.update(layout.WINDOWS)
import companion
import process_memory
import live_memory
import native_profile
from graphics import save_versioned_portrait
from fmface import PNG_SIGNATURE


class MemoryProbeTests(unittest.TestCase):
    @unittest.skipIf(sys.platform == "darwin", "macOS reads only FM24, through FM Face's library inside it")
    def test_read_only_process_memory_access(self):
        sample = ctypes.create_string_buffer(b"FM24 probe")
        address = ctypes.addressof(sample)
        self.assertEqual(process_memory.read_process_bytes(os.getpid(), address, 10), b"FM24 probe")


PLAYER_VTABLE = next(vtable for vtable, kind in layout.WINDOWS["person_vtables"].items() if kind == "player")


class LiveMemoryTests(unittest.TestCase):
    def test_record_decodes_name_and_dates(self):
        image = 0x140000000
        player, staff = (next(v for v, k in layout.WINDOWS["person_vtables"].items() if k == kind)
                         for kind in ("player", "staff"))
        record = bytearray(0x60)  # through the name objects' pointers at +0x4c/+0x54
        struct.pack_into("<II", record, 0, 14008037, 14008037)
        record[12:16] = bytes.fromhex("00025810")
        struct.pack_into("<I", record, 16, 0)
        struct.pack_into("<BBH", record, 0x34, 2, 26, 2023)
        struct.pack_into("<HH", record, 0x38, 133, 1964)
        struct.pack_into("<I", record, 0x3c, 0x1234)
        name = "Jorge Walter Theiler".encode()
        string = bytearray(struct.pack("<I", len(name)) + name + b"\0" + bytes(75))
        ratings = bytes(15)  # staff records hold no position ratings
        memory = {"vtable": staff}

        def read(pid, address, size):
            if address == 0x1000 - 12:  # the person object's vtable pointer
                return struct.pack("<Q", image + memory["vtable"])[:size]
            blocks = {0x1000: bytes(record), 0x1000 + live_memory.POSITION_RATINGS: ratings, 0x1234: string,
                      0x2000: struct.pack("<I", 0x2100), 0x2100: struct.pack("<I", 4) + b"Toni\0",
                      0x3000: struct.pack("<I", 0x3100), 0x3100: struct.pack("<I", 8) + b"Micevski\0"}
            for start, data in blocks.items():
                if start <= address and address + size <= start + len(data):
                    return data[address - start:address - start + size]
            raise OSError(14, "Bad address")

        def parse():
            with patch.object(live_memory, "read_process_bytes", side_effect=read), \
                    patch.object(live_memory, "fm24_image_base", return_value=image), \
                    patch.dict(live_memory.LAYOUT, person_vtables=layout.WINDOWS["person_vtables"]):
                return live_memory._parse_record(123, 0x1000, 14008037)

        identity = parse()
        self.assertEqual(identity.name, "Jorge Walter Theiler")
        self.assertEqual(identity.birth_date, dt.date(1964, 5, 12))
        self.assertEqual(identity.possible_ages, (58, 59))
        self.assertEqual(identity.kind, "staff")

        # Bytes 12-15 are flags that vary from person to person (all seen live).
        for marker in ("00025010", "00027810", "04027810", "00000000", "00006000", "80000000", "0000000a"):
            record[12:16] = bytes.fromhex(marker)
            self.assertEqual(parse(), identity, marker)

        # The same bytes outside a person object (a copy, a retired person,
        # an official) are not a person.
        for other in (0x5A6EC38, 0x5A6D858, 0x5C7D4F8, 0):
            memory["vtable"] = other
            self.assertIsNone(parse())

        # Players, including reserve and youth players of types 0 and 1.
        memory["vtable"] = player
        ratings = bytes([1, 1, 15, 1, 1, 1, 1, 20, 1, 1, 1, 1, 1, 1, 1])
        for record_type in (0, 1, 4):
            struct.pack_into("<I", record, 16, record_type)
            self.assertEqual(parse().kind, "player")
        ratings = bytes(15)
        struct.pack_into("<I", record, 16, 1)
        self.assertIsNone(parse())  # a type 1 player without ratings

        # No full name (60% of players): "first surname" from the name objects.
        struct.pack_into("<I", record, 16, 4)
        struct.pack_into("<I", record, 0x3c, 0)
        struct.pack_into("<I", record, 0x4c, 0x2000)
        struct.pack_into("<I", record, 0x54, 0x3000)
        self.assertEqual(parse().name, "Toni Micevski")
        struct.pack_into("<I", record, 0x4c, 0)  # one name only (Nur'alim): the surname
        self.assertEqual(parse().name, "Micevski")
        struct.pack_into("<I", record, 0x54, 0)
        self.assertIsNone(parse())
        # A full name with a non-breaking space (Oluwagbemileke Immanuel\u00a0Ajibode).
        struct.pack_into("<I", record, 0x3c, 0x1234)
        string[4:4 + len(name) + 1] = "Immanuel\u00a0Ajibode".encode().ljust(len(name) + 1, b"\0")
        struct.pack_into("<I", string, 0, len("Immanuel\u00a0Ajibode".encode()))
        self.assertEqual(parse().name, "Immanuel Ajibode")
        # An unusable full name (Hugo Videla's pointed at "v"): the name objects.
        struct.pack_into("<I", string, 0, 1)
        struct.pack_into("<II", record, 0x4c, 0x2000, 0)
        struct.pack_into("<I", record, 0x54, 0x3000)
        self.assertEqual(parse().name, "Toni Micevski")

    def test_without_mapped_classes_the_record_marker_decides(self):
        for marker in ("00025810", "00025010", "00027810", "00425810"):
            self.assertTrue(live_memory.record_marker(bytes(12) + bytes.fromhex(marker)), marker)
        for marker in ("00020010", "08024030", "00027800", "08027810"):
            self.assertFalse(live_memory.record_marker(bytes(12) + bytes.fromhex(marker)), marker)

    def test_native_rating_offsets_match_two_live_profile_snapshots(self):
        # These bytes came from two FM24 24.4.2 game records. Expected values
        # were read independently from the corresponding in-game overviews.
        luis = bytes.fromhex("4b4b5a414b285550502d4b050a0f0f0a0a504b0f280a5a5041645a4b412d28050a0f2d50463232374b412d5a4b552d50233750645f41")
        cj = bytes.fromhex("0a0f05290a053233050f29424233333d383d3829334729292364320f33420a32373c381e4233334228283d32322d423c3232373c2e33")
        self.assertEqual(native_profile._attributes(luis, native_profile.PHYSICAL), {
            "Acceleration": 9, "Agility": 9, "Balance": 9, "Jumping Reach": 11,
            "Natural Fitness": 16, "Pace": 10, "Stamina": 10, "Strength": 14})
        self.assertEqual(native_profile._attributes(luis, native_profile.TECHNICAL)["Penalty Taking"], 16)
        self.assertEqual(native_profile._attributes(luis, native_profile.MENTAL)["Determination"], 20)
        self.assertEqual(native_profile._attributes(cj, native_profile.GOALKEEPING)["Reflexes"], 14)
        self.assertEqual(native_profile._attributes(cj, native_profile.GOALKEEPING)["Handling"], 13)
        self.assertEqual(native_profile._attributes(cj, native_profile.PHYSICAL)["Strength"], 13)

    def test_native_ui_scan_excludes_discarded_title_text(self):
        active = b"Goalkeeping Coach - Inter Miami (ID:3100693)"
        stale = b"Defender (Center) - Inter Miami (ID:2000155624)"
        data = (b"\0" * 20 + struct.pack("<IIII", 53, 0, 1, len(active)) + active + b"\0" +
                struct.pack("<IIII", 53, 0, 0, len(stale)) + stale +
                b"\0 43 years old\0 24 years old\0")
        titles, ages = {}, set()
        native_profile._collect_ui_chunk(data, titles, ages)
        self.assertEqual(titles, {"3100693": ["Goalkeeping Coach - Inter Miami"]})
        self.assertEqual(ages, {43, 24})

class NativeReloadTests(unittest.TestCase):
    def _dll(self, statuses):
        """A fake DLL reload listener answering each poll with the next status."""
        import socket
        server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        server.bind(("127.0.0.1", 0))
        self.addCleanup(server.close)
        received = []

        def serve():
            for status in statuses:
                packet, peer = server.recvfrom(256)
                received.append(packet.decode())
                serial = packet.split()[2].decode()
                if status == "wrong-serial":
                    server.sendto(b"FMFACE24R 1 done", peer)
                    continue
                server.sendto(f"FMFACE24R {serial} {status}".encode(), peer)

        threading.Thread(target=serve, daemon=True).start()
        return server.getsockname()[1], received

    def test_polls_same_serial_until_done(self):
        import native_reload
        port, received = self._dll(["queued", "wrong-serial", "queued", "done"])
        native_reload.request_native_reload(port, "t" * 43, serial=7)
        self.assertEqual(set(received), {"FMFACE24R 1 7 " + "t" * 43})
        self.assertEqual(len(received), 4)

    def test_failed_and_busy_raise(self):
        import native_reload
        for status in ("failed", "busy"):
            port, _ = self._dll(["queued", status])
            with self.assertRaises(RuntimeError):
                native_reload.request_native_reload(port, "t" * 43)

    def test_no_answer_times_out(self):
        import native_reload
        port, _ = self._dll([])
        with self.assertRaisesRegex(TimeoutError, "did not answer"):
            native_reload.request_native_reload(port, "t" * 43, timeout=1.2)

    def test_companion_reloads_through_the_dll(self):
        handler = SimpleNamespace(reload_port=8770, token="t" * 43)
        with patch.object(companion, "request_native_reload") as native:
            companion.reload_portrait(handler)
        native.assert_called_once_with(8770, "t" * 43)


class InGameProgressTests(unittest.TestCase):
    HANDLER = SimpleNamespace(reload_port=8770, token="t" * 43)

    def test_progress_is_on_the_fm_face_portrait_popup(self):
        with patch.object(companion, "set_progress") as progress, patch.object(companion, "show_dialog") as dialog:
            self.assertIsInstance(companion.start_progress(self.HANDLER, "update"), companion.NativePortraitProgress)
        progress.assert_called_once_with(8770, "t" * 43, "portrait", "Updating…")
        dialog.assert_not_called()

    def test_without_the_popup_progress_and_errors_are_in_a_dialog(self):
        def portrait_missing(port, token, target, text):
            if target == "portrait":
                raise companion.DialogUnavailable("FM24 could not set the progress text (failed).")
        with patch.object(companion, "set_progress", side_effect=portrait_missing) as progress, \
             patch.object(companion, "show_dialog") as dialog:
            shown = companion.start_progress(self.HANDLER, "generate")
            self.assertIsInstance(shown, companion.NativeDialogProgress)
            dialog.assert_called_once_with(8770, "t" * 43, "message", "FM Face", "Generating a new face…")
            shown.send("reload")
            self.assertTrue(shown.fail("OpenAI image API returned HTTP 500."))
        self.assertEqual([call.args[2:] for call in progress.call_args_list[1:]],
                         [("dialog", "Loading the new face…"), ("finish", "OpenAI image API returned HTTP 500.")])

    def test_an_error_after_the_dialog_was_closed_is_reported_by_the_caller(self):
        with patch.object(companion, "show_dialog"):
            shown = companion.NativeDialogProgress(8770, "t" * 43, "Generating a new face…")
        with patch.object(companion, "set_progress", side_effect=companion.DialogUnavailable("closed")):
            self.assertFalse(shown.fail("OpenAI image API returned HTTP 500."))
        self.assertTrue(shown.failed)


class CompanionHttpTests(unittest.TestCase):
    PLAYER = {"type": "player", "name": "Test Player", "age": 25, "club": "Test Club"}

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        handler = companion.Handler
        handler.token = "a" * 43
        handler.graphics_dir = Path(self.temp.name)
        handler.preview_only = True
        handler.nonces = {}
        handler.in_flight = False
        handler.reload_port = 8770
        native = patch.object(companion, "read_native_profile",
                              side_effect=lambda identity_only=False: ("123", dict(self.PLAYER)))
        self.native = native.start()
        self.addCleanup(native.stop)
        progress = patch.object(companion, "start_progress", return_value=None)
        progress.start()
        self.addCleanup(progress.stop)
        self.server = companion.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.addCleanup(self.server.server_close)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.server.shutdown)
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def _nonce(self):
        page = urllib.request.urlopen(self.base + "/fm24?key=" + companion.Handler.token).read().decode()
        return re.search(r'"nonce": "([^"]+)"', page).group(1)

    def _post(self, nonce, expected_id=None, mode="generate"):
        body = json.dumps({"key": companion.Handler.token, "nonce": nonce,
                           "expectedPlayerId": expected_id, "mode": mode}).encode()
        return urllib.request.Request(self.base + "/fm24/generate", data=body,
                                      headers={"Content-Type": "application/json"}, method="POST")

    def test_preview_does_not_generate_and_nonce_cannot_be_reused(self):
        nonce = self._nonce()
        with patch.object(companion, "profile_prompt", return_value="test prompt"), \
             patch.object(companion, "generate_png") as generate:
            response = json.load(urllib.request.urlopen(self._post(nonce)))
            self.assertEqual((response["preview"], response["playerId"], response["prompt"]),
                             (True, "123", "test prompt"))
            generate.assert_not_called()
            with self.assertRaises(urllib.error.HTTPError) as repeated:
                urllib.request.urlopen(self._post(nonce))
            self.assertEqual(repeated.exception.code, 400)
        self.assertEqual(self.native.call_args.kwargs, {"identity_only": False})

    def test_wrong_host_and_wrong_token_are_rejected(self):
        request = urllib.request.Request(self.base + "/fm24?key=" + companion.Handler.token,
                                         headers={"Host": "unrelated.example"})
        with self.assertRaises(urllib.error.HTTPError) as wrong_host:
            urllib.request.urlopen(request)
        self.assertEqual(wrong_host.exception.code, 403)
        with self.assertRaises(urllib.error.HTTPError) as wrong_token:
            urllib.request.urlopen(self.base + "/fm24?key=wrong")
        self.assertEqual(wrong_token.exception.code, 404)

    def test_generation_saves_and_reloads_in_game(self):
        companion.Handler.preview_only = False
        with patch.object(companion, "profile_prompt", return_value="test prompt"), \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png", return_value=PNG_SIGNATURE + b"test") as generate, \
             patch.object(companion, "request_native_reload") as reload:
            response = json.load(urllib.request.urlopen(self._post(self._nonce())))
            self.assertTrue(response["refreshed"])
            portraits = list(Path(self.temp.name).glob("123-*.png"))
            self.assertEqual([path.read_bytes() for path in portraits], [PNG_SIGNATURE + b"test"])
            generate.assert_called_once_with({"OPENAI_API_KEY": "test-only"}, "test prompt")
            reload.assert_called_once_with(8770, "a" * 43)

    def test_failed_reload_keeps_the_portrait_and_warns(self):
        companion.Handler.preview_only = False
        with patch.object(companion, "profile_prompt", return_value="test prompt"), \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png", return_value=PNG_SIGNATURE + b"test"), \
             patch.object(companion, "request_native_reload", side_effect=TimeoutError("no reply")):
            response = json.load(urllib.request.urlopen(self._post(self._nonce())))
        self.assertEqual((response["ok"], response["refreshed"]), (True, False))
        self.assertIn("Reload Skin", response["warning"])

    def test_expected_id_is_checked_before_billing(self):
        companion.Handler.preview_only = False
        with patch.object(companion, "generate_png") as generate:
            with self.assertRaises(urllib.error.HTTPError) as rejected:
                urllib.request.urlopen(self._post(self._nonce(), expected_id="456"))
            self.assertEqual(rejected.exception.code, 422)
            self.assertIn("no image request was made", rejected.exception.read().decode())
            generate.assert_not_called()

    def _update(self, *, found, preview):
        companion.Handler.preview_only = preview
        self.PLAYER = {"name": "Lionel Messi", "age": 38, "club": "Inter Miami"}
        with patch.object(companion, "find_existing_portrait", return_value=found) as lookup, \
             patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "game_dir_from_process", return_value=Path("/games/fm24")), \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png") as generate, \
             patch.object(companion, "edit_png", return_value=PNG_SIGNATURE + b"updated") as edit, \
             patch.object(companion, "request_native_reload"):
            try:
                result = json.load(urllib.request.urlopen(self._post(self._nonce(), "123", "update")))
            except urllib.error.HTTPError as error:
                result = {"status": error.code, **json.load(error)}
            generate.assert_not_called()
            lookup.assert_called_once_with("123", Path(self.temp.name), Path("/games/fm24"))
        self.assertEqual(self.native.call_args.kwargs, {"identity_only": True})
        return result, edit

    def test_update_preview_names_the_existing_portrait(self):
        result, edit = self._update(found=(PNG_SIGNATURE + b"photo", "FM24 bundled messi.jpg"), preview=True)
        self.assertTrue(result["preview"])
        self.assertEqual(result["reference"], "FM24 bundled messi.jpg")
        self.assertIn("updated version of the supplied picture of Lionel Messi", result["prompt"])
        edit.assert_not_called()

    def test_update_sends_existing_portrait_and_saves_result(self):
        result, edit = self._update(found=(PNG_SIGNATURE + b"photo", "FM24 bundled messi.jpg"), preview=False)
        self.assertTrue(result["ok"])
        self.assertEqual(edit.call_args.args[2], PNG_SIGNATURE + b"photo")
        self.assertIn("at age 38", edit.call_args.args[1])
        saved = list(Path(self.temp.name).glob("123-*.png"))
        self.assertEqual([path.read_bytes() for path in saved], [PNG_SIGNATURE + b"updated"])

    def test_update_without_a_face_is_refused_before_any_request(self):
        result, edit = self._update(found=None, preview=False)
        self.assertEqual(result["status"], 422)
        self.assertIn("no face to update", result["error"])
        edit.assert_not_called()


class NativeProfileReadTests(unittest.TestCase):
    PROFILE = {"type": "player", "name": "Lionel Messi", "age": 35, "club": "Inter Miami"}

    def read(self, screens, reread=None, identity_only=False):
        screens = iter(screens)
        reread = reread or (lambda pid, person_id, title, age, identity_only=False: (person_id, dict(self.PROFILE)))
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile",
                          side_effect=lambda *a, **k: ("7458500", dict(self.PROFILE))) as first, \
             patch.object(companion, "read_open_screen", side_effect=lambda pid: next(screens)), \
             patch.object(companion, "reread_profile", side_effect=reread) as again:
            result = companion.read_native_profile(identity_only=identity_only)
        return result, first, again

    def test_native_profile_takes_biography_from_the_open_screen(self):
        from native_screen import OpenScreen
        screen = OpenScreen("7458500", "Barcelona legend Lionel Messi is truly one of world soccer's superstars.",
                            "Attacking Midfielder - Inter Miami")
        (person_id, result), _, again = self.read([screen, screen])
        self.assertEqual(result["biographyHeadline"], screen.biography)
        # The check read uses the header title and the first age: no second memory scan.
        self.assertEqual(again.call_args.args, (1, "7458500", "Attacking Midfielder - Inter Miami", 35))
        with self.assertRaisesRegex(ValueError, "shows 2000045865"):
            self.read([OpenScreen("2000045865", ""), OpenScreen("2000045865", "")])
        with self.assertRaisesRegex(ValueError, "changed"):
            self.read([screen, OpenScreen("2000045865", "", "Defender - Inter Miami")])
        with self.assertRaisesRegex(ValueError, "changed"):
            self.read([screen, screen], reread=lambda *a, **k: ("7458500", dict(self.PROFILE, club="Las Vegas")))
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile", side_effect=live_memory.MemoryUnavailable("gone")):
            with self.assertRaisesRegex(ValueError, "from game memory"):
                companion.read_native_profile()

    def test_update_reads_identity_only(self):
        from native_screen import OpenScreen
        screen = OpenScreen("7458500", "A biography.", "Attacking Midfielder - Inter Miami")
        (person_id, result), first, again = self.read([screen, screen], identity_only=True)
        self.assertEqual((person_id, result), ("7458500", self.PROFILE))
        # The profile read takes the person and title from the screen, not "(ID:n)" titles.
        self.assertEqual(first.call_args.kwargs, {"identity_only": True, "screen": screen})
        self.assertEqual(again.call_args.kwargs, {"identity_only": True})


class PersonReferenceTests(unittest.TestCase):
    def test_only_person_references_have_a_handle(self):
        import person_handles
        self.assertEqual(person_handles.person_handle(0x01000C0E), 0xC0E)
        for reference in (0x18000CB8, 0x01000000, 0x017FFFFF, 0):
            with self.assertRaises(ValueError):
                person_handles.person_handle(reference)

    def test_person_table_lookup_reads_the_handle_slot_and_checks_the_record(self):
        import person_handles
        image, database, table, person = 0x100000000, 0x7000, 0x9000, 0xA000
        memory = {
            image + 0x50: struct.pack("<Q", database),
            database + 0x18: struct.pack("<QQ", table, table + 8 * 0x20),
            table + 8 * 0x0E: struct.pack("<Q", person),
            # A player object: its vtable, the handle, then the record (the ID twice).
            person: struct.pack("<QIII", image + PLAYER_VTABLE, 0x0E, 4000829, 4000829) + bytes(8),
        }

        def read(pid, address, size):
            for start, data in memory.items():
                if start <= address and address + size <= start + len(data):
                    return data[address - start:address - start + size]
            return bytes(size)
        with patch.dict(person_handles.LAYOUT, {"person_table": (0x50, 0x18), "browser_reference": 0x878}), \
                patch.object(person_handles, "read_process_bytes", side_effect=read), \
                patch.object(live_memory, "read_process_bytes", side_effect=read), \
                patch.object(person_handles, "fm24_image_base", return_value=image), \
                patch.object(live_memory, "fm24_image_base", return_value=image), \
                patch.object(person_handles, "_scan", side_effect=AssertionError("no memory pass")):
            self.assertEqual(person_handles.person_for_reference(1, 0x0100000E, wait=False), "4000829")
            # Outside the table, or an empty slot: the lookup falls back to the index.
            for reference in (0x01000040, 0x0100000F):
                self.assertIsNone(person_handles._from_table(1, reference & 0x7FFFFF))

    def test_the_handle_index_finds_person_objects_by_class(self):
        import person_handles
        image, base = 0x140000000, 0x30000000
        heap = bytearray(0x400)
        objects = ((0x000, PLAYER_VTABLE, 0x0E, 4000829), (0x100, 0x5A6D858, 0x0F, 77),  # an official
                   (0x200, PLAYER_VTABLE, 0x10, 0), (0x304, PLAYER_VTABLE, 0x11, 5))   # empty, misaligned
        for offset, vtable, handle, person in objects:
            struct.pack_into("<QIII", heap, offset, image + vtable, handle, person, person)
        with patch.dict(person_handles.LAYOUT, person_vtables=layout.WINDOWS["person_vtables"]), \
                patch.object(person_handles, "fm24_image_base", return_value=image), \
                patch.object(person_handles, "MemoryScan", return_value=[(base, bytes(heap))]):
            self.assertEqual(person_handles._scan(1), {0x0E: base + 12})

    def test_person_table_is_found_in_another_database_slot(self):
        import person_handles
        image, database, decoy, table, other, person = 0x100000000, 0x7000, 0x8000, 0x9000, 0xB000, 0xA000
        memory = {
            image + 0x50: struct.pack("<Q", database),
            # The layout's slot now holds another kind of object, also indexed by handle.
            database + 0x18: struct.pack("<QQ", decoy, decoy + 8 * 0x20),
            decoy + 8 * 0x0E: struct.pack("<Q", other),
            # An official: the handle and an ID twice, but not a person FM Face makes faces for.
            other: struct.pack("<QIII", image + 0x5A6D858, 0x0E, 1234, 1234) + bytes(8),
            database + 0x98: struct.pack("<QQ", table, table + 8 * 0x20),
            table + 8 * 0x0E: struct.pack("<Q", person),
            person: struct.pack("<QIII", image + PLAYER_VTABLE, 0x0E, 4000829, 4000829) + bytes(8),
        }

        def read(pid, address, size):
            for start, data in memory.items():
                if start <= address and address + size <= start + len(data):
                    return data[address - start:address - start + size]
            return bytes(size)
        with patch.dict(person_handles.LAYOUT, {"person_table": (0x50, 0x18), "browser_reference": 0x878}), \
                patch.dict(person_handles._table_slots, clear=True), \
                patch.object(person_handles, "read_process_bytes", side_effect=read), \
                patch.object(live_memory, "read_process_bytes", side_effect=read), \
                patch.object(person_handles, "fm24_image_base", return_value=image), \
                patch.object(live_memory, "fm24_image_base", return_value=image):
            self.assertEqual(person_handles._from_table(1, 0x0E), "4000829")
            self.assertEqual(person_handles._table_slots[1], 0x98)

    def test_screen_reference_prefers_the_sidebar_which_survives_a_skin_reload(self):
        import person_handles
        image, sidebar, browser = 0x100000000, 0x5000, 0x6000
        side, other = int.from_bytes(b"side", "big"), int.from_bytes(b"NULL", "big")
        memory = {sidebar: struct.pack("<Q", image + 0x40), sidebar + 0x278: struct.pack("<I", 0x01000ED8),
                  browser: struct.pack("<Q", image + 0x80), browser + 0x878: struct.pack("<I", 0x78008020)}

        def read(pid, address, size):
            return memory.get(address, bytes(size))[:size]
        layout = {"sidebar_vtable": 0x40, "sidebar_reference": 0x278,
                  "object_browser_vtable": 0x80, "browser_reference": 0x878}
        with patch.dict(person_handles.LAYOUT, layout), \
                patch.object(person_handles, "read_process_bytes", side_effect=read), \
                patch.object(person_handles, "fm24_image_base", return_value=image):
            # After a reload the browser holds something else; the sidebar still has the person.
            self.assertEqual(person_handles.screen_reference(1, 0, {side: [sidebar], other: [browser]}), 0x01000ED8)
            # A sidebar showing a club falls back to the browser, which is checked as before.
            memory[sidebar + 0x278] = struct.pack("<I", 0x18000CB8)
            memory[browser + 0x878] = struct.pack("<I", 0x01001BAD)
            self.assertEqual(person_handles.screen_reference(1, 0, {side: [sidebar], other: [browser]}), 0x01001BAD)

    def test_a_click_waits_for_the_running_pass_instead_of_starting_another(self):
        import person_handles
        started, release = threading.Event(), threading.Event()
        calls = []

        def scan(pid):
            calls.append(pid)
            started.set()
            release.wait(2)
            return {}
        with patch.object(person_handles, "_scan", side_effect=scan):
            first = threading.Thread(target=person_handles.build, args=(99,))
            first.start()
            self.assertTrue(started.wait(2))
            second = threading.Thread(target=person_handles.build, args=(99,))
            second.start()
            release.set()
            first.join(2)
            second.join(2)
        self.assertEqual(calls, [99])


class GraphicsTests(unittest.TestCase):
    def test_regeneration_uses_new_graphics_mapping(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            first, older = save_versioned_portrait(directory, "123", PNG_SIGNATURE + b"first")
            second, previous = save_versioned_portrait(directory, "123", PNG_SIGNATURE + b"second")
            self.assertNotEqual(first, second)
            self.assertEqual(older, [])
            self.assertEqual(previous, [first])
            config = (directory / "config.xml").read_text()
            self.assertIn(second.stem, config)
            self.assertNotIn(first.stem, config)


if __name__ == "__main__":
    unittest.main()
