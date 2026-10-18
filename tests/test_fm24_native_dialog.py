"""FM24 native dialog/progress client against a fake DLL listener."""

import base64
import socket
import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
import layout  # noqa: E402  The fixtures are records from the Windows fm.exe.
layout.LAYOUT.update(layout.WINDOWS)
import native_dialog

TOKEN = "k" * 43


class FakeDll:
    """Answers FMFACE24D/P packets like the DLL: queued, then the scripted states."""

    def __init__(self, test, statuses):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(("127.0.0.1", 0))
        self.port = self.socket.getsockname()[1]
        self.statuses = statuses if callable(statuses) else list(statuses)
        self.packets = []
        self.done = False
        threading.Thread(target=self.serve, daemon=True).start()
        test.addCleanup(self.close)

    def serve(self):
        self.socket.settimeout(.2)
        while not self.done:
            try:
                packet, peer = self.socket.recvfrom(4096)
            except (socket.timeout, OSError):
                continue
            fields = packet.decode().split()
            self.packets.append(fields)
            if fields[3] != TOKEN:
                continue
            if callable(self.statuses):
                status = self.statuses(fields)
            else:
                status = self.statuses.pop(0) if len(self.statuses) > 1 else self.statuses[0]
            self.socket.sendto(f"{fields[0]} {fields[2]} {status}".encode(), peer)

    def close(self):
        self.done = True
        self.socket.close()


class NativeDialogTests(unittest.TestCase):
    def test_confirm_waits_for_the_answer_and_sends_text_as_base64(self):
        dll = FakeDll(self, ["queued", "shown", "shown", "yes"])
        self.assertEqual(native_dialog.show_dialog(dll.port, TOKEN, "confirm", "Generate faces?",
                                                   "4 of 29 players have no face."), "yes")
        magic, version, serial, token, kind, title, text = dll.packets[0]
        self.assertEqual((magic, version, kind), ("FMFACE24D", "1", "confirm"))
        self.assertEqual(base64.b64decode(text).decode(), "4 of 29 players have no face.")
        # Polling repeats the same serial, so the DLL never opens a second dialog.
        self.assertEqual({fields[2] for fields in dll.packets}, {serial})

    def test_key_prompt_returns_the_typed_key_or_none(self):
        typed = base64.b64encode(b"sk-typed").decode()
        dll = FakeDll(self, ["shown", "shown", f"key {typed}"])
        self.assertEqual(native_dialog.ask_key(dll.port, TOKEN, "Paste it.\n\nKept on this Mac."), "sk-typed")
        magic, version, serial, token, text = dll.packets[0]
        self.assertEqual((magic, version), ("FMFACE24K", "1"))
        # Line breaks are kept: the prompt shows paragraphs.
        self.assertEqual(base64.b64decode(text).decode(), "Paste it.\n\nKept on this Mac.")
        self.assertEqual({fields[2] for fields in dll.packets}, {serial})
        self.assertIsNone(native_dialog.ask_key(FakeDll(self, ["shown", "cancel"]).port, TOKEN, "Paste it."))
        with self.assertRaises(native_dialog.DialogUnavailable):
            native_dialog.ask_key(FakeDll(self, ["busy"]).port, TOKEN, "Paste it.")

    def test_message_returns_once_shown(self):
        dll = FakeDll(self, ["queued", "shown"])
        self.assertEqual(native_dialog.show_dialog(dll.port, TOKEN, "message", "FM Face", "Done."), "shown")

    def test_busy_or_failed_dll_means_use_the_fallback(self):
        for status in ("busy", "failed"):
            dll = FakeDll(self, [status])
            with self.assertRaises(native_dialog.DialogUnavailable):
                native_dialog.show_dialog(dll.port, TOKEN, "confirm", "Title", "Text")
        fallback = []
        dll = FakeDll(self, ["busy"])
        native_dialog.notify(dll.port, TOKEN, "Error text", fallback.append)
        self.assertEqual(fallback, ["Error text"])
        native_dialog.notify(None, TOKEN, "No DLL", fallback.append)
        self.assertEqual(fallback[-1], "No DLL")

    def test_invalid_text_is_refused_before_sending(self):
        with self.assertRaises(ValueError):
            native_dialog.show_dialog(1, TOKEN, "confirm", "", "Text")
        with self.assertRaises(ValueError):
            native_dialog.show_dialog(1, TOKEN, "confirm", "Title", "x" * 2000)
        with self.assertRaises(ValueError):
            native_dialog.show_dialog(1, TOKEN, "shell", "Title", "Text")

    def test_progress_text_and_clearing(self):
        dll = FakeDll(self, ["queued", "done"])
        native_dialog.set_progress(dll.port, TOKEN, "portrait", "Generating face…")
        self.assertEqual(dll.packets[0][:2] + dll.packets[0][4:5], ["FMFACE24P", "1", "portrait"])
        self.assertEqual(base64.b64decode(dll.packets[0][5]).decode(), "Generating face…")
        native_dialog.set_progress(dll.port, TOKEN, "portrait", "")
        self.assertEqual(dll.packets[-1][5], "-")
        dll = FakeDll(self, ["failed"])
        with self.assertRaises(native_dialog.DialogUnavailable):
            native_dialog.set_progress(dll.port, TOKEN, "dialog", "2 of 4")

    def test_squad_progress_dialog_reports_stop(self):
        dll = FakeDll(self, ["queued", "shown", "shown", "yes"])
        progress = native_dialog.NativeProgress(dll.port, TOKEN, "FM Face squad faces", "Generating A (1/2)")
        self.addCleanup(progress.close)
        self.assertTrue(progress.stopped.wait(5))
        self.assertEqual(dll.packets[0][4], "progress")
        # Answered (Stop), so the result needs a new message, not the old dialog.
        dll.statuses = ["shown"]
        fallback = []
        progress.finish("Generated 1 squad face.", fallback.append)
        self.assertEqual(dll.packets[-1][4], "message")
        self.assertEqual(fallback, [])

    def test_update_after_stop_does_not_reopen_the_dialog(self):
        answered = threading.Event()
        dll = FakeDll(self, lambda fields: ("failed" if fields[0] == "FMFACE24P" else
                                            "yes" if answered.is_set() else "shown"))
        progress = native_dialog.NativeProgress(dll.port, TOKEN, "FM Face squad faces", "Generating A (1/2)")
        self.addCleanup(progress.close)
        answered.set()  # Stop chosen; the dialog is gone, so updating it fails
        progress.update("Loaded A (1/2)")
        self.assertTrue(progress.stopped.is_set())
        self.assertEqual(len({fields[2] for fields in dll.packets if fields[0] == "FMFACE24D"}), 1)

    def test_open_progress_dialog_becomes_the_result(self):
        dll = FakeDll(self, ["queued", "shown"])
        progress = native_dialog.NativeProgress(dll.port, TOKEN, "FM Face squad faces", "Generating A (1/1)")
        dll.statuses = ["done"]
        progress.finish("Generated 1 squad face.", lambda text: self.fail("fallback used"))
        self.assertEqual(dll.packets[-1][:1] + dll.packets[-1][4:5], ["FMFACE24P", "finish"])


if __name__ == "__main__":
    unittest.main()
