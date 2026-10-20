"""Native bridge, skin, and installer behaviour, without game/API access."""

import hashlib
import json
import socket
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24" / "native_menu"))
import layout  # noqa: E402  The fixtures are records from the Windows fm.exe.
layout.LAYOUT.update(layout.WINDOWS)
import install
import native_listener
import skin

KEY = "k" * 43
SQUAD_PANEL = """<panel>
\t<container>
\t\t<container navigation_container="true">
\t\t\t<container>
\t\t\t\t<layout class="arrange_horizontal_attachment" offset="0" alignment="right" />
\t\t\t\t<!-- quick pick -->
\t\t\t\t<container>
\t\t\t\t\t<widget class="icon_button" id="askp" auto_size="all" />
\t\t\t\t\t<widget class="popup_button" id="apic" fixed="true" width="45" />
\t\t\t\t</container>
\t\t\t\t<widget class="embedded_filter_button" id="plsf" target="plti" auto_size="all" />
\t\t\t</container>
\t\t</container>
\t</container>
</panel>
"""
PLAYER_PANEL = """<panel>
\t<container>
\t\t<widget class="player_personal_details_panel" id="ppdp" height="80" />
\t</container>
</panel>
"""
STAFF_PANEL = """<panel>
\t<container>
\t\t<widget class="picture" id="ppic" />
\t</container>
</panel>
"""


class Bridge:
    """A started listener plus a UDP client speaking the DLL's protocol."""

    def __init__(self, test, **options):
        options.setdefault("notify", lambda message: None)
        self.server = native_listener.start_native_listener(1234, 0, KEY, None, **options)
        self.client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.client.settimeout(.3)
        self.address = self.server.getsockname()
        test.addCleanup(self.close)

    def send(self, serial, token=KEY, action="profile", pid=77, age=0.0, person=None):
        clicked = round((time.time() - age) * 1000)
        if person is None:
            person = "123" if action == "profile" else "0"
        self.client.sendto(f"FMFACE24 5 {pid} {serial} {action} {clicked} {person} {token}".encode(),
                           self.address)

    def reply(self):
        return self.client.recv(512)

    def close(self):
        self.client.close()
        self.server.close()


