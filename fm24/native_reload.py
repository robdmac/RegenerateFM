"""Ask the FM24 native DLL to reload the skin inside the game."""

from __future__ import annotations

import socket
import time


def request_native_reload(port: int, token: str, *, timeout: float = 70,
                          clock=time.monotonic, serial: int | None = None) -> None:
    """Return once FM24 reports the reload finished; raise if it did not.

    The DLL replies "queued" until its UI thread has completed FM24's own
    reload-current-skin command. Repeating the same serial polls for the result
    and never starts a second reload.
    """
    serial = serial or time.time_ns() // 1_000_000
    packet = f"FMFACE24R 1 {serial} {token}".encode()
    prefix = f"FMFACE24R {serial} "
    deadline = clock() + timeout
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.connect(("127.0.0.1", port))
        connection.settimeout(.5)
        answered = False
        while clock() < deadline:
            try:
                connection.send(packet)
                reply = connection.recv(128).decode("ascii", "replace")
            except ConnectionRefusedError:
                # Nothing listening yet (ICMP unreachable); FM24 may be starting.
                time.sleep(.5)
                continue
            except socket.timeout:
                continue
            if not reply.startswith(prefix):
                continue
            status = reply[len(prefix):]
            answered = True
            if status == "done":
                return
            if status == "failed":
                raise RuntimeError("FM24 could not reload its skin in game")
            if status == "busy":
                raise RuntimeError("FM24 is already reloading its skin")
            time.sleep(.25)
    raise TimeoutError("FM24 did not finish reloading its skin" if answered else
                       "The FM24 native DLL did not answer the reload request")
