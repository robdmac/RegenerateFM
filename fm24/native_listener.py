"""Authenticated loopback bridge from FM24's native skin menu to the companion."""

from __future__ import annotations

import hmac
import socket
import threading
import time

from companion_client import describe_result, request_generation
from native_dialog import notify as native_notify
from squad_batch import run_native_squad_batch

# The DLL reports the person ID shown in the clicked screen's header, read from
# FM24's memory inside the menu callback, and the click time. Without screen
# IDs the macOS library reports "r<reference>" instead: the screen's object
# browser reference, which ``resolve`` turns into the person's ID. A command older
# than this is refused as defence in depth; the person binding is what prevents
# a delayed command from targeting a newly opened profile.
MAX_CLICK_AGE = .75
CLOCK_TOLERANCE = .25


def _print_error(message: str) -> None:
    print(f"RegenerateFM: {message}", flush=True)


def _person_field(value: str) -> bool:
    return value.isdecimal() or (value[:1] == "r" and value[1:].isdecimal())


def start_native_listener(port, bridge_port, token, handler, *,
                          notify=None, clock=time.time, generate=None, has_face=None, resolve=None,
                          key_ready=None):
    if notify is None:
        # FM24's own OK dialog through the DLL, else the companion terminal.
        def notify(message):
            native_notify(getattr(handler, "reload_port", None), token, message, _print_error)
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        server.bind(("127.0.0.1", bridge_port))
    except OSError as exc:
        server.close()
        raise RuntimeError(f"FM24 native bridge port 127.0.0.1:{bridge_port} is unavailable ({exc}). "
                           "Is another companion already running?") from exc
    server.settimeout(.5)
    busy = threading.Lock()
    # Per game run (the DLL's start time, not the process ID, which Wine
    # reuses after a restart): last sequence and the reply sent for it. The DLL
    # retransmits an unanswered click with the same sequence; it gets the same
    # reply and never starts a second request.
    last_reply: dict[str, tuple[int, bytes]] = {}

    def process(action, bound_id):
        try:
            if action == "squad":
                # Roster, portrait state and prompts come from game memory.
                run_native_squad_batch(handler,
                                       lambda person_id, profile: generate(handler, person_id, profile),
                                       describe_result, key_ready=key_ready)
                return
            # The companion reads the open profile from memory and refuses the
            # request unless it is still this person.
            if not bound_id:
                raise ValueError("FM24 did not report which person's menu was chosen; "
                                 "no image request was made.")
            # FM24 for macOS asks for a missing key first, before the request.
            if key_ready is not None and not key_ready():
                print("No OpenAI key was saved; no image request was made.", flush=True)
                return
            if bound_id.startswith("r"):
                if resolve is None:
                    raise ValueError("Enable screen IDs in FM24's preferences; no image request was made.")
                bound_id = resolve(int(bound_id[1:]), wait=True)
            result = request_generation(port, token, handler, expected_id=bound_id,
                                        mode="update" if action == "update" else "generate")
            print(describe_result(result), flush=True)
            if result.get("shown"):
                pass  # Already in FM24's progress dialog.
            elif not result.get("ok") and not result.get("cancelled"):
                notify(result.get("error", "RegenerateFM could not generate this portrait."))
            elif not result.get("preview") and not result.get("refreshed"):
                notify(result.get("warning", "The portrait was saved, but FM24 did not reload it."))
        except Exception as exc:
            print(f"FM24 native menu failed: {exc}", flush=True)
            notify(str(exc))
        finally:
            busy.release()

    def answer_face(fields, peer):
        """"REGENERATEFM24F 1 <serial> <person> <token>": does the person shown in
        FM24's portrait popup have a face? The DLL greys out "Update face"
        on "no" and leaves it enabled without an answer."""
        magic, version, serial, person, key = fields
        if (peer[0] != "127.0.0.1" or version != "1" or not serial.isdecimal() or
                not _person_field(person) or not hmac.compare_digest(key, token) or has_face is None):
            return
        try:
            if person.startswith("r"):
                if resolve is None:
                    return
                person = resolve(int(person[1:]), wait=False)
            answer = "yes" if has_face(person) else "no"
        except (OSError, ValueError, RuntimeError) as exc:
            print(f"FM24 face check failed (Update face stays enabled): {exc}", flush=True)
            return
        server.sendto(f"REGENERATEFM24F {serial} {answer}".encode(), peer)

    def handle(packet, peer):
        fields = packet.decode("ascii").split()
        if fields and fields[0] == "REGENERATEFM24F":
            answer_face(fields, peer)
            return
        magic, version, pid, serial, action, clicked, person, key = fields
        if (peer[0] != "127.0.0.1" or magic != "REGENERATEFM24" or version != "5" or
                not pid.isdecimal() or not serial.isdecimal() or not clicked.isdecimal() or
                not _person_field(person) or
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
        reply = f"REGENERATEFM24 ACK {serial} {status}".encode()
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