class NativeBridgeTests(unittest.TestCase):
    def test_worker_start_failure_releases_lock_and_keeps_listener_alive(self):
        failures = []
        notified = threading.Event()
        bridge = Bridge(self, notify=lambda message: (failures.append(message), notified.set()))
        with patch.object(native_listener.threading.Thread, "start", side_effect=RuntimeError("thread limit")), \
                patch.object(native_listener, "request_generation") as request:
            bridge.send(1)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(notified.wait(2))
            notified.clear()
            bridge.send(2)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 2 accepted")
            self.assertTrue(notified.wait(2))
            request.assert_not_called()
        self.assertTrue(all("thread limit" in message for message in failures))

    def test_authentication_replay_and_busy_clicks(self):
        started = threading.Event()
        finish = threading.Event()

        def generate(*args, **kwargs):
            started.set()
            finish.wait(2)
            return {"ok": True, "preview": True, "playerId": "123", "prompt": "test"}

        with patch.object(native_listener, "request_generation", side_effect=generate) as request:
            bridge = Bridge(self)
            try:
                bridge.send(1, token="incorrect")
                with self.assertRaises(socket.timeout):
                    bridge.reply()
                self.assertFalse(started.is_set())
                bridge.send(1)
                self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
                self.assertTrue(started.wait(1))
                # A retransmission gets the same answer and starts nothing new.
                bridge.send(1)
                self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
                bridge.send(2)
                self.assertEqual(bridge.reply(), b"FMFACE24 ACK 2 busy")
                bridge.send(1)
                with self.assertRaises(socket.timeout):
                    bridge.reply()
                self.assertEqual(request.call_count, 1)
                bridge.send(3, action="unknown")
                with self.assertRaises(socket.timeout):
                    bridge.reply()
                self.assertEqual(request.call_count, 1)
            finally:
                finish.set()

    def test_request_is_bound_to_person_read_from_game_memory(self):
        # The DLL read Farias's ID from the clicked screen's header. Even if
        # FM24 now shows Ian Fray (as in the retest), the request names
        # Farias, and the companion refuses when the open profile differs.
        done = threading.Event()
        seen = []

        def generate(port, token, handler, *, expected_id=None, mode="generate"):
            seen.append(expected_id)
            done.set()
            return {"ok": False, "error": "The open FM24 profile is now person 2000045865, not the "
                                          "requested person 14240979; no image request was made."}

        failures = []
        with patch.object(native_listener, "request_generation", side_effect=generate):
            bridge = Bridge(self, notify=failures.append)
            bridge.send(1, person="14240979", age=.53)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(done.wait(2))
        self.assertEqual(seen, ["14240979"])

    def test_update_row_requests_an_update_for_the_clicked_person(self):
        done = threading.Event()
        seen = []

        def generate(port, token, handler, *, expected_id=None, mode="generate"):
            seen.append((expected_id, mode))
            done.set()
            return {"ok": True, "preview": True, "playerId": expected_id, "prompt": "test"}

        with patch.object(native_listener, "request_generation", side_effect=generate):
            bridge = Bridge(self)
            bridge.send(1, action="update", person="7458500")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(done.wait(2))
        self.assertEqual(seen, [("7458500", "update")])

    def test_profile_without_reported_person_is_refused(self):
        failures = []
        done = threading.Event()
        generated = threading.Event()

        def generate(*args, **kwargs):
            generated.set()
            return {"ok": True, "preview": True, "playerId": "123", "prompt": "test"}

        with patch.object(native_listener, "request_generation") as request:
            bridge = Bridge(self, notify=lambda message: (failures.append(message), done.set()))
            bridge.send(1, person="0")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(done.wait(2))
            request.assert_not_called()
            request.side_effect = generate
            bridge.send(2)  # The refusal released the busy lock.
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 2 accepted")
            self.assertTrue(generated.wait(2))
        self.assertIn("did not report which person", failures[0])
        self.assertEqual(request.call_args.kwargs["expected_id"], "123")

    def test_an_error_already_in_the_progress_dialog_is_not_shown_again(self):
        notices = []
        for result in ({"ok": False, "error": "HTTP 500", "shown": True},
                       {"ok": True, "refreshed": False, "warning": "reload failed", "shown": True},
                       {"ok": False, "error": "HTTP 500"}):
            done = threading.Event()
            with patch.object(native_listener, "request_generation", return_value=result), \
                 patch.object(native_listener, "describe_result", side_effect=lambda value: done.set() or ""):
                bridge = Bridge(self, notify=notices.append)
                bridge.send(1)
                self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
                self.assertTrue(done.wait(2))
                time.sleep(.1)
                bridge.close()
        self.assertEqual(notices, ["HTTP 500"])

    def test_delayed_command_is_refused(self):
        with patch.object(native_listener, "request_generation") as request:
            bridge = Bridge(self)
            bridge.send(1, age=5)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 stale")
            # The retransmission gets the same refusal.
            bridge.send(1, age=5.7)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 stale")
            bridge.send(2, action="squad", age=3)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 2 stale")
            # Future-dated (clock jump) commands are refused too.
            bridge.send(3, age=-2)
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 3 stale")
            # A fresh click is still accepted: the refusal never takes the busy lock.
            done = threading.Event()
            request.side_effect = lambda *args, **kwargs: (done.set(), {"ok": True, "preview": True})[1]
            bridge.send(4, person="456")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 4 accepted")
            self.assertTrue(done.wait(2))
        self.assertEqual(request.call_args.kwargs["expected_id"], "456")

    def test_squad_command_ignores_person_field(self):
        done = threading.Event()
        with patch.object(native_listener, "run_native_squad_batch", side_effect=lambda *args, **kwargs: done.set()) as batch:
            bridge = Bridge(self)
            bridge.send(1, action="squad")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(done.wait(2))
        batch.assert_called_once()

    def test_old_protocol_packets_are_ignored(self):
        bridge = Bridge(self)
        clicked = round(time.time() * 1000)
        for packet in (f"FMFACE24 1 77 1 profile {KEY}", f"FMFACE24 2 77 1 profile {clicked} {KEY}",
                       f"FMFACE24 3 77 1 profile {clicked} 123 {KEY}"):
            bridge.client.sendto(packet.encode(), bridge.address)
            with self.assertRaises(socket.timeout):
                bridge.reply()

    def test_face_check_answers_for_the_popup(self):
        asked = []
        bridge = Bridge(self, has_face=lambda person: asked.append(person) or person == "123")
        for serial, person, answer in ((1, "123", b"FMFACE24F 1 yes"), (2, "456", b"FMFACE24F 2 no")):
            bridge.client.sendto(f"FMFACE24F 1 {serial} {person} {KEY}".encode(), bridge.address)
            self.assertEqual(bridge.reply(), answer)
        for packet in ("FMFACE24F 1 3 123 incorrect", f"FMFACE24F 2 3 123 {KEY}", f"FMFACE24F 1 3 ../1 {KEY}",
                       f"FMFACE24F 1 3 123 {KEY} extra"):
            bridge.client.sendto(packet.encode(), bridge.address)
            with self.assertRaises(socket.timeout):
                bridge.reply()
        self.assertEqual(asked, ["123", "456"])
        # Clicks still work on the same port.
        with patch.object(native_listener, "run_native_squad_batch"):
            bridge.send(1, action="squad")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")

    def test_references_resolve_to_the_person_without_screen_ids(self):
        # The macOS library sends "r<reference>" when the header shows no ID.
        resolved = []

        def resolve(reference, *, wait):
            resolved.append((reference, wait))
            return "123"
        bridge = Bridge(self, has_face=lambda person: person == "123", resolve=resolve)
        bridge.client.sendto(f"FMFACE24F 1 1 r16780302 {KEY}".encode(), bridge.address)
        self.assertEqual(bridge.reply(), b"FMFACE24F 1 yes")
        self.assertEqual(resolved, [(16780302, False)])  # a menu never waits for the index
        done = threading.Event()
        with patch.object(native_listener, "request_generation",
                          side_effect=lambda *a, **k: (done.set(), {"ok": True, "preview": True})[1]) as request:
            bridge.send(1, person="r16780302")
            self.assertEqual(bridge.reply(), b"FMFACE24 ACK 1 accepted")
            self.assertTrue(done.wait(2))
        self.assertEqual(request.call_args.kwargs["expected_id"], "123")
        self.assertEqual(resolved[-1], (16780302, True))
        # Without a resolver (or with a malformed field) nothing is requested.
        notices = []
        plain = Bridge(self, notify=notices.append)
        with patch.object(native_listener, "request_generation") as request:
            plain.send(1, person="r16780302")
            self.assertEqual(plain.reply(), b"FMFACE24 ACK 1 accepted")
            for _ in range(20):
                if notices:
                    break
                time.sleep(.05)
            request.assert_not_called()
        self.assertIn("screen IDs", notices[0])
        plain.send(2, person="rx")
        with self.assertRaises(socket.timeout):
            plain.reply()

    def test_failed_face_check_sends_no_answer(self):
        # The DLL then leaves "Update face" enabled; the update is refused later.
        bridge = Bridge(self, has_face=lambda person: (_ for _ in ()).throw(OSError("unreadable")))
        bridge.client.sendto(f"FMFACE24F 1 1 123 {KEY}".encode(), bridge.address)
        with self.assertRaises(socket.timeout):
            bridge.reply()
        no_check = Bridge(self)
        no_check.client.sendto(f"FMFACE24F 1 1 123 {KEY}".encode(), no_check.address)
        with self.assertRaises(socket.timeout):
            no_check.reply()

    def test_occupied_bridge_port_is_a_clear_startup_error(self):
        holder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        holder.bind(("127.0.0.1", 0))
        try:
            with self.assertRaisesRegex(RuntimeError, "another companion"):
                native_listener.start_native_listener(1, holder.getsockname()[1], KEY, None)
        finally:
            holder.close()


