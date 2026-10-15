#!/usr/bin/env python3
"""FM24 companion: read the open profile from game memory and generate its portrait.

Requires the FM Face native DLL (fm24/native_menu); there is no X11 code.
"""

from __future__ import annotations

import argparse
import hmac
import json
import secrets
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from xml.etree.ElementTree import ParseError

if sys.platform != "win32" and not sys.platform.startswith("linux"):
    sys.exit("FM Face's FM24 companion runs on Linux (Steam/Proton) and Windows. The macOS version "
             "of FM24 is a separate program that the FM Face DLL and memory reader do not support.")

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from fmface import check_graphics_config, edit_png, generate_png, load_env, profile_prompt, update_prompt
from existing_portrait import find_existing_portrait, game_dir_from_process
from graphics import save_versioned_portrait
from live_memory import MemoryUnavailable, find_game_pid
from native_profile import read_open_profile, reread_profile
from native_dialog import DialogUnavailable, set_progress
from native_reload import request_native_reload
from native_screen import read_open_screen


class NativePortraitProgress:
    """Progress text on FM Face's portrait popup inside FM24 (native DLL).

    The popup is 88 pixels wide, so the texts are one short word.

    The skin reload after a new face rebuilds the popup, which clears it.
    Raises DialogUnavailable from the constructor if FM24 cannot show it.
    """

    def __init__(self, port: int, token: str, text: str):
        self.port, self.token = port, token
        self.failed = False
        set_progress(port, token, "portrait", text)

    def send(self, command: str) -> None:
        if command == "reload":
            self._set("Loading…")

    def _set(self, text: str) -> None:
        try:
            set_progress(self.port, self.token, "portrait", text)
        except (DialogUnavailable, ValueError, OSError):
            pass

    def fail(self) -> None:
        self.failed = True
        self._set("Failed")

    def close(self) -> None:
        self._set("")


def start_progress(handler, text: str):
    """Progress text on the FM Face portrait popup, or None if FM24 can't show it."""
    try:
        return NativePortraitProgress(handler.reload_port, handler.token, text)
    except (DialogUnavailable, ValueError, OSError) as exc:
        print(f"FM24 in-game progress unavailable ({exc}); continuing without it.", flush=True)
        return None


def read_native_profile(*, identity_only: bool = False):
    """The open profile and the biography FM24 shows, from game memory only.

    The native mode's read: no screenshots. The open screen's header must show
    the person read from memory, and both are read twice so navigation during
    capture is refused. ``identity_only`` reads just name, age and club.
    """
    try:
        pid = find_game_pid()
        person_id, profile = read_open_profile(pid, identity_only=identity_only)
        screen = read_open_screen(pid)
        if screen.person_id != person_id:
            raise ValueError(f"The open FM24 screen shows {screen.person_id}, not person {person_id}; "
                             "no image request was made.")
        biography = screen.biography
        if (not identity_only and biography and
                not biography.lower().startswith("no biography is available")):
            profile["biographyHeadline"] = biography
        # The check read uses the header and the known record: no memory search.
        if (read_open_screen(pid) != screen or
                reread_profile(pid, person_id, screen.title, profile["age"], identity_only=identity_only) !=
                (person_id, {key: value for key, value in profile.items() if key != "biographyHeadline"})):
            raise ValueError("The open FM24 profile changed during capture; no image request was made.")
        return person_id, profile
    except (MemoryUnavailable, OSError) as exc:
        raise ValueError(f"Could not read the open FM24 profile from game memory ({exc}); "
                         "no image request was made.") from exc


def existing_portrait(handler, person_id: str, name: str) -> tuple[bytes, str]:
    """The portrait FM24 shows for this person now, for "Update face"."""
    game_dir = handler.game_dir
    if game_dir is None:
        try:
            game_dir = game_dir_from_process(find_game_pid())
        except (MemoryUnavailable, OSError, ValueError):
            game_dir = None
    found = find_existing_portrait(person_id, handler.graphics_dir, game_dir)
    if found is None:
        raise ValueError(f"{name} has no face to update yet. Choose Generate new face instead; "
                         "no image request was made.")
    return found


def reload_portrait(handler) -> None:
    """Reload FM24's skin inside the game through the native DLL."""
    request_native_reload(handler.reload_port, handler.token)


