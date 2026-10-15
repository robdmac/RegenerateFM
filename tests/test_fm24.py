"""FM24 companion HTTP boundaries; no OCR models or billed requests are used."""

from __future__ import annotations

import json
import ctypes
import base64
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
import companion
import process_memory
import live_memory
import native_profile
import native_portrait
from graphics import save_versioned_portrait
from squad_batch import _current_row, _overlap, _portrait_state_ready, _return_to_squad, _title_image, portrait_state
from fmface import PNG_SIGNATURE
from PIL import Image


class MemoryProbeTests(unittest.TestCase):
    def test_read_only_process_memory_access(self):
        sample = ctypes.create_string_buffer(b"FM24 probe")
        address = ctypes.addressof(sample)
        self.assertEqual(process_memory.read_process_bytes(os.getpid(), address, 10), b"FM24 probe")


class LiveMemoryTests(unittest.TestCase):
    def test_record_decodes_name_and_dates(self):
        record = bytearray(64)
        struct.pack_into("<II", record, 0, 14008037, 14008037)
        record[12:16] = live_memory._MARKERS[0]
        struct.pack_into("<I", record, 16, 0)
        struct.pack_into("<BBH", record, 0x34, 2, 26, 2023)
        struct.pack_into("<HH", record, 0x38, 133, 1964)
        struct.pack_into("<I", record, 0x3c, 0x1234)
        name = "Jorge Walter Theiler".encode()
        string = struct.pack("<I", len(name)) + name + b"\0" + bytes(75)
        with patch.object(live_memory, "read_process_bytes", side_effect=[bytes(record), string]):
            identity = live_memory._parse_record(123, 0x1000, 14008037)
        self.assertEqual(identity.name, "Jorge Walter Theiler")
        self.assertEqual(identity.birth_date, dt.date(1964, 5, 12))
        self.assertEqual(identity.possible_ages, (58, 59))
        self.assertEqual(identity.kind, "staff")

        for marker in live_memory._MARKERS[1:]:
            record[12:16] = marker
            with patch.object(live_memory, "read_process_bytes", side_effect=[bytes(record), string]):
                self.assertEqual(live_memory._parse_record(123, 0x1000, 14008037), identity)

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

    def test_profile_enrichment_rejects_wrong_person(self):
        identity = live_memory.PersonIdentity(14008037, "Jorge Walter Theiler",
                                              dt.date(1964, 5, 12), 2023, "staff")
        profile = {"type": "staff", "name": "Jorge Theiler", "age": 58}
        with patch.object(live_memory, "read_identity", return_value=identity):
            self.assertEqual(live_memory.enrich_profile("14008037", profile)["name"],
                             "Jorge Walter Theiler")
            with self.assertRaisesRegex(ValueError, "disagree"):
                live_memory.enrich_profile("14008037", {**profile, "name": "Other Coach"})
            with self.assertRaisesRegex(ValueError, "disagree"):
                live_memory.enrich_profile("14008037", {**profile, "age": 60})

    def test_profile_enrichment_accepts_hidden_trailing_surname(self):
        identity = live_memory.PersonIdentity(14253394, "Federico Redondo Solari",
                                              dt.date(2003, 1, 18), 2023, "player")
        profile = {"type": "player", "name": "Federico Redondo", "age": 19}
        with patch.object(live_memory, "read_identity", return_value=identity):
            self.assertEqual(live_memory.enrich_profile("14253394", profile)["name"],
                             "Federico Redondo Solari")
            with self.assertRaisesRegex(ValueError, "disagree"):
                live_memory.enrich_profile("14253394", {**profile, "name": "Federico Gomez"})

    def test_profile_enrichment_accepts_close_given_name_with_matching_surname(self):
        identity = live_memory.PersonIdentity(20049212, "Shander Antonio Borgelin",
                                              dt.date(2001, 10, 19), 2023, "player")
        profile = {"type": "player", "name": "Shanyder Borgelin", "age": 21}
        with patch.object(live_memory, "read_identity", return_value=identity):
            self.assertEqual(live_memory.enrich_profile("20049212", profile)["name"],
                             "Shander Antonio Borgelin")
            with self.assertRaisesRegex(ValueError, "disagree"):
                live_memory.enrich_profile("20049212", {**profile, "name": "Shanyder Smith"})

    def test_native_biography_decodes_linked_first_paragraph(self):
        rich = (b"\x01Q2xpY2sgdG8gdmlldyBzb21lb25lJ3MgcHJvZmlsZQ==\x02Someone\x01 "
                b"joined \x01Q2xpY2sgdG8gdmlldyBhIGNsdWI=\x02A Club\x01 in 2023 and made a strong impression.\n\n"
                b"Later career details.")
        self.assertEqual(native_profile._decode_biography(rich),
                         "Someone joined A Club in 2023 and made a strong impression.")
        self.assertEqual(native_profile._decode_biography(b"unverified text"), "")