class NativeDllSourceTests(unittest.TestCase):
    """The DLL source against the build data and the companion's protocol."""

    source = (Path(__file__).resolve().parents[1] / "fm24" / "native_menu" / "fmface24.c").read_text()

    def test_every_game_address_comes_from_the_supported_build(self):
        supported = json.loads((Path(install.__file__).with_name("supported_build.json")).read_text())
        generated = set()
        for group, prefix in (("skinReload", "RELOAD_"), ("squadMenu", "SQUAD_MENU_"), ("dialog", "DIALOG_"),
                              ("profileMenu", "PROFILE_MENU_")):
            for key in supported[group]:
                generated.add(prefix + "".join("_" + c if c.isupper() else c.upper() for c in key))
        import re
        used = set(re.findall(r"\b((?:RELOAD|SQUAD_MENU|DIALOG|PROFILE_MENU)_[A-Z_]+_(?:RVA|OFFSET))\b", self.source))
        self.assertIn("PROFILE_MENU_TAB_BUTTON_VTABLE_RVA", used)
        self.assertLessEqual(used, generated)
        build = (Path(install.__file__).with_name("build.py")).read_text()
        self.assertIn('("profileMenu", "PROFILE_MENU_")', build)

    def test_clicks_name_the_game_run_not_the_reused_process_id(self):
        self.assertIn('(unsigned long long)bridge_instance, (long)serial', self.source)
        # FM24's process ID only tells the companion it starts when to stop.
        self.assertEqual(self.source.count("GetCurrentProcessId()"), 1)
        self.assertIn('--started-by-fm24 %lu', self.source)

    def test_person_lookup_offsets_match_the_companion(self):
        import re
        defines = {name: int(value, 16) for name, value in
                   re.findall(r"#define (\w+)\s+(0x[0-9A-Fa-f]+)\b", self.source)}
        self.assertEqual((defines["SIDEBAR_VTABLE_RVA"], defines["SIDEBAR_OBJECT"]),
                         (layout.WINDOWS["sidebar_vtable"], layout.WINDOWS["sidebar_reference"]))
        self.assertEqual((defines["DATABASE_GLOBAL_RVA"], defines["DATABASE_PEOPLE"]),
                         layout.WINDOWS["person_table"])
        # The DLL accepts exactly the person classes the companion does.
        self.assertEqual({defines[name] for name in ("PLAYER_VTABLE_RVA", "NON_PLAYER_VTABLE_RVA",
                                                     "PLAYER_COACH_VTABLE_RVA")},
                         set(layout.WINDOWS["person_vtables"]))

    def test_face_check_packets_match_the_listener(self):
        self.assertIn('"FMFACE24F 1 %ld %ld %s", (long)serial, (long)person, bridge_token', self.source)
        self.assertIn('"FMFACE24F %ld yes"', self.source)
        self.assertIn('"FMFACE24F %ld no"', self.source)


