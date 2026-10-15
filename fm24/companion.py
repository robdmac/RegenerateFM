#!/usr/bin/env python3
"""FM24 skin companion: capture the open profile and generate its portrait."""

from __future__ import annotations

import argparse
import hmac
import json
import secrets
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from xml.etree.ElementTree import ParseError

from rapidocr import RapidOCR
from Xlib.error import XError

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from fmface import check_graphics_config, generate_png, load_env, profile_prompt
from capture_profile import BASE_SIZE, _biography, read_player_profile
from capture_staff import read_staff_identity, read_staff_profile
from graphics import save_versioned_portrait
from live_memory import MemoryUnavailable, enrich_profile
from native_profile import biography_candidates, choose_biography, read_open_profile
from native_reload import request_native_reload
from read_profile import capture_window, read_identity
from refresh import _activate_game, find_game_window, reload_skin


class ProgressOverlay:
    """Show status over the FM24 portrait while a portrait request runs."""

    def __init__(self, window_id: str):
        self.process = None
        self.failed = False
        try:
            self.process = subprocess.Popen(
                [sys.executable, str(Path(__file__).with_name("overlay.py")),
                 "--window-id", window_id],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                text=True,
            )
        except OSError:
            pass

    def send(self, command: str) -> None:
        if self.process and self.process.poll() is None and self.process.stdin:
            try:
                self.process.stdin.write(command + "\n")
                self.process.stdin.flush()
            except OSError:
                pass

    def fail(self) -> None:
        self.failed = True
        self.send("error")
        timer = threading.Timer(13, self.close)
        timer.daemon = True
        timer.start()

    def close(self) -> None:
        if self.process and self.process.poll() is None:
            self.send("hide")
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.terminate()
        if self.process and self.process.stdin:
            self.process.stdin.close()


def read_visible_profile(window_id: str, engine: RapidOCR):
    """Wait for FM24 attributes to finish drawing after profile navigation."""
    deadline = time.monotonic() + 15
    while True:
        screenshot = capture_window(window_id)
        if screenshot.size != BASE_SIZE:
            raise ValueError("Automatic FM24 reload requires the tested 1854×1011 game window; no image request was made.")
        try:
            return read_staff_profile(screenshot, engine)
        except ValueError as staff_error:
            try:
                return read_player_profile(screenshot, engine)
            except ValueError as player_error:
                if time.monotonic() >= deadline:
                    raise ValueError(f"Could not verify the open FM24 profile. Staff: {staff_error} Player: {player_error}") from player_error
                time.sleep(.5)


def read_prompt_profile(window_id: str, engine: RapidOCR):
    """Read FM24's native profile and biography, with verified screen fallback."""
    try:
        person_id, profile = read_open_profile()
    except (MemoryUnavailable, OSError, ValueError) as exc:
        print(f"FM24 native profile unavailable; using verified screen profile: {exc}", flush=True)
        person_id, profile = read_visible_profile(window_id, engine)
        try:
            profile = enrich_profile(person_id, profile)
        except MemoryUnavailable as error:
            print(f"FM24 live identity unavailable; using verified screen profile: {error}", flush=True)
        return person_id, profile
    # Memory can hold several paragraphs linking to this person (news,
    # later biography paragraphs). Choose the one FM24 is showing; the
    # Verify the screenshot itself: matching native reads alone cannot catch
    # navigation away and back during capture.
    screenshot = capture_window(window_id)
    visible = _biography(screenshot, engine) if screenshot.size == BASE_SIZE else ""
    biography = ""
    if visible:
        visible_id = (read_staff_identity(screenshot, engine)[0] if profile.get("type") == "staff"
                      else read_identity(screenshot, engine).player_id)
        if visible_id != person_id:
            raise ValueError("The open FM24 profile changed during biography capture; no image request was made.")
        biography = choose_biography(biography_candidates(None, profile["name"]), visible)
    if biography and not biography.lower().startswith("no biography is available"):
        profile["biographyHeadline"] = biography
    verified_id, verified_profile = read_open_profile()
    if verified_id != person_id or verified_profile != {key: value for key, value in profile.items()
                                                        if key != "biographyHeadline"}:
        raise ValueError("The open FM24 profile changed during native capture; no image request was made.")
    return person_id, profile


