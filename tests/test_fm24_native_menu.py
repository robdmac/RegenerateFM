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
        self.server = native_listener.start_native_listener("0x1", 1234, 0, KEY, None, None, **options)
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

        def generate(port, token, handler, *, expected_id=None):
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
        with patch.object(native_listener, "run_native_squad_batch", side_effect=lambda *args: done.set()) as batch:
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

    def test_occupied_bridge_port_is_a_clear_startup_error(self):
        holder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        holder.bind(("127.0.0.1", 0))
        try:
            with self.assertRaisesRegex(RuntimeError, "another companion"):
                native_listener.start_native_listener("0x1", 1, holder.getsockname()[1], KEY, None, None)
        finally:
            holder.close()


class NativeSkinTests(unittest.TestCase):
    def test_squad_panel_is_no_longer_modified(self):
        # The squad command is an item in FM24's own Squad menu (added by the DLL).
        with self.assertRaisesRegex(ValueError, "Unknown"):
            skin.modify_panel(SQUAD_PANEL, "squad")
        modified = skin.modify_panel(PLAYER_PANEL, "player")
        self.assertIn('select_event="FMFG"', modified)
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
            self.assertEqual(destination.stat().st_mode & 0o777, 0o600)

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


if __name__ == "__main__":
    unittest.main()