def generate_for_profile(handler, player_id: str, profile: dict) -> dict:
    """Generate, save and reload a portrait for a profile read by ID.

    Used by the in-game squad batch, which reads each player from memory
    instead of opening the profile. Shares the endpoint's in-flight lock.
    """
    with handler.lock:
        if handler.in_flight:
            return {"ok": False, "error": "A portrait is already being generated."}
        handler.in_flight = True
    try:
        prompt = profile_prompt(profile, person_id=player_id,
                                appearance_dir=None if handler.preview_only else handler.graphics_dir)
        if handler.preview_only:
            return {"ok": True, "preview": True, "playerId": player_id, "profile": profile, "prompt": prompt}
        check_graphics_config(handler.graphics_dir)
        env = load_env()
        if not env.get("OPENAI_API_KEY", "").strip():
            raise ValueError("OPENAI_API_KEY is missing from .env.")
        png = generate_png(env, prompt)
        _, previous = save_versioned_portrait(handler.graphics_dir, player_id, png)
        try:
            reload_portrait(handler)
        except (OSError, RuntimeError, TimeoutError) as exc:
            return {"ok": True, "playerId": player_id, "refreshed": False,
                    "warning": f"Portrait saved, but FM24 reload failed: {exc}. Use Preferences > Reload Skin."}
        for old in previous:
            old.unlink(missing_ok=True)
        return {"ok": True, "playerId": player_id, "refreshed": True}
    except (ValueError, OSError, RuntimeError, ParseError) as exc:
        return {"ok": False, "error": str(exc)}
    finally:
        with handler.lock:
            handler.in_flight = False


