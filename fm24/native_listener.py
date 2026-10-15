"""Authenticated loopback bridge from FM24's native skin menu to the companion."""

from __future__ import annotations

import hmac
import socket
import subprocess
import threading
import time

from companion_client import describe_result, request_generation
from native_dialog import notify as native_notify
from squad_batch import run_native_squad_batch

# The DLL reports the person ID shown in the clicked screen's header, read from
# FM24's memory inside the menu callback, and the click time. A command older
# than this is refused as defence in depth; the person binding is what prevents
# a delayed command from targeting a newly opened profile.
MAX_CLICK_AGE = .75
CLOCK_TOLERANCE = .25


def _notify_error(message: str) -> None:
    try:
        subprocess.Popen(["zenity", "--error", "--title=FM Face", "--no-markup", "--text=" + message],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        pass


def start_native_listener(window_id, port, bridge_port, token, engine, handler, *,
                          notify=None, clock=time.time, generate=None):
    if notify is None:
        # FM24's own OK dialog through the DLL, else a zenity window.
        def notify(message):
            native_notify(getattr(handler, "reload_port", None), token, message, _notify_error)
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        server.bind(("127.0.0.1", bridge_port))
    except OSError as exc:
        server.close()
        raise RuntimeError(f"FM24 native bridge port 127.0.0.1:{bridge_port} is unavailable ({exc}). "
                           "Is another companion already running?") from exc
    server.settimeout(.5)
    busy = threading.Lock()
    # Per game process: last sequence and the reply sent for it. The DLL
    # retransmits an unanswered click with the same sequence; it gets the same
    # reply and never starts a second request.
    last_reply: dict[str, tuple[int, bytes]] = {}

    def process(action, bound_id):
        try:
            if action == "squad":
                # Roster, portrait state and prompts come from game memory.
                run_native_squad_batch(window_id, handler,
                                       lambda person_id, profile: generate(handler, person_id, profile),
                                       describe_result)
                return
            # The companion reads the open profile from memory and refuses the
            # request unless it is still this person.
            if not bound_id:
                raise ValueError("FM24 did not report which person's menu was chosen; "
                                 "no image request was made.")
            result = request_generation(port, token, handler, expected_id=bound_id,
                                        mode="update" if action == "update" else "generate")
            print(describe_result(result), flush=True)
            if not result.get("ok"):
                notify(result.get("error", "FM Face could not generate this portrait."))
            elif not result.get("preview") and not result.get("refreshed"):
                notify(result.get("warning", "The portrait was saved, but FM24 did not reload it."))
        except Exception as exc:
            print(f"FM24 native menu failed: {exc}", flush=True)
            notify(str(exc))
        finally:
            busy.release()

    def handle(packet, peer):
        magic, version, pid, serial, action, clicked, person, key = packet.decode("ascii").split()
        if (peer[0] != "127.0.0.1" or magic != "FMFACE24" or version != "5" or
                not pid.isdecimal() or not serial.isdecimal() or not clicked.isdecimal() or
                not person.isdecimal() or
                action not in ("profile", "update", "squad") or not hmac.compare_digest(key, token)):
            return
        sequence = int(serial)
        previous, reply = last_reply.get(pid, (0, b""))
        if sequence == previous:
            server.sendto(reply, peer)
            return
        if sequence < previous:
            return
        # Bound state even if a local client with the token sends new process IDs.
        if len(last_reply) >= 32 and pid not in last_reply:
            last_reply.clear()
        age = clock() - int(clicked) / 1000
        stale = not -CLOCK_TOLERANCE <= age <= MAX_CLICK_AGE
        accepted = not stale and busy.acquire(blocking=False)
        status = "stale" if stale else "accepted" if accepted else "busy"
        if stale:
            print(f"FM24 native {action} command arrived {age:.2f}s after the click; "
                  "refused because the screen may have changed. No image request was made.", flush=True)
        reply = f"FMFACE24 ACK {serial} {status}".encode()
        last_reply[pid] = (sequence, reply)
        try:
            server.sendto(reply, peer)
        except OSError as exc:
            # The DLL retransmits and receives the cached reply; the accepted
            # click must still run and release the busy lock.
            print(f"FM24 native bridge reply failed (continuing): {exc}", flush=True)
        if accepted:
            bound_id = person if action in ("profile", "update") and person != "0" else None
            try:
                threading.Thread(target=process, args=(action, bound_id), daemon=True).start()
            except RuntimeError as exc:
                busy.release()
                message = f"Could not start the FM24 face request worker: {exc}; no image request was made."
                print(message, flush=True)
                notify(message)

    def receive():
        while server.fileno() >= 0:
            try:
                packet, peer = server.recvfrom(512)
            except socket.timeout:
                continue
            except OSError as exc:
                if server.fileno() < 0:
                    break
                print(f"FM24 native bridge receive error (continuing): {exc}", flush=True)
                continue
            try:
                handle(packet, peer)
            except (ValueError, UnicodeError):
                continue
            except OSError as exc:
                print(f"FM24 native bridge reply failed (continuing): {exc}", flush=True)

    threading.Thread(target=receive, name="fm24-native-bridge", daemon=True).start()
    print(f"FM24 native menu bridge listening on 127.0.0.1:{bridge_port}.", flush=True)
    return server