def reload_portrait(handler, player_id: str, staff: bool) -> None:
    """Reload FM24's skin in process when the native DLL is in use."""
    if handler.reload_port:
        try:
            request_native_reload(handler.reload_port, handler.token)
            return
        except (OSError, RuntimeError, TimeoutError) as exc:
            print(f"FM24 in-game reload failed ({exc}); using the Preferences reload instead.", flush=True)
    reload_skin(handler.window_id, player_id, handler.engine, staff=staff)


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
            if not handler.reload_port:
                raise RuntimeError("the in-game reload needs the native menu DLL")
            request_native_reload(handler.reload_port, handler.token)
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
    window_id = ""
    graphics_dir = Path()
    preview_only = False
    engine = None
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
            _activate_game(self.window_id)
            if not self.preview_only:
                progress = ProgressOverlay(self.window_id)
            player_id, profile = read_prompt_profile(self.window_id, self.engine)
            if expected_id is not None and expected_id != player_id:
                raise ValueError(f"The open FM24 profile is now person {player_id}, not the requested person "
                                 f"{expected_id}; no image request was made.")
            prompt = profile_prompt(profile, person_id=player_id,
                                    appearance_dir=None if self.preview_only else self.graphics_dir)
            if self.preview_only:
                self._json(200, {"ok": True, "preview": True, "playerId": player_id,
                                 "profile": profile, "prompt": prompt})
                return
            check_graphics_config(self.graphics_dir)
            env = load_env()
            if not env.get("OPENAI_API_KEY", "").strip():
                raise ValueError("OPENAI_API_KEY is missing from .env.")
            png = generate_png(env, prompt)
            _, previous = save_versioned_portrait(self.graphics_dir, player_id, png)
            if progress:
                progress.send("reload")
            try:
                reload_portrait(self, player_id, profile.get("type") == "staff")
            except (ValueError, OSError, RuntimeError, TimeoutError, XError) as exc:
                if progress:
                    progress.fail()
                self._json(200, {"ok": True, "playerId": player_id, "refreshed": False,
                                 "warning": f"Portrait saved, but FM24 reload failed: {exc}. Use Preferences > Reload Skin."})
                return
            for old in previous:
                old.unlink(missing_ok=True)
            self._json(200, {"ok": True, "playerId": player_id, "refreshed": True})
        except (ValueError, OSError, RuntimeError, ParseError, XError) as exc:
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
    parser.add_argument("--window-id", help="FM24 game X11 window ID (found automatically when omitted)")
    parser.add_argument("--graphics-dir", type=Path, required=True)
    parser.add_argument("--port", type=int, default=8768)
    parser.add_argument("--native-menu", action="store_true",
                        help="Accept events from the FM24 native menu DLL (the default)")
    parser.add_argument("--native-port", type=int, default=8769)
    parser.add_argument("--preview-only", action="store_true", help="Return the prompt without an API request or portrait write")
    parser.add_argument("--direct-clicks", action="store_true",
                        help="Use the X11 portrait and Overview menu overlays instead of the native menu DLL")
    parser.add_argument("--menu-overlay", action="store_true",
                        help="Keep the Overview menu overlays enabled (the default with --direct-clicks)")
    parser.add_argument("--no-menu-overlay", action="store_true",
                        help="Disable player and squad Overview menu rows")
    args = parser.parse_args()
    if args.native_menu and args.direct_clicks:
        parser.error("Use either --native-menu or --direct-clicks")
    if not 1024 <= args.native_port <= 65534:
        parser.error("--native-port must be between 1024 and 65534")
    if args.menu_overlay and not args.direct_clicks:
        parser.error("--menu-overlay requires --direct-clicks")
    if args.menu_overlay and args.no_menu_overlay:
        parser.error("--menu-overlay and --no-menu-overlay cannot be combined")
    # FM24 draws the menus itself once the native DLL is installed; the X11
    # overlays remain only for --direct-clicks.
    args.native_menu = not args.direct_clicks
    menu_overlay = args.direct_clicks and not args.no_menu_overlay
    token = args.auth_file.read_text(encoding="ascii").strip()
    if len(token) < 40:
        parser.error("Invalid FM24 callback token file.")
    graphics_dir = args.graphics_dir.expanduser().resolve()
    if not graphics_dir.is_dir() or graphics_dir.name != "fmface" or graphics_dir.parent.name != "graphics" or graphics_dir.parent.parent.name != "Football Manager 2024":
        parser.error("--graphics-dir must be the existing Football Manager 2024/graphics/fmface directory")
    if args.direct_clicks:
        skin_panels = graphics_dir.parent.parent / "skins" / "fmface" / "panels"
        player_panel = skin_panels / "player" / "player overview panel.xml"
        staff_panel = skin_panels / "non_player" / "non player profile.xml"
        if not player_panel.is_file():
            parser.error("--direct-clicks requires the FM Face skin in Football Manager 2024/skins/fmface")
        for panel in (player_panel, staff_panel):
            if panel.is_file() and 'event_id="OURL"' in panel.read_text(encoding="utf-8"):
                parser.error("The installed FM Face skin still opens a browser; rebuild it before using --direct-clicks")
    if not args.window_id:
        try:
            args.window_id = find_game_window()
        except (ValueError, OSError) as exc:
            parser.exit(1, f"{exc}\n")
        print(f"Using FM24 game window {args.window_id}.", flush=True)
    Handler.token = token
    Handler.window_id = args.window_id
    Handler.graphics_dir = graphics_dir
    Handler.preview_only = args.preview_only
    Handler.engine = RapidOCR()
    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    except OSError as exc:
        parser.exit(1, f"Companion port 127.0.0.1:{args.port} is unavailable ({exc}). "
                       "Is another companion already running?\n")
    if args.native_menu:
        # The DLL accepts skin reload requests on the port after the bridge.
        Handler.reload_port = args.native_port + 1
        from native_listener import start_native_listener
        try:
            start_native_listener(args.window_id, args.port, args.native_port, token, Handler.engine, Handler,
                                  generate=generate_for_profile)
        except RuntimeError as exc:
            parser.exit(1, f"{exc}\n")
    if args.direct_clicks:
        from click_listener import start_listener
        start_listener(args.window_id, args.port, token, Handler.engine, Handler,
                       menu_overlay=menu_overlay, portrait_menu=True)
    print(f"FM24 companion listening on 127.0.0.1:{args.port} "
          f"({'preview' if args.preview_only else 'generation'} mode, "
          f"{'native menu' if args.native_menu else 'direct clicks'}"
          f"{' + Overview menu overlays' if menu_overlay else ''}"
          f"{' + portrait menu' if args.direct_clicks else ''})", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