class SquadSafetyTests(unittest.TestCase):
    def test_native_portrait_index_rejects_bare_lookup_strings(self):
        key = b"graphics/pictures/person/123/portrait"
        valid = struct.pack("<II", 1, len(key)) + key + b"\0"
        self.assertTrue(native_portrait._indexed(valid, key))
        self.assertFalse(native_portrait._indexed(b"pictures/person/123/portrait\0", key))
        self.assertFalse(native_portrait._indexed(struct.pack("<II", 0, len(key)) + key + b"\0", key))

    def test_scroll_requires_matching_rows(self):
        previous = ("A", "B", "C", "D")
        self.assertEqual(_overlap(previous, ("C", "D", "E")), 2)
        with self.assertRaisesRegex(ValueError, "skipped rows"):
            _overlap(previous, ("E", "F"))

    def test_blank_portrait_is_not_a_missing_face(self):
        blank = Image.new("RGB", (1854, 1011), (32, 32, 32))
        self.assertEqual(portrait_state(blank), "unknown")

    def test_relocates_row_after_game_recenters_table(self):
        with patch("squad_batch._read_rows", return_value=[(426, "Noah Allen"),
                                                         (456, "Sergio Busquets")]):
            self.assertEqual(_current_row("window", object(), object(), "Noah Allen"), 426)

    def test_waits_for_portrait_after_title_renders(self):
        with patch("squad_batch.read_portrait_state", return_value="missing"), \
             patch("squad_batch.portrait_state", side_effect=["unknown", "missing"]), \
             patch("squad_batch.capture_window", return_value=object()), \
             patch("squad_batch.read_identity", return_value=SimpleNamespace(player_id="123")), \
             patch("squad_batch.time.sleep"):
            self.assertEqual(_portrait_state_ready("window", object(), "123", object()),
                             "missing")

    def test_native_present_portrait_needs_no_pixel_classification(self):
        with patch("squad_batch.read_portrait_state", return_value="present"), \
             patch("squad_batch.portrait_state") as pixels:
            self.assertEqual(_portrait_state_ready("window", object(), "123", object()),
                             "present")
        pixels.assert_not_called()

    def test_native_missing_cannot_override_visible_picture(self):
        with patch("squad_batch.read_portrait_state", return_value="missing"), \
             patch("squad_batch.portrait_state", return_value="present"), \
             patch("squad_batch.read_identity", return_value=SimpleNamespace(player_id="123")):
            self.assertEqual(_portrait_state_ready("window", object(), "123", object()),
                             "present")

    def test_return_to_squad_waits_without_reclicking_navigation(self):
        squad = Image.new("RGB", (1854, 1011), "black")
        profile = squad.copy()
        profile.paste("red", (374, 12, 1100, 54))
        with patch("squad_batch._click") as click, \
             patch("squad_batch.capture_window", side_effect=[profile, profile, squad]), \
             patch("squad_batch._squad_title", side_effect=ValueError("loading")), \
             patch("squad_batch.time.sleep"):
            _return_to_squad("window", _title_image(squad), object(), "123")
        click.assert_called_once_with("window", 60, 103)


    def _clock(self, step=1.0):
        now = [0.0]

        def monotonic():
            now[0] += step
            return now[0]
        return monotonic

    def test_return_to_squad_repeats_back_only_while_profile_is_unchanged(self):
        squad = Image.new("RGB", (1854, 1011), "black")
        profile = squad.copy()
        profile.paste("red", (374, 12, 1100, 54))
        # The first Back is dropped: the same profile stays up until the retry.
        screens = [profile] * 6 + [squad]
        with patch("squad_batch._click") as click, \
             patch("squad_batch.capture_window", side_effect=screens), \
             patch("squad_batch._squad_title", side_effect=ValueError("not squad")), \
             patch("squad_batch.time.monotonic", self._clock()), \
             patch("squad_batch.time.sleep"):
            _return_to_squad("window", _title_image(squad), object(), "123")
        self.assertEqual(click.call_count, 2)

    def test_return_to_squad_never_repeats_back_after_navigation_started(self):
        squad = Image.new("RGB", (1854, 1011), "black")
        profile, other = squad.copy(), squad.copy()
        profile.paste("red", (374, 12, 1100, 54))
        other.paste("blue", (374, 12, 1100, 54))
        with patch("squad_batch._click") as click, \
             patch("squad_batch.capture_window", side_effect=[profile, other] + [other] * 200), \
             patch("squad_batch._squad_title", side_effect=ValueError("not squad")), \
             patch("squad_batch.time.monotonic", self._clock()), \
             patch("squad_batch.time.sleep"), \
             self.assertRaisesRegex(ValueError, "Could not return"):
            _return_to_squad("window", _title_image(squad), object(), "123")
        click.assert_called_once()

