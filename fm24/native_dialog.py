"""Show FM24's own dialogs through the native DLL instead of desktop windows."""

from __future__ import annotations

import base64
import socket
import threading
import time

MAX_TITLE = 250
MAX_TEXT = 1000


class DialogUnavailable(RuntimeError):
    """The DLL could not show the dialog; the caller should use its fallback."""


def _packet(serial: int, token: str, kind: str, title: str, text: str) -> bytes:
    encoded = []
    for value, limit in ((title, MAX_TITLE), (text, MAX_TEXT)):
        data = " ".join(value.split()).encode("utf-8")
        if not data or len(data) > limit or b"\0" in data:
            raise ValueError("FM24 dialog text is empty or too long.")
        encoded.append(base64.b64encode(data).decode("ascii"))
    return f"FMFACE24D 1 {serial} {token} {kind} {encoded[0]} {encoded[1]}".encode("ascii")


def show_dialog(port: int, token: str, kind: str, title: str, text: str, *,
                timeout: float | None = None, clock=time.monotonic,
                serial: int | None = None) -> str:
    """Show a dialog in FM24 and return "yes", "no", or "shown".

    ``kind`` "confirm" (Yes/No) waits for the answer, up to ``timeout`` seconds
    (forever when None). "message" (OK) returns "shown" once it is on screen.
    Raises DialogUnavailable when FM24 cannot show it, so the caller can fall
    back to a desktop dialog.
    """
    if kind not in ("confirm", "message", "progress"):
        raise ValueError("Unknown FM24 dialog kind.")
    serial = serial or time.time_ns() // 1_000_000
    packet = _packet(serial, token, kind, title, text)
    prefix = f"FMFACE24D {serial} "
    replied = shown = False
    start = clock()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.connect(("127.0.0.1", port))
        connection.settimeout(.5)
        while True:
            waited = clock() - start
            if not replied and waited > 5:
                raise DialogUnavailable("The FM24 native DLL did not answer the dialog request.")
            if not shown and waited > 15:
                raise DialogUnavailable("FM24 did not show the dialog.")
            if shown and timeout is not None and waited > timeout:
                raise TimeoutError("No answer to the FM24 dialog.")
            try:
                connection.send(packet)
                reply = connection.recv(128).decode("ascii", "replace")
            except ConnectionRefusedError:
                raise DialogUnavailable("The FM24 native DLL is not listening.") from None
            except socket.timeout:
                continue
            if not reply.startswith(prefix):
                continue
            replied = True
            status = reply[len(prefix):]
            if status in ("busy", "failed"):
                raise DialogUnavailable(f"FM24 could not show the dialog ({status}).")
            if status in ("yes", "no"):
                return status
            if status == "shown":
                if kind != "confirm":
                    return status
                shown = True
            time.sleep(.25)


def set_progress(port: int, token: str, target: str, text: str, *, clock=time.monotonic,
                 serial: int | None = None) -> None:
    """Set in-game progress text: on the portrait ("portrait") or in the open
    FM Face dialog ("dialog"). Empty text clears it. Raises DialogUnavailable
    when FM24 could not apply it, so the caller can fall back."""
    if target not in ("portrait", "dialog", "finish"):
        raise ValueError("Unknown FM24 progress target.")
    data = " ".join(text.split()).encode("utf-8")
    if len(data) > MAX_TITLE or b"\0" in data:
        raise ValueError("FM24 progress text is too long.")
    encoded = base64.b64encode(data).decode("ascii") if data else "-"
    serial = serial or time.time_ns() // 1_000_000
    packet = f"FMFACE24P 1 {serial} {token} {target} {encoded}".encode("ascii")
    prefix = f"FMFACE24P {serial} "
    start = clock()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.connect(("127.0.0.1", port))
        connection.settimeout(.5)
        while clock() - start < 5:
            try:
                connection.send(packet)
                reply = connection.recv(128).decode("ascii", "replace")
            except ConnectionRefusedError:
                raise DialogUnavailable("The FM24 native DLL is not listening.") from None
            except socket.timeout:
                continue
            if not reply.startswith(prefix):
                continue
            status = reply[len(prefix):]
            if status == "done":
                return
            if status in ("busy", "failed"):
                raise DialogUnavailable(f"FM24 could not set the progress text ({status}).")
            time.sleep(.1)
    raise DialogUnavailable("FM24 did not apply the progress text.")