class Handler(BaseHTTPRequestHandler):
    token = ""
    reload_port = None
    graphics_dir = Path()
    game_dir = None
    preview_only = False
    lock = threading.Lock()
    in_flight = False
    nonces: dict[str, float] = {}

    def log_message(self, format, *args):
        # The URL includes a private callback token; never print request paths.
        pass

    def _local(self) -> bool:
        hosts = self.headers.get_all("Host", [])
        return len(hosts) == 1 and hosts[0].lower() in (
            f"127.0.0.1:{self.server.server_port}", f"localhost:{self.server.server_port}")

    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, status: int, value: dict) -> None:
        self._send(status, json.dumps(value, ensure_ascii=False).encode(), "application/json; charset=utf-8")

    def do_GET(self) -> None:
        if not self._local():
            self.send_error(403)
            return
        url = urlsplit(self.path)
        keys = parse_qs(url.query).get("key", [])
        if url.path != "/fm24" or len(keys) != 1 or not hmac.compare_digest(keys[0], self.token):
            self.send_error(404)
            return
        nonce = secrets.token_urlsafe(24)
        with self.lock:
            now = time.monotonic()
            type(self).nonces = {k: t for k, t in self.nonces.items() if now - t < 600}
            self.nonces[nonce] = now
        mode = "Preview" if self.preview_only else "Generate"
        payload = json.dumps({"key": self.token, "nonce": nonce})
        page = f'''<!doctype html><html lang="en"><meta charset="utf-8">
<meta name="referrer" content="no-referrer"><title>FM Face for FM24</title>
<style>body{{font:16px system-ui;background:#15191d;color:#eee;max-width:660px;margin:3rem auto;padding:0 1rem}}
button{{font:inherit;padding:.6rem 1rem}}pre{{white-space:pre-wrap;overflow-wrap:anywhere}}</style>
<h1>FM Face for FM24</h1><p id="status">Reading the open FM24 profile…</p><pre id="detail"></pre>
<button id="again" hidden>{mode} again</button>
<script>
const data={payload};
const status=document.getElementById('status'),detail=document.getElementById('detail'),again=document.getElementById('again');
async function run(force=false){{
  if(!force && sessionStorage.getItem('fmface-fm24-ran')){{status.textContent='This tab already ran. Return to FM24, or use the button below.';again.hidden=false;return;}}
  sessionStorage.setItem('fmface-fm24-ran','1');
  status.textContent='{mode} in progress…';again.hidden=true;
  try{{const response=await fetch('/fm24/generate',{{method:'POST',headers:{{'Content-Type':'application/json'}},body:JSON.stringify(data)}});
    const result=await response.json();status.textContent=result.ok ? (result.preview ? 'Profile preview ready; no image was generated.' : (result.refreshed ? 'Portrait saved and reloaded in FM24.' : 'Portrait saved; FM24 reload needs attention.')) : 'FM Face could not continue.';
    detail.textContent=result.ok ? (result.preview ? result.prompt : ('Person '+result.playerId+(result.warning ? '\\n'+result.warning : ''))) : result.error;
  }}catch(error){{status.textContent='Could not reach the FM24 companion.';detail.textContent=String(error);}}
  again.hidden=false;
}}
again.onclick=()=>{{sessionStorage.removeItem('fmface-fm24-ran');location.reload();}};run();
</script></html>'''
        self._send(200, page.encode(), "text/html; charset=utf-8")

    def do_POST(self) -> None:
        if not self._local() or urlsplit(self.path).path != "/fm24/generate":
            self.send_error(403)
            return
        origin = self.headers.get("Origin", "")
        if origin and origin != f"http://{self.headers['Host']}":
            self.send_error(403)
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 1 <= size <= 4096:
                raise ValueError("Invalid request size.")
            payload = json.loads(self.rfile.read(size))
            if not isinstance(payload, dict):
                raise ValueError("Invalid FM24 request body.")
            if not hmac.compare_digest(str(payload.get("key", "")), self.token):
                raise ValueError("Invalid FM24 callback token.")
            expected_id = payload.get("expectedPlayerId")
            if expected_id is not None and (not isinstance(expected_id, str) or
                                            not expected_id.isdecimal() or len(expected_id) > 20):
                raise ValueError("Invalid expected FM24 player ID.")
            mode = payload.get("mode", "generate")
            if mode not in ("generate", "update"):
                raise ValueError("Unknown FM24 request mode.")
            nonce = payload.get("nonce")
            with self.lock:
                issued = self.nonces.pop(nonce, None) if isinstance(nonce, str) else None
                if issued is None or time.monotonic() - issued > 600:
                    raise ValueError("This FM24 request has expired. Click Generate face in FM24 again.")
                if self.in_flight:
                    raise ValueError("A portrait is already being generated.")
                type(self).in_flight = True
        except (ValueError, TypeError, json.JSONDecodeError) as exc:
            self._json(400, {"ok": False, "error": str(exc)})
            return
        progress = None
        try:
            if not self.preview_only:
                progress = start_progress(self, "Updating…" if mode == "update" else "Generating…")
            player_id, profile = read_native_profile(identity_only=mode == "update")
            if expected_id is not None and expected_id != player_id:
                raise ValueError(f"The open FM24 profile is now person {player_id}, not the requested person "
                                 f"{expected_id}; no image request was made.")
            reference = source = None
            if mode == "update":
                reference, source = existing_portrait(self, player_id, profile["name"])
                prompt = update_prompt(profile)
            else:
                prompt = profile_prompt(profile, person_id=player_id,
                                        appearance_dir=None if self.preview_only else self.graphics_dir)
            if self.preview_only:
                self._json(200, {"ok": True, "preview": True, "playerId": player_id,
                                 "profile": profile, "prompt": prompt, "reference": source})
                return
            check_graphics_config(self.graphics_dir)
            env = load_env()
            if not env.get("OPENAI_API_KEY", "").strip():
                raise ValueError("OPENAI_API_KEY is missing from .env.")
            png = edit_png(env, prompt, reference) if reference else generate_png(env, prompt)
            _, previous = save_versioned_portrait(self.graphics_dir, player_id, png)
            if progress:
                progress.send("reload")
            try:
                reload_portrait(self)
            except (ValueError, OSError, RuntimeError, TimeoutError) as exc:
                if progress:
                    progress.fail()
                self._json(200, {"ok": True, "playerId": player_id, "refreshed": False,
                                 "warning": f"Portrait saved, but FM24 reload failed: {exc}. Use Preferences > Reload Skin."})
                return
            for old in previous:
                old.unlink(missing_ok=True)
            self._json(200, {"ok": True, "playerId": player_id, "refreshed": True})
        except (ValueError, OSError, RuntimeError, ParseError) as exc:
            if progress:
                progress.fail()
            self._json(422, {"ok": False, "error": str(exc)})
        finally:
            if progress and not progress.failed:
                progress.close()
            with self.lock:
                type(self).in_flight = False


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--auth-file", type=Path, required=True)
    parser.add_argument("--graphics-dir", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8768)
    # Accepted for old scripts; the native menu is the only mode.
    parser.add_argument("--native-menu", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--native-port", type=int, default=8769)
    parser.add_argument("--preview-only", action="store_true", help="Return the prompt without an API request or portrait write")
    args = parser.parse_args()
    if not 1024 <= args.native_port <= 65534:
        parser.error("--native-port must be between 1024 and 65534")
    token = args.auth_file.read_text(encoding="ascii").strip()
    if len(token) < 40:
        parser.error("Invalid FM24 callback token file.")
    graphics_dir = args.graphics_dir.expanduser().resolve()
    if not graphics_dir.is_dir() or graphics_dir.name != "fmface" or graphics_dir.parent.name != "graphics" or graphics_dir.parent.parent.name != "Football Manager 2024":
        parser.error("--graphics-dir must be the existing Football Manager 2024/graphics/fmface directory")
    Handler.token = token
    Handler.graphics_dir = graphics_dir
    Handler.preview_only = args.preview_only
    # The DLL accepts reload, dialog and progress requests on the port after the bridge.
    Handler.reload_port = args.native_port + 1
    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    except OSError as exc:
        parser.exit(1, f"Companion port 127.0.0.1:{args.port} is unavailable ({exc}). "
                       "Is another companion already running?\n")
    from native_listener import start_native_listener
    try:
        start_native_listener(args.port, args.native_port, token, Handler, generate=generate_for_profile)
    except RuntimeError as exc:
        parser.exit(1, f"{exc}\n")
    print(f"FM24 companion listening on 127.0.0.1:{args.port} "
          f"({'preview' if args.preview_only else 'generation'} mode)", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