class GameWindowTests(unittest.TestCase):
    @staticmethod
    def _display(windows):
        import refresh

        def window(wid, cls, name, mapped, children=()):
            return SimpleNamespace(
                id=wid, get_wm_class=lambda: cls, get_wm_name=lambda: name,
                get_attributes=lambda: SimpleNamespace(
                    map_state=refresh.X.IsViewable if mapped else refresh.X.IsUnmapped),
                query_tree=lambda: SimpleNamespace(children=list(children)))

        root = window(1, None, None, True, [window(*spec) for spec in windows])
        return SimpleNamespace(screen=lambda: SimpleNamespace(root=root), close=lambda: None)

    def test_finds_only_the_visible_game_window(self):
        import refresh
        game = ("steam_app_2252570", "steam_app_2252570")
        tree = [(0x10, game, "Default IME", False), (0x11, game, "Steam", False),
                (0x12, game, "Football Manager 2024", False),
                (0x4a00005, game, "Football Manager 2024", True),
                (0x13, ("other", "other"), "Football Manager 2024", True)]
        with patch.object(refresh.display, "Display", return_value=self._display(tree)):
            self.assertEqual(refresh.find_game_window(), "0x4a00005")

    def test_refuses_missing_or_ambiguous_game_window(self):
        import refresh
        game = ("steam_app_2252570", "steam_app_2252570")
        for tree in ([], [(0x20, game, "Football Manager 2024", True),
                          (0x21, game, "Football Manager 2024", True)]):
            with patch.object(refresh.display, "Display", return_value=self._display(tree)):
                with self.assertRaisesRegex(ValueError, "exactly one visible FM24"):
                    refresh.find_game_window()


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

    def test_companion_falls_back_to_preferences_reload(self):
        handler = SimpleNamespace(reload_port=8770, token="t" * 43, window_id="0x1", engine=None)
        with patch.object(companion, "request_native_reload") as native, \
                patch.object(companion, "reload_skin") as preferences:
            companion.reload_portrait(handler, "42", False)
            native.assert_called_once_with(8770, "t" * 43)
            preferences.assert_not_called()
            native.side_effect = TimeoutError("no reply")
            companion.reload_portrait(handler, "42", True)
            preferences.assert_called_once_with("0x1", "42", None, staff=True)
        handler.reload_port = None
        with patch.object(companion, "request_native_reload") as native, \
                patch.object(companion, "reload_skin") as preferences:
            companion.reload_portrait(handler, "42", False)
            native.assert_not_called()
            preferences.assert_called_once()


class CompanionHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        handler = companion.Handler
        handler.token = "a" * 43
        handler.window_id = "0x123"
        handler.graphics_dir = Path(self.temp.name)
        handler.preview_only = True
        handler.engine = object()
        handler.nonces = {}
        handler.in_flight = False
        handler.reload_port = None
        activate = patch.object(companion, "_activate_game")
        activate.start()
        self.addCleanup(activate.stop)
        overlay = patch.object(companion, "ProgressOverlay")
        overlay.start()
        self.addCleanup(overlay.stop)
        enrichment = patch.object(companion, "enrich_profile", side_effect=lambda _id, profile: profile)
        enrichment.start()
        self.addCleanup(enrichment.stop)
        native = patch.object(companion, "read_open_profile",
                              side_effect=live_memory.MemoryUnavailable("test fallback"))
        native.start()
        self.addCleanup(native.stop)
        self.server = companion.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.addCleanup(self.server.server_close)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.server.shutdown)
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def _nonce(self):
        page = urllib.request.urlopen(self.base + "/fm24?key=" + companion.Handler.token).read().decode()
        self.assertIn("result.warning ? '\\n'+result.warning", page)
        return re.search(r'"nonce": "([^"]+)"', page).group(1)

    def _post(self, nonce, expected_id=None, mode="generate"):
        body = json.dumps({"key": companion.Handler.token, "nonce": nonce,
                           "expectedPlayerId": expected_id, "mode": mode}).encode()
        return urllib.request.Request(self.base + "/fm24/generate", data=body,
                                      headers={"Content-Type": "application/json"}, method="POST")

    def test_preview_does_not_generate_and_nonce_cannot_be_reused(self):
        nonce = self._nonce()
        with patch.object(companion, "capture_window", return_value=Image.new("RGB", companion.BASE_SIZE)), \
             patch.object(companion, "read_staff_profile", side_effect=ValueError("not staff")), \
             patch.object(companion, "read_player_profile", return_value=("123", {"name": "Test Player"})), \
             patch.object(companion, "profile_prompt", return_value="test prompt"), \
             patch.object(companion, "generate_png") as generate:
            response = json.load(urllib.request.urlopen(self._post(nonce)))
            self.assertEqual((response["preview"], response["playerId"], response["prompt"]),
                             (True, "123", "test prompt"))
            generate.assert_not_called()
            with self.assertRaises(urllib.error.HTTPError) as repeated:
                urllib.request.urlopen(self._post(nonce))
            self.assertEqual(repeated.exception.code, 400)

    def test_wrong_host_and_wrong_token_are_rejected(self):
        request = urllib.request.Request(self.base + "/fm24?key=" + companion.Handler.token,
                                         headers={"Host": "unrelated.example"})
        with self.assertRaises(urllib.error.HTTPError) as wrong_host:
            urllib.request.urlopen(request)
        self.assertEqual(wrong_host.exception.code, 403)
        with self.assertRaises(urllib.error.HTTPError) as wrong_token:
            urllib.request.urlopen(self.base + "/fm24?key=wrong")
        self.assertEqual(wrong_token.exception.code, 404)

    def test_generation_saves_only_after_valid_capture(self):
        companion.Handler.preview_only = False
        nonce = self._nonce()
        with patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1854, 1011))), \
             patch.object(companion, "read_staff_profile", side_effect=ValueError("not staff")), \
             patch.object(companion, "read_player_profile", return_value=("123", {"name": "Test Player"})), \
             patch.object(companion, "profile_prompt", return_value="test prompt"), \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png", return_value=PNG_SIGNATURE + b"test") as generate, \
             patch.object(companion, "reload_skin") as reload:
            response = json.load(urllib.request.urlopen(self._post(nonce)))
            self.assertTrue(response["ok"])
            portraits = list(Path(self.temp.name).glob("123-*.png"))
            self.assertEqual(len(portraits), 1)
            self.assertEqual(portraits[0].read_bytes(), PNG_SIGNATURE + b"test")
            generate.assert_called_once_with({"OPENAI_API_KEY": "test-only"}, "test prompt")
            reload.assert_called_once()

    def _update(self, *, found, preview):
        companion.Handler.preview_only = preview
        profile = {"name": "Lionel Messi", "age": 38, "club": "Inter Miami"}
        with patch.object(companion, "read_update_profile", return_value=("7458500", profile)), \
             patch.object(companion, "find_existing_portrait", return_value=found) as lookup, \
             patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "game_dir_from_process", return_value=Path("/games/fm24")), \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png") as generate, \
             patch.object(companion, "edit_png", return_value=PNG_SIGNATURE + b"updated") as edit, \
             patch.object(companion, "reload_skin"):
            try:
                result = json.load(urllib.request.urlopen(self._post(self._nonce(), "7458500", "update")))
            except urllib.error.HTTPError as error:
                result = {"status": error.code, **json.load(error)}
            generate.assert_not_called()
            lookup.assert_called_once_with("7458500", Path(self.temp.name), Path("/games/fm24"))
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
        saved = list(Path(self.temp.name).glob("7458500-*.png"))
        self.assertEqual([path.read_bytes() for path in saved], [PNG_SIGNATURE + b"updated"])

    def test_update_reads_only_identity_and_detects_navigation(self):
        identity = ("7458500", {"type": "player", "name": "Lionel Messi", "age": 35, "club": "Inter Miami"})
        with patch.object(companion, "read_open_profile", side_effect=[identity, identity]) as native, \
             patch.object(companion, "read_prompt_profile") as full:
            self.assertEqual(companion.read_update_profile("0x123", object()), identity)
            self.assertEqual(native.call_args.kwargs, {"identity_only": True})
            full.assert_not_called()
        other = ("2000045865", dict(identity[1], name="Ian Fray"))
        with patch.object(companion, "read_open_profile", side_effect=[identity, other]):
            with self.assertRaisesRegex(ValueError, "changed"):
                companion.read_update_profile("0x123", object())
        with patch.object(companion, "read_open_profile", side_effect=live_memory.MemoryUnavailable("gone")), \
             patch.object(companion, "read_prompt_profile", return_value=identity) as full:
            self.assertEqual(companion.read_update_profile("0x123", object()), identity)
            full.assert_called_once()

    def test_native_mode_reads_memory_only_and_never_touches_the_window(self):
        companion.Handler.native = True
        self.addCleanup(setattr, companion.Handler, "native", False)
        profile = {"type": "player", "name": "Lionel Messi", "age": 35, "club": "Inter Miami"}
        with patch.object(companion, "read_native_profile", return_value=("7458500", profile)) as native, \
             patch.object(companion, "_activate_game", side_effect=AssertionError("X11 used")), \
             patch.object(companion, "capture_window", side_effect=AssertionError("X11 used")), \
             patch.object(companion, "profile_prompt", return_value="test prompt"):
            result = json.load(urllib.request.urlopen(self._post(self._nonce(), "7458500")))
        self.assertEqual((result["preview"], result["prompt"]), (True, "test prompt"))
        self.assertEqual(native.call_args.kwargs, {"identity_only": False})

    def test_native_profile_takes_biography_from_the_open_screen(self):
        from native_screen import OpenScreen
        profile = {"type": "player", "name": "Lionel Messi", "age": 35}
        screen = OpenScreen("7458500", "Barcelona legend Lionel Messi is truly one of world soccer's superstars.")
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile", side_effect=lambda *a, **k: ("7458500", dict(profile))), \
             patch.object(companion, "read_open_screen", return_value=screen):
            person_id, result = companion.read_native_profile()
        self.assertEqual(result["biographyHeadline"], screen.biography)
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile", side_effect=lambda *a, **k: ("7458500", dict(profile))), \
             patch.object(companion, "read_open_screen", return_value=OpenScreen("2000045865", "")):
            with self.assertRaisesRegex(ValueError, "shows 2000045865"):
                companion.read_native_profile()
        later = iter([("7458500", dict(profile)), ("2000045865", dict(profile))])
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile", side_effect=lambda *a, **k: next(later)), \
             patch.object(companion, "read_open_screen", return_value=screen):
            with self.assertRaisesRegex(ValueError, "changed"):
                companion.read_native_profile()
        with patch.object(companion, "find_game_pid", return_value=1), \
             patch.object(companion, "read_open_profile", side_effect=live_memory.MemoryUnavailable("gone")), \
             patch.object(companion, "capture_window", side_effect=AssertionError("no screen fallback")):
            with self.assertRaisesRegex(ValueError, "from game memory"):
                companion.read_native_profile()

    def test_update_without_a_face_is_refused_before_any_request(self):
        result, edit = self._update(found=None, preview=False)
        self.assertEqual(result["status"], 422)
        self.assertIn("no face to update", result["error"])
        edit.assert_not_called()

    def test_unsupported_window_rejected_before_api_request(self):
        companion.Handler.preview_only = False
        nonce = self._nonce()
        with patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1600, 900))), \
             patch.object(companion, "generate_png") as generate:
            with self.assertRaises(urllib.error.HTTPError) as rejected:
                urllib.request.urlopen(self._post(nonce))
            self.assertEqual(rejected.exception.code, 422)
            self.assertIn("no image request was made", rejected.exception.read().decode())
            generate.assert_not_called()

    def test_profile_capture_retries_incomplete_attributes(self):
        screenshot = SimpleNamespace(size=(1854, 1011))
        with patch.object(companion, "capture_window", return_value=screenshot) as capture, \
             patch.object(companion, "read_staff_profile", side_effect=ValueError("not staff")), \
             patch.object(companion, "read_player_profile",
                          side_effect=[ValueError("Acceleration not drawn"),
                                       ("123", {"name": "Test Player"})]), \
             patch.object(companion.time, "sleep"):
            self.assertEqual(companion.read_visible_profile("0x123", object())[0], "123")
            self.assertEqual(capture.call_count, 2)

    def test_native_profile_uses_the_biography_shown_on_screen(self):
        profile = {"type": "staff", "name": "Test Coach", "age": 50,
                   "nationality": "English", "jobTitle": "Assistant Coach", "club": "Test Club"}
        candidates = {"Coach's love affair with the club won through in 2008.",
                      "Test Club icon Test Coach spent his whole career there."}
        with patch.object(companion, "read_open_profile", side_effect=[("456", profile.copy()),
                                                                ("456", profile.copy())]) as native, \
             patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1854, 1011))), \
             patch.object(companion, "_biography", return_value="Test Club icon Test Coach spent his"), \
             patch.object(companion, "read_staff_identity", return_value=("456", "Test Coach", "Assistant Coach", "Test Club")), \
             patch.object(companion, "biography_candidates", return_value=candidates):
            person_id, result = companion.read_prompt_profile("0x123", object())
        self.assertEqual((person_id, result["biographyHeadline"]),
                         ("456", "Test Club icon Test Coach spent his whole career there."))
        self.assertEqual(native.call_count, 2)

    def test_biography_screenshot_must_match_native_person_even_when_native_reads_agree(self):
        for kind in ("player", "staff"):
            with self.subTest(kind=kind):
                profile = {"type": kind, "name": "Original Person"}
                with patch.object(companion, "read_open_profile", return_value=("123", profile)), \
                     patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1854, 1011))), \
                     patch.object(companion, "_biography", return_value="Another person's career headline."), \
                     patch.object(companion, "read_identity", return_value=SimpleNamespace(player_id="456")), \
                     patch.object(companion, "read_staff_identity", return_value=("456", "Other", "Coach", "Club")), \
                     patch.object(companion, "biography_candidates") as candidates, \
                     self.assertRaisesRegex(ValueError, "changed during biography capture"):
                    companion.read_prompt_profile("0x123", object())
                candidates.assert_not_called()

    def test_biography_choice_fails_closed_to_visible_text(self):
        candidates = {"Someone joined A Club in 2023 and impressed.",
                      "Someone joined A Club in 2023 and impressed everyone."}
        # Ambiguous memory paragraphs: use what is visible, never guess.
        self.assertEqual(native_profile.choose_biography(candidates, "Someone joined A Club in 2023 and"),
                         "Someone joined A Club in 2023 and")
        self.assertEqual(native_profile.choose_biography(candidates, ""), "")
        self.assertEqual(native_profile.choose_biography({"Later paragraph about Someone's return."},
                                                         "Club icon Someone began his career"),
                         "Club icon Someone began his career")

    def test_biography_choice_prefers_headline_over_joined_paragraphs(self):
        headline = "Ryan Sailor is a name recognizable to many, where he plies his trade."
        candidates = {headline, headline + " Sailor scored his first senior goal for Inter Miami.",
                      "Ryan Sailor began his career at Real Colorado in 2013."}
        visible = "Ryan Sailor is a name recognizable to many, where he plies his trade. Sailor scored"
        self.assertEqual(native_profile.choose_biography(candidates, visible), headline)

    def test_biography_ocr_fallback_drops_link_icon(self):
        visible = "He was drafted to the MLS in 2022 by !InterMiami, as a 1st round pick (9 overall)!"
        self.assertEqual(native_profile.choose_biography(set(), visible),
                         "He was drafted to the MLS in 2022 by InterMiami, as a 1st round pick (9 overall)!")

    def test_biography_candidates_include_headline_linking_person_later(self):
        def link(text, tooltip):
            return b"\x01" + base64.b64encode(tooltip.encode()) + b"\x02" + text.encode() + b"\x01"
        header = b"\x00" * 8 + b"\x01\x00\x00\x00" + b"\x90\x00\x00\x00"
        headline = (header + link("A Club", "Click to view A Club") + b" icon " +
                    link("Test Coach", "Click to view Test Coach's profile") +
                    b" spent his whole career at the club.\n\nLater.\x00")
        later = (header + link("Coach", "Click to view Test Coach's profile") +
                 b"'s love affair with the club won through in 2008.\x00")
        data = b"\xff" * 64 + headline + b"\xff" * 64 + later

        class FakeScan:
            def __init__(self, pid, overlap):
                pass

            def __iter__(self):
                yield 1000, data

            def read(self, address, size):
                return data[address - 1000:address - 1000 + size]

        with patch.object(native_profile, "MemoryScan", FakeScan):
            self.assertEqual(native_profile.biography_candidates(1, "Test Coach"),
                             {"A Club icon Test Coach spent his whole career at the club.",
                              "Coach's love affair with the club won through in 2008."})

    def test_squad_expected_id_is_checked_before_billing(self):
        companion.Handler.preview_only = False
        nonce = self._nonce()
        with patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1854, 1011))), \
             patch.object(companion, "read_staff_profile", side_effect=ValueError("not staff")), \
             patch.object(companion, "read_player_profile", return_value=("123", {"name": "Test Player"})), \
             patch.object(companion, "generate_png") as generate:
            with self.assertRaises(urllib.error.HTTPError) as rejected:
                urllib.request.urlopen(self._post(nonce, expected_id="456"))
            self.assertEqual(rejected.exception.code, 422)
            self.assertIn("no image request was made", rejected.exception.read().decode())
            generate.assert_not_called()

    def test_staff_generation_uses_staff_reload(self):
        companion.Handler.preview_only = False
        nonce = self._nonce()
        staff = {"type": "staff", "name": "Test Coach", "age": 50,
                 "nationality": "English", "jobTitle": "Goalkeeping Coach"}
        with patch.object(companion, "capture_window", return_value=SimpleNamespace(size=(1854, 1011))), \
             patch.object(companion, "read_staff_profile", return_value=("456", staff)), \
             patch.object(companion, "read_player_profile") as read_player, \
             patch.object(companion, "load_env", return_value={"OPENAI_API_KEY": "test-only"}), \
             patch.object(companion, "generate_png", return_value=PNG_SIGNATURE + b"test"), \
             patch.object(companion, "reload_skin") as reload:
            response = json.load(urllib.request.urlopen(self._post(nonce)))
            self.assertTrue(response["refreshed"])
            read_player.assert_not_called()
            reload.assert_called_once_with("0x123", "456", companion.Handler.engine, staff=True)

    def test_regeneration_uses_new_graphics_mapping(self):
        directory = Path(self.temp.name)
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