class NativeSkinTests(unittest.TestCase):
    def test_squad_panel_is_no_longer_modified(self):
        # The squad command is an item in FM24's own Squad menu (added by the DLL).
        with self.assertRaisesRegex(ValueError, "Unknown"):
            skin.modify_panel(SQUAD_PANEL, "squad")
        modified = skin.modify_panel(PLAYER_PANEL, "player")
        self.assertIn('select_event="FMFG"', modified)
        # Both rows post FMFG; the DLL tells them apart by the row ID.
        self.assertIn('<record id="FMFG">', modified)
        self.assertIn('<record id="FMFU">', modified)
        self.assertIn('value="Update face"', modified)
        with self.assertRaisesRegex(ValueError, "already"):
            skin.modify_panel(modified, "player")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class NativeInstallerTests(unittest.TestCase):
    def test_atomic_write_does_not_overwrite_an_existing_temporary_file(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            destination = root / "config.ini"
            unrelated = root / ".config.ini.fmface-tmp"
            unrelated.write_bytes(b"keep this file")
            install._atomic_write(destination, b"new configuration", 0o600)
            self.assertEqual(unrelated.read_bytes(), b"keep this file")
            self.assertEqual(destination.read_bytes(), b"new configuration")
            if sys.platform != "win32":  # Windows has no POSIX permission bits.
                self.assertEqual(destination.stat().st_mode & 0o777, 0o600)

    @unittest.skipIf(sys.platform == "win32", "Windows has no POSIX permission bits")
    def test_directory_rollback_preserves_permissions(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            directory = root / "private"
            directory.mkdir(mode=0o700)
            journal = install.Transaction()
            journal.remove_empty_dirs(directory, root)
            self.assertFalse(directory.exists())
            self.assertEqual(journal.rollback(), [])
            self.assertEqual(directory.stat().st_mode & 0o777, 0o700)

    def setUp(self):
        # The fixtures are a Windows/Proton game folder (fm.exe and winmm.dll) on any system.
        install.use_system(False)
        self.addCleanup(install.use_system, sys.platform == "darwin")
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        self.game, self.skin = root / "game", root / "skin"
        self.game.mkdir()
        (self.game / "fm.exe").write_bytes(b"fm24")
        for kind, text in (("player", PLAYER_PANEL), ("staff", STAFF_PANEL)):
            panel = self.skin / install.PANEL_FILES[kind]
            panel.parent.mkdir(parents=True, exist_ok=True)
            panel.write_text(text)
        self.squad_source = root / "team squad.xml"
        self.squad_source.write_text(SQUAD_PANEL)
        self.dll = root / "winmm.dll"
        self.dll.write_bytes(b"MZ...FMFACE24_NATIVE_MENU_HOOK_INSTALLED...")
        self.auth = root / "auth"
        self.auth.write_text(KEY + "\n")
        for name, value in (("game_running", False), ("SUPPORTED_SHA256", sha(self.game / "fm.exe"))):
            patcher = patch.object(install, name, return_value=value) if name == "game_running" \
                else patch.object(install, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def snapshot(self):
        return {str(path.relative_to(self.game.parent)): path.read_bytes()
                for base in (self.game, self.skin) for path in base.rglob("*") if path.is_file()}

    def run_installer(self, *extra):
        install.main(["--game-dir", str(self.game), "--skin-dir", str(self.skin),
                      "--dll", str(self.dll), *extra])

    def test_install_then_remove_restores_everything(self):
        before = self.snapshot()
        # An old script's --squad-panel-source is accepted and ignored.
        self.run_installer("--auth-file", str(self.auth), "--squad-panel-source", str(self.squad_source))
        manifest = json.loads((self.game / "fmface24-install.json").read_text())
        self.assertEqual(set(manifest["panels"]), {install.PANEL_FILES["player"], install.PANEL_FILES["staff"]})
        self.assertFalse((self.skin / install.LEGACY_SQUAD_PANEL).exists())
        self.run_installer("--remove")
        self.assertEqual(self.snapshot(), before)

    def test_the_companion_fm24_starts_is_in_a_utf16_config(self):
        companion = r"C:\Users\Zoë\FM Face\fmface-fm24.exe"
        self.run_installer("--auth-file", str(self.auth), "--companion", companion)
        data = (self.game / "fmface24.ini").read_bytes()
        self.assertEqual(data[:2], b"\xff\xfe")  # GetPrivateProfileStringW needs the byte-order mark.
        self.assertEqual(install.read_config(self.game / "fmface24.ini"),
                         {"enabled": "1", "port": "8769", "token": KEY, "companion": companion})
        # Without --companion (FM Face from source) there is nothing for FM24 to start.
        self.run_installer("--auth-file", str(self.auth))
        self.assertNotIn("companion", install.read_config(self.game / "fmface24.ini"))

    def test_the_skin_is_optional_and_a_companion_built_one_is_adopted(self):
        for kind in ("player", "staff"):
            (self.skin / install.PANEL_FILES[kind]).unlink()
        self.run_installer("--auth-file", str(self.auth))
        self.assertEqual(json.loads((self.game / "fmface24-install.json").read_text())["panels"], {})
        self.assertTrue((self.game / "winmm.dll").is_file())
        # The companion FM24 started then built the skin with its popups.
        for kind, text in (("player", PLAYER_PANEL), ("staff", STAFF_PANEL)):
            (self.skin / install.PANEL_FILES[kind]).write_text(skin.modify_panel(text, kind))
        self.run_installer("--auth-file", str(self.auth))
        panels = json.loads((self.game / "fmface24-install.json").read_text())["panels"]
        self.assertEqual({entry["created"] for entry in panels.values()}, {True})
        self.run_installer("--remove")
        self.assertFalse((self.skin / install.PANEL_FILES["player"]).exists())

    def _legacy_squad_install(self, existing_panel):
        """Reproduce a version 2 install that added the Squad Faces button."""
        for kind in ("player", "staff"):
            panel = self.skin / install.PANEL_FILES[kind]
            Path(str(panel) + install.BACKUP_SUFFIX).write_bytes(panel.read_bytes())
            panel.write_text(skin.modify_panel(panel.read_text(), kind))
        squad = self.skin / install.LEGACY_SQUAD_PANEL
        squad.parent.mkdir(parents=True, exist_ok=True)
        if existing_panel:
            Path(str(squad) + install.BACKUP_SUFFIX).write_text(SQUAD_PANEL)
        squad.write_text(SQUAD_PANEL.replace("<!-- quick pick -->", '<widget id="fmnQ"/><!-- quick pick -->'))
        (self.game / "winmm.dll").write_bytes(self.dll.read_bytes())
        names = [install.PANEL_FILES["player"], install.PANEL_FILES["staff"], install.LEGACY_SQUAD_PANEL]
        (self.game / "fmface24-install.json").write_text(json.dumps({"version": 2, "dllSha256": sha(self.dll),
            "panels": {name: {"sha256": sha(self.skin / name), "created": name == names[2] and not existing_panel}
                       for name in names}}))

    def test_upgrade_removes_created_squad_faces_panel(self):
        self._legacy_squad_install(existing_panel=False)
        self.run_installer("--auth-file", str(self.auth))
        manifest = json.loads((self.game / "fmface24-install.json").read_text())
        self.assertNotIn(install.LEGACY_SQUAD_PANEL, manifest["panels"])
        self.assertFalse((self.skin / "panels" / "team").exists())
        self.run_installer("--remove")
        self.assertEqual((self.skin / install.PANEL_FILES["player"]).read_text(), PLAYER_PANEL)

    def test_upgrade_restores_users_own_squad_panel(self):
        self._legacy_squad_install(existing_panel=True)
        self.run_installer("--auth-file", str(self.auth))
        squad = self.skin / install.LEGACY_SQUAD_PANEL
        self.assertEqual(squad.read_text(), SQUAD_PANEL)
        self.assertFalse(Path(str(squad) + install.BACKUP_SUFFIX).exists())

    def test_failed_install_rolls_back_every_write(self):
        before = self.snapshot()
        real_write = install._atomic_write
        calls = []

        def fail_on_dll(path, data, mode):
            calls.append(path.name)
            if path.name == "winmm.dll":
                raise OSError("disk full")
            real_write(path, data, mode)

        with patch.object(install, "_atomic_write", side_effect=fail_on_dll), \
                patch("sys.stderr"), self.assertRaises(OSError):
            self.run_installer("--auth-file", str(self.auth))
        self.assertIn("winmm.dll", calls)
        self.assertEqual(self.snapshot(), before)
        # A clean retry is possible: no stray backups or manifest block it.
        self.run_installer("--auth-file", str(self.auth))
        self.assertTrue((self.game / "fmface24-install.json").exists())

    def test_failed_removal_rolls_back(self):
        self.run_installer("--auth-file", str(self.auth))
        installed = self.snapshot()
        real_unlink = Path.unlink

        def fail_on_config(path, *args, **kwargs):
            if path.name == "fmface24.ini":
                raise OSError("permission denied")
            return real_unlink(path, *args, **kwargs)

        with patch.object(Path, "unlink", fail_on_config), patch("sys.stderr"), self.assertRaises(OSError):
            self.run_installer("--remove")
        self.assertEqual(self.snapshot(), installed)

    def test_original_manifest_upgrades(self):
        # Reproduce an install made by the first two-panel installer.
        names = [install.PANEL_FILES["player"], install.PANEL_FILES["staff"]]
        for name, kind in zip(names, ("player", "staff")):
            panel = self.skin / name
            Path(str(panel) + install.BACKUP_SUFFIX).write_bytes(panel.read_bytes())
            panel.write_text(skin.modify_panel(panel.read_text(), kind))
        (self.game / "winmm.dll").write_bytes(self.dll.read_bytes())
        (self.game / "fmface24-install.json").write_text(json.dumps({
            "dllSha256": sha(self.dll), "panelSha256": [sha(self.skin / name) for name in names]}))
        self.run_installer("--auth-file", str(self.auth))
        manifest = json.loads((self.game / "fmface24-install.json").read_text())
        self.assertEqual(manifest["version"], 2)
        self.assertEqual(len(manifest["panels"]), 2)
        self.run_installer("--remove")
        self.assertEqual((self.skin / names[0]).read_text(), PLAYER_PANEL)
        self.assertEqual((self.skin / names[1]).read_text(), STAFF_PANEL)

    def test_edited_panel_blocks_reinstall_and_removal(self):
        self.run_installer("--auth-file", str(self.auth))
        (self.skin / install.PANEL_FILES["player"]).write_text(PLAYER_PANEL + "<!-- edit -->")
        for extra in (("--auth-file", str(self.auth)), ("--remove",)):
            with patch("sys.stderr"), self.assertRaises(SystemExit):
                self.run_installer(*extra)



class GameRunningTests(unittest.TestCase):
    def test_a_running_fm26_is_not_taken_for_fm24(self):
        game = Path("/games/steamapps/common/Football Manager 2024")
        fm26 = [(1, r"Z:\games\steamapps\common\Football Manager 26\fm.exe")]
        fm24 = [(2, r"Z:\games\steamapps\common\Football Manager 2024\fm.exe")]
        with patch.object(install, "EXECUTABLE", "fm.exe"):
            with patch.object(install, "running_executables", return_value=fm26):
                self.assertFalse(install.game_running(game))
            with patch.object(install, "running_executables", return_value=fm26 + fm24):
                self.assertTrue(install.game_running(game))


class FindGamePidTests(unittest.TestCase):
    """Why the companion can't reach FM24, in words a player can act on."""

    def find(self, processes, unreadable=(), platform="linux"):
        import live_memory

        def image_base(pid):
            if pid in unreadable:
                raise OSError("no answer")
            return 0x140000000
        with patch.object(live_memory, "running_executables", return_value=processes), \
                patch.object(live_memory, "fm24_image_base", side_effect=image_base), \
                patch.object(live_memory, "EXECUTABLE_SUFFIX", "/fm.exe"), \
                patch.object(sys, "platform", platform):
            return live_memory.find_game_pid()

    def test_messages(self):
        from live_memory import MemoryUnavailable
        game = "C:/Games/FM24/fm.exe"
        self.assertEqual(self.find([(7, "/bin/sh"), (9, game)]), 9)
        with self.assertRaisesRegex(MemoryUnavailable, "isn't running. Start it"):
            self.find([(7, "/bin/sh")])
        with self.assertRaisesRegex(MemoryUnavailable, "More than one FM24"):
            self.find([(8, game), (9, game)])
        with self.assertRaisesRegex(MemoryUnavailable, "isn't answering. Quit FM24 and start it again"):
            self.find([(9, game)], unreadable={9}, platform="darwin")
        with self.assertRaisesRegex(MemoryUnavailable, r"can't read the running FM24 \(no answer\)"):
            self.find([(9, game)], unreadable={9})

if __name__ == "__main__":
    unittest.main()