def ask_key(port: int, token: str, text: str, *, timeout: float = 1800, clock=time.monotonic,
            serial: int | None = None) -> str | None:
    """Ask for the OpenAI key in FM24 (the macOS library's key prompt) and
    return what was typed, or None for Cancel. ``text`` may have line breaks.
    Raises DialogUnavailable when the library can't show the prompt."""
    data = text.strip().encode("utf-8")
    if not data or len(data) > MAX_TEXT or b"\0" in data:
        raise ValueError("FM24 key prompt text is empty or too long.")
    serial = serial or time.time_ns() // 1_000_000
    packet = f"FMFACE24K 1 {serial} {token} {base64.b64encode(data).decode('ascii')}".encode("ascii")
    prefix = f"FMFACE24K {serial} "
    replied = False
    start = clock()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.connect(("127.0.0.1", port))
        connection.settimeout(.5)
        while True:
            waited = clock() - start
            if not replied and waited > 5:
                raise DialogUnavailable("The FM24 native library did not answer the key prompt request.")
            if waited > timeout:
                raise TimeoutError("No answer to FM24's key prompt.")
            try:
                connection.send(packet)
                reply = connection.recv(1024).decode("ascii", "replace")
            except ConnectionRefusedError:
                raise DialogUnavailable("The FM24 native library is not listening.") from None
            except socket.timeout:
                continue
            if not reply.startswith(prefix):
                continue
            replied = True
            status = reply[len(prefix):]
            if status == "busy":
                raise DialogUnavailable("FM24 is already showing the key prompt.")
            if status == "cancel":
                return None
            if status.startswith("key "):
                try:
                    return base64.b64decode(status[4:], validate=True).decode("utf-8")
                except ValueError:
                    return ""
            time.sleep(.25)


def notify(port: int | None, token: str, text: str, fallback) -> None:
    """Show text in an FM24 OK dialog, or call ``fallback(text)`` if FM24 can't."""
    if port:
        try:
            show_dialog(port, token, "message", "FM Face", _clip(text))
            return
        except (DialogUnavailable, ValueError, OSError):
            pass
    fallback(text)


def _clip(text: str) -> str:
    data = " ".join(text.split()).encode("utf-8")
    return text if len(data) <= MAX_TEXT else data[:MAX_TEXT - 3].decode("utf-8", "ignore") + "…"


class NativeProgress:
    """Squad batch progress in an FM24 dialog whose one button is "Stop after
    this face"; the same interface as the X11 panel (stopped, update, close).

    FM24's skin reload after each face closes the dialog, so the next update
    opens a new one. Raises DialogUnavailable from the constructor if FM24
    cannot show it, so the caller can use the X11 panel instead.
    """

    def __init__(self, port: int, token: str, title: str, text: str):
        self.port, self.token, self.title = port, token, title
        self.stopped = threading.Event()
        self._lock = threading.Lock()
        self._closed = threading.Event()
        self._packet = self._prefix = None
        self._show(text)
        threading.Thread(target=self._watch, daemon=True).start()

    def _exchange(self, packet: bytes, prefix: str) -> str | None:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
            connection.connect(("127.0.0.1", self.port))
            connection.settimeout(.5)
            try:
                connection.send(packet)
                reply = connection.recv(128).decode("ascii", "replace")
            except (ConnectionRefusedError, socket.timeout):
                return None
        return reply[len(prefix):] if reply.startswith(prefix) else None

    def _show(self, text: str) -> None:
        serial = time.time_ns() // 1_000_000
        packet = _packet(serial, self.token, "progress", self.title, _clip(text))
        prefix = f"FMFACE24D {serial} "
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            status = self._exchange(packet, prefix)
            if status == "shown":
                self._packet, self._prefix = packet, prefix
                return
            if status in ("busy", "failed", "closed"):
                break
            time.sleep(.25)
        raise DialogUnavailable("FM24 did not show the progress dialog.")

    def _watch(self) -> None:
        while not self._closed.wait(.5):
            with self._lock:
                status = self._exchange(self._packet, self._prefix) if self._packet else None
            if status == "yes":
                self.stopped.set()
                return

    def update(self, message: str, percent: int | None = None) -> None:
        del percent  # FM24's dialog has no progress bar; the text carries the count.
        if self.stopped.is_set() or self._closed.is_set():
            return
        with self._lock:
            try:
                set_progress(self.port, self.token, "dialog", message)
            except (DialogUnavailable, ValueError):
                # Gone: answered (Stop), or closed by a skin reload.
                if self._packet and self._exchange(self._packet, self._prefix) == "yes":
                    self.stopped.set()
                    return
                try:
                    self._show(message)
                except DialogUnavailable:
                    pass

    def finish(self, text: str, fallback) -> None:
        """Turn the progress dialog into an OK message with ``text``, or show
        a new message if FM24 has closed it (a skin reload or Stop)."""
        self._closed.set()
        with self._lock:
            try:
                if self.stopped.is_set():
                    raise DialogUnavailable("the progress dialog was answered")
                set_progress(self.port, self.token, "finish", _clip(text))
            except (DialogUnavailable, ValueError):
                notify(self.port, self.token, text, fallback)

    def close(self) -> None:
        self._closed.set()
