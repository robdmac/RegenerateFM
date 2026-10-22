#!/usr/bin/env python3
"""FM24 companion: read the open profile from game memory and generate its portrait.

Requires RegenerateFM's native library in FM24 (fm24/native_menu: the DLL on
Windows and Proton, libregeneratefm24.dylib on macOS); there is no X11 code.
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

if sys.platform not in ("win32", "darwin") and not sys.platform.startswith("linux"):
    sys.exit("RegenerateFM's FM24 companion runs on Linux (Steam/Proton), Windows and macOS.")

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from regeneratefm import ROOT, api_base, check_graphics_config, edit_png, generate_png, load_env, profile_prompt, update_prompt
from existing_portrait import find_existing_portrait, game_dir_from_process, has_existing_portrait
from graphics import save_versioned_portrait
from layout import LAYOUT
from live_memory import MemoryUnavailable, find_game_pid
from native_profile import read_open_profile, reread_profile
from native_dialog import DialogUnavailable, ask_key, clip_text, set_progress, show_dialog
from native_reload import request_native_reload
from native_screen import read_open_screen
from person_handles import build_in_background, person_for_reference


MISSING_KEY = ("RegenerateFM has no OpenAI key. Close and restart the companion to enter it, "
               "or add OPENAI_API_KEY to .env.")


REJECTED_KEY = (" Close the companion and start it again to enter a new key (on Windows, a key in .env "
                "takes priority over the saved one).")
# On macOS FM24 asks for the key itself (ensure_key).
REJECTED_KEY_IN_GAME = " Choose the face command again to enter a new key."
NO_KEY_SAVED = "No OpenAI key was saved, so no image request was made."


def error_text(exc: Exception, handler=None) -> str:
    """The error for FM24's dialog; a rejected key says how to replace it."""
    text = str(exc)
    if "HTTP 401" not in text:
        return text
    if handler is not None and (_asks_in_game(handler) or _asks_in_window(handler)) and not _env_key():
        _handler_class(handler).key_rejected = True
        return text + REJECTED_KEY_IN_GAME
    return text + REJECTED_KEY


def _api(key: str | None = None) -> str:
    """The image API address ``key`` is checked against (regeneratefm.api_base):
    the credits service's for a RegenerateFM credits token."""
    return api_base(load_env(), key)


# What the key prompts return for Buy credits / Sign in (never a valid key):
# key_dialog.CREDITS_CHOICE, and key_prompt_macos.m's CREDITS_CHOICE.
CREDITS_CHOICE = "regeneratefm:credits"


def _tell(handler, text: str) -> None:
    """A message in FM24's own dialog, or the companion's log if FM24 can't show it."""
    print(text, flush=True)
    if handler.reload_port:
        try:
            show_dialog(handler.reload_port, handler.token, "message", "RegenerateFM credits", clip_text(text))
        except (DialogUnavailable, OSError, ValueError):
            pass


def sign_in_with_credits(handler, tell=None) -> str | None:
    """Buy credits / Sign in: start a device sign-in with RegenerateFM credits,
    open it in the browser, wait for the player to sign in there, and save
    the token where an OpenAI key goes. None once a token is saved,
    otherwise the problem to show at the key prompt."""
    import webbrowser
    import credits
    import keystore
    tell = tell or (lambda text: _tell(handler, text))
    store = keystore.default_path()
    if store is None:
        return "RegenerateFM credits need RegenerateFM's key file, which this system doesn't have."
    try:
        service = credits.service_url(load_env().get("REGENERATEFM_CREDITS_URL", ""))
        sign_in = credits.start(service)
    except (RuntimeError, ValueError) as exc:
        return str(exc)
    opened = webbrowser.open(sign_in.link)
    tell(("Your browser is open at RegenerateFM credits." if opened else f"Open {sign_in.link} in your browser.") +
         f" Sign in with your email, buy credits if you need them, and confirm the code {sign_in.user_code}."
         " RegenerateFM carries on by itself once you've signed in.")
    try:
        answer = credits.wait(service, sign_in)
    except RuntimeError as exc:
        return str(exc)
    if answer is None:
        return "The sign-in expired before it was confirmed. Choose Buy credits / Sign in again."
    try:
        keystore.save(store, answer["token"])
    except (OSError, ValueError) as exc:
        return f"Could not save the sign-in: {exc}"
    _handler_class(handler).key_rejected = False
    count = int(answer.get("credits") or 0)
    tell(f"Signed in to RegenerateFM credits as {answer.get('email', '')}, with {count} "
         f"credit{'' if count == 1 else 's'}." + ("" if count else
         " Buy credits in the browser page, then choose the face command again."))
    return None


def _env_key() -> str:
    """A key set in .env or the environment, which takes priority over the saved one."""
    return load_env(use_key_file=False).get("OPENAI_API_KEY", "").strip()


def _handler_class(handler) -> type:
    """Handler state lives on the class; menus pass the class, requests an instance."""
    return handler if isinstance(handler, type) else type(handler)


def _asks_in_game(handler) -> bool:
    return sys.platform == "darwin" and bool(handler.reload_port)


def _asks_in_window(handler) -> bool:
    """Started by FM24 on Windows or Proton: no window of its own, so a key is
    asked for in RegenerateFM's key dialog (ask_key_in_window)."""
    return sys.platform == "win32" and bool(getattr(handler, "started_by_fm24", None))


KEY_WINDOW = "Paste or type your OpenAI key in the RegenerateFM: OpenAI key window."


def ask_key_in_window(handler, *, tell_fm24: bool = True) -> bool:
    """With no key saved (or one OpenAI rejected), ask in RegenerateFM's key
    dialog (key_dialog: an ordinary Windows edit box, which takes a paste
    under Wine too), check the key with OpenAI (free) and save it in FM
    Face's key file. True once a key is saved or set in .env."""
    import key_dialog
    import keystore
    store = keystore.default_path()
    with handler.key_lock:
        if _env_key() or (load_env().get("OPENAI_API_KEY", "").strip() and not handler.key_rejected):
            return True
        if tell_fm24 and handler.reload_port:
            try:
                show_dialog(handler.reload_port, handler.token, "message", "RegenerateFM", KEY_WINDOW)
            except (DialogUnavailable, OSError, ValueError):
                pass
        problem = _rejected_text() if handler.key_rejected else None
        while True:
            text = ((problem + "\n\n") if problem else "") + _credits_text() + \
                   ("\n\nOr use your own OpenAI API key: paste it (it starts with sk-) with Ctrl+V or type it, "
                    f"then choose Save key. Get a key… opens {keystore.KEYS_PAGE}.\n\n"
                    f"The key or sign-in is saved encrypted for your Windows user in {store.parent}. "
                    "Delete that folder to forget it.")
            try:
                entered = key_dialog.ask(text, link=keystore.KEYS_PAGE, credits=True)
            except OSError as exc:
                print(f"Couldn't show the key dialog ({exc}).", flush=True)
                return False
            if entered is None:
                print("No OpenAI key was saved; choosing a face command asks again.", flush=True)
                return False
            if entered == CREDITS_CHOICE:
                problem = sign_in_with_credits(handler)
                if problem is None:
                    return True
                continue
            try:
                key = keystore.clean(entered)
            except ValueError as exc:
                problem = str(exc)
                continue
            problem = keystore.check(key, base=_api(key))
            if problem:
                continue
            try:
                keystore.save(store, key)
            except (OSError, ValueError) as exc:
                problem = f"Could not save the key: {exc}"
                continue
            _handler_class(handler).key_rejected = False
            print(f"OpenAI key checked and saved in {store.parent}.", flush=True)
            return True


def _credits_text() -> str:
    import credits
    return (credits.BUY_TEXT + " Choose Buy credits / Sign in.\n\n"
            "With your own OpenAI API key instead, each face is one billed request on your OpenAI account, "
            "usually a few cents or less.")


def ensure_key(handler) -> bool:
    """FM24 for macOS asks for the OpenAI key in the game, like FM26: with no
    key saved (or one OpenAI rejected), show the library's key prompt, check
    the key with OpenAI (free) and save it in RegenerateFM's key file. False when
    the player cancels; True otherwise (a missing key elsewhere fails later
    with MISSING_KEY)."""
    import keystore
    store = keystore.default_path()
    if not handler.preview_only and _asks_in_window(handler) and store is not None:
        return ask_key_in_window(handler)
    if handler.preview_only or not _asks_in_game(handler) or store is None or _env_key():
        return True
    problem = None
    if load_env().get("OPENAI_API_KEY", "").strip():
        if not handler.key_rejected:
            return True
        problem = _rejected_text()
    with handler.key_lock:
        while True:
            text = ((problem + "\n\n") if problem else "") + _credits_text() + \
                   ("\n\nOr paste or type your OpenAI key (it starts with sk-), then choose Save key. "
                    f"Get a key… opens {keystore.KEYS_PAGE}.\n\n"
                    f"The key or sign-in is kept on this Mac only, readable only by your user account, in "
                    f"{store.parent}. Delete that folder to forget it.")
            try:
                entered = ask_key(handler.reload_port, handler.token, text)
            except (DialogUnavailable, OSError, ValueError, TimeoutError) as exc:
                print(f"FM24 couldn't ask for the OpenAI key ({exc}).", flush=True)
                return True
            if entered is None:
                return False
            if entered == CREDITS_CHOICE:
                problem = sign_in_with_credits(handler)
                if problem is None:
                    return True
                continue
            try:
                key = keystore.clean(entered)
            except ValueError as exc:
                problem = str(exc)
                continue
            problem = keystore.check(key, base=_api(key))
            if problem:
                continue
            try:
                keystore.save(store, key)
            except (OSError, ValueError) as exc:
                problem = f"Could not save the key: {exc}"
                continue
            _handler_class(handler).key_rejected = False
            print(f"OpenAI key checked and saved in {store.parent}.", flush=True)
            return True


def _rejected_text() -> str:
    """The key prompt's first line once the saved key or sign-in was refused."""
    import credits
    if credits.is_token(load_env().get("OPENAI_API_KEY", "").strip()):
        return "Your RegenerateFM credits sign-in has ended. Choose Buy credits / Sign in to sign in again."
    return "OpenAI didn't accept the saved key. Paste a new one."


def _check_saved_key(handler) -> None:
    """At start: a saved key (or credits sign-in) now refused is asked for again."""
    import keystore
    saved = "" if _env_key() else load_env().get("OPENAI_API_KEY", "").strip()
    if saved and keystore.check(saved, base=_api(saved)) in (keystore.REJECTED, keystore.SIGNED_OUT):
        _handler_class(handler).key_rejected = True
        print("OpenAI no longer accepts the saved key; FM24 will ask for a new one.", flush=True)


class NativePortraitProgress:
    """Progress text on RegenerateFM's portrait popup inside FM24 (native DLL).

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

    def fail(self, message: str) -> bool:
        """Mark the popup failed; the caller still reports ``message``."""
        del message
        self.failed = True
        self._set("Failed")
        return False

    def close(self) -> None:
        self._set("")


class NativeDialogProgress:
    """Progress in a RegenerateFM OK dialog, for skins without RegenerateFM's portrait
    popup. The skin reload after a new face closes it; a failure turns it
    into the error message. Closing it early doesn't stop the face.

    Raises DialogUnavailable from the constructor if FM24 cannot show it.
    """

    def __init__(self, port: int, token: str, text: str):
        self.port, self.token = port, token
        self.failed = False
        show_dialog(port, token, "message", "RegenerateFM", text)

    def send(self, command: str) -> None:
        if command == "reload":
            try:
                set_progress(self.port, self.token, "dialog", "Loading the new face…")
            except (DialogUnavailable, ValueError, OSError):
                pass

    def fail(self, message: str) -> bool:
        """Show ``message`` in the dialog; False if it was closed, so the
        caller must report it."""
        self.failed = True
        try:
            set_progress(self.port, self.token, "finish", clip_text(message))
            return True
        except (DialogUnavailable, ValueError, OSError):
            return False

    def close(self) -> None:
        pass


def start_progress(handler, mode: str):
    """In-game progress: on the RegenerateFM portrait popup, else in a RegenerateFM
    dialog (another skin), else None."""
    try:
        return NativePortraitProgress(handler.reload_port, handler.token,
                                      "Updating…" if mode == "update" else "Generating…")
    except (DialogUnavailable, ValueError, OSError):
        pass
    try:
        return NativeDialogProgress(handler.reload_port, handler.token,
                                    "Updating this face…" if mode == "update" else "Generating a new face…")
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
    except MemoryUnavailable as exc:
        raise ValueError(f"{exc} No image request was made.") from exc
    try:
        screen = read_open_screen(pid)
        person_id, profile = read_open_profile(pid, identity_only=identity_only, screen=screen)
        if screen.person_id != person_id:
            raise ValueError(f"The open FM24 screen shows {screen.person_id}, not person {person_id}; "
                             "no image request was made.")
        biography = screen.biography
        if (not identity_only and biography and
                not biography.lower().startswith("no biography is available")):
            profile["biographyHeadline"] = biography
        # The check read uses the header and the known record: no memory search.
        if (read_open_screen(pid) != screen or
                reread_profile(pid, person_id, screen.title, profile["age"], identity_only=identity_only,
                               today=screen.today) !=
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


def resolve_reference(reference: int, *, wait: bool) -> str:
    """The person ID for the object browser reference the macOS library sends
    when the header shows no ID (person_handles)."""
    try:
        return person_for_reference(find_game_pid(), reference, wait=wait)
    except MemoryUnavailable as exc:
        raise ValueError(f"{exc} No image request was made.") from exc


def face_exists(handler, person_id: str) -> bool:
    """Whether "Update face" has a portrait to start from (for the DLL's popup)."""
    if handler.game_dir is None:
        try:
            handler.game_dir = game_dir_from_process(find_game_pid())
        except (MemoryUnavailable, OSError, ValueError):
            pass
    # The DLL waits at most 30 ms, so the game archive is indexed in the
    # background on first use; until then there is no answer.
    return has_existing_portrait(person_id, handler.graphics_dir, handler.game_dir, wait=False)


def _quietly(function, *args) -> None:
    try:
        function(*args)
    except (OSError, ValueError, RuntimeError):
        pass


def reload_portrait(handler) -> None:
    """Reload FM24's skin inside the game through the native DLL."""
    request_native_reload(handler.reload_port, handler.token)


def generate_for_profile(handler, player_id: str, profile: dict) -> dict:
    """Generate, save and reload a portrait for a profile read by ID.

    Used by the in-game squad batch, which reads each player from memory
    instead of opening the profile. Shares the endpoint's in-flight lock.
    """
    if not ensure_key(handler):
        return {"ok": False, "cancelled": True, "error": NO_KEY_SAVED}
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
            raise ValueError(MISSING_KEY)
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
        return {"ok": False, "error": error_text(exc, handler)}
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
    key_lock = threading.Lock()
    key_rejected = False
    started_by_fm24 = None  # FM24's process ID when the DLL started this companion
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
<meta name="referrer" content="no-referrer"><title>RegenerateFM for FM24</title>
<style>body{{font:16px system-ui;background:#15191d;color:#eee;max-width:660px;margin:3rem auto;padding:0 1rem}}
button{{font:inherit;padding:.6rem 1rem}}pre{{white-space:pre-wrap;overflow-wrap:anywhere}}</style>
<h1>RegenerateFM for FM24</h1><p id="status">Reading the open FM24 profile…</p><pre id="detail"></pre>
<button id="again" hidden>{mode} again</button>
<script>
const data={payload};
const status=document.getElementById('status'),detail=document.getElementById('detail'),again=document.getElementById('again');
async function run(force=false){{
  if(!force && sessionStorage.getItem('regeneratefm-fm24-ran')){{status.textContent='This tab already ran. Return to FM24, or use the button below.';again.hidden=false;return;}}
  sessionStorage.setItem('regeneratefm-fm24-ran','1');
  status.textContent='{mode} in progress…';again.hidden=true;
  try{{const response=await fetch('/fm24/generate',{{method:'POST',headers:{{'Content-Type':'application/json'}},body:JSON.stringify(data)}});
    const result=await response.json();status.textContent=result.ok ? (result.preview ? 'Profile preview ready; no image was generated.' : (result.refreshed ? 'Portrait saved and reloaded in FM24.' : 'Portrait saved; FM24 reload needs attention.')) : 'RegenerateFM could not continue.';
    detail.textContent=result.ok ? (result.preview ? result.prompt : ('Person '+result.playerId+(result.warning ? '\\n'+result.warning : ''))) : result.error;
  }}catch(error){{status.textContent='Could not reach the FM24 companion.';detail.textContent=String(error);}}
  again.hidden=false;
}}
again.onclick=()=>{{sessionStorage.removeItem('regeneratefm-fm24-ran');location.reload();}};run();
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
            if not ensure_key(self):
                self._json(200, {"ok": False, "cancelled": True, "error": NO_KEY_SAVED})
                return
            if not self.preview_only:
                progress = start_progress(self, mode)
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
                raise ValueError(MISSING_KEY)
            png = edit_png(env, prompt, reference) if reference else generate_png(env, prompt)
            _, previous = save_versioned_portrait(self.graphics_dir, player_id, png)
            if progress:
                progress.send("reload")
            try:
                reload_portrait(self)
            except (ValueError, OSError, RuntimeError, TimeoutError) as exc:
                warning = f"Portrait saved, but FM24 reload failed: {exc}. Use Preferences > Reload Skin."
                # "shown": the progress dialog now says so; nothing else needs to.
                self._json(200, {"ok": True, "playerId": player_id, "refreshed": False, "warning": warning,
                                 "shown": bool(progress and progress.fail(warning))})
                return
            for old in previous:
                old.unlink(missing_ok=True)
            self._json(200, {"ok": True, "playerId": player_id, "refreshed": True})
        except (ValueError, OSError, RuntimeError, ParseError) as exc:
            error = error_text(exc, self)
            self._json(422, {"ok": False, "error": error, "shown": bool(progress and progress.fail(error))})
        finally:
            if progress and not progress.failed:
                progress.close()
            with self.lock:
                type(self).in_flight = False


def _default_graphics_dir() -> Path | None:
    """FM24's user-data graphics/regeneratefm folder on Windows (Documents may be redirected) and macOS."""
    if sys.platform == "darwin":
        return (Path.home() / "Library" / "Application Support" / "Sports Interactive" /
                "Football Manager 2024" / "graphics" / "regeneratefm")
    if sys.platform != "win32":
        return None
    import ctypes
    buffer = ctypes.create_unicode_buffer(260)
    if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buffer) != 0:  # CSIDL_PERSONAL
        return None
    return Path(buffer.value) / "Sports Interactive" / "Football Manager 2024" / "graphics" / "regeneratefm"


def _installed_token(game_dir: Path) -> str | None:
    import setup_fm24
    return setup_fm24.installed_token(game_dir)


def _exit_with_game(pid: int) -> None:
    """A companion FM24 started stops when FM24 does."""
    if sys.platform == "win32":
        import ctypes
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = ctypes.c_void_p
        handle = kernel32.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
        if handle:
            kernel32.WaitForSingleObject(ctypes.c_void_p(handle), 0xFFFFFFFF)
    else:
        import os
        while True:
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                break
            except PermissionError:
                pass
            time.sleep(2)
    print("FM24 has closed, so the companion is stopping.", flush=True)
    import os
    os._exit(0)


def _first_start(handler, game_dir: Path | None, preview_only: bool) -> None:
    """In a companion FM24 started: build the optional skin if the setup
    couldn't (plain python3 on Linux), then on Windows and Proton check the
    key, asking for one in a window if none is saved."""
    if game_dir is not None:
        import setup_fm24
        user_data = setup_fm24.find_user_data_dir(game_dir)
        if user_data is not None and setup_fm24.build_skin_with_popups(game_dir, user_data / "skins" / "regeneratefm"):
            print("Built the optional RegenerateFM skin; choose it in FM24's Preferences > Interface "
                  "after restarting FM24.", flush=True)
    # On macOS the key is asked for in the game when a face command needs it.
    if preview_only or _env_key() or sys.platform == "darwin":
        return
    _check_saved_key(handler)
    if not load_env().get("OPENAI_API_KEY", "").strip() or handler.key_rejected:
        ask_key_in_window(handler, tell_fm24=False)


def _log_to_file() -> None:
    """A companion FM24 started has no window: everything goes to
    regeneratefm-fm24.log beside it (the previous run's in regeneratefm-fm24.log.old)."""
    import os
    log = ROOT / "regeneratefm-fm24.log"
    try:
        if log.exists() and log.stat().st_size > 1_000_000:
            os.replace(log, log.with_suffix(".log.old"))
        stream = open(log, "a", encoding="utf-8", buffering=1, errors="replace")
    except OSError:
        return
    sys.stdout = sys.stderr = stream


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--auth-file", type=Path, default=ROOT / ".fm24-auth",
                        help="the token file build_skin.py wrote (default: .fm24-auth beside RegenerateFM)")
    default_graphics = _default_graphics_dir()
    parser.add_argument("--graphics-dir", type=Path, default=default_graphics, required=default_graphics is None,
                        help="FM24's user-data graphics/regeneratefm folder" +
                             (f" (default: {default_graphics})" if default_graphics else ""))
    parser.add_argument("--port", type=int, default=8768)
    # Accepted for old scripts; the native menu is the only mode.
    parser.add_argument("--native-menu", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--native-port", type=int, default=8769)
    parser.add_argument("--preview-only", action="store_true", help="Return the prompt without an API request or portrait write")
    # Set by the DLL when it starts the companion with FM24.
    parser.add_argument("--started-by-fm24", type=int, metavar="PID", help=argparse.SUPPRESS)
    parser.add_argument("--game-dir", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if not 1024 <= args.native_port <= 65534:
        parser.error("--native-port must be between 1024 and 65534")
    if args.started_by_fm24:
        print(f"Started by FM24 (process {args.started_by_fm24}) at {time.strftime('%Y-%m-%d %H:%M:%S')}.", flush=True)
        threading.Thread(target=_exit_with_game, args=(args.started_by_fm24,), daemon=True).start()
    try:
        token = args.auth_file.read_text(encoding="ascii").strip()
    except (OSError, UnicodeError) as exc:
        token = _installed_token(args.game_dir) if args.game_dir else None
        if not token:
            parser.error(f"Could not read the FM24 token file {args.auth_file} ({exc}). "
                         "Run the RegenerateFM setup again.")
    if len(token) < 40:
        parser.error("Invalid FM24 callback token file.")
    graphics_dir = args.graphics_dir.expanduser().resolve()
    if args.graphics_dir == default_graphics and graphics_dir.parent.is_dir():
        graphics_dir.mkdir(exist_ok=True)
    if not graphics_dir.is_dir() or graphics_dir.name != "regeneratefm" or graphics_dir.parent.name != "graphics" or graphics_dir.parent.parent.name != "Football Manager 2024":
        parser.error("--graphics-dir must be the existing Football Manager 2024/graphics/regeneratefm directory")
    if not args.preview_only and sys.platform != "darwin" and not args.started_by_fm24:
        ask_for_key()
    Handler.started_by_fm24 = args.started_by_fm24
    Handler.token = token
    Handler.graphics_dir = graphics_dir
    Handler.preview_only = args.preview_only
    # The DLL accepts reload, dialog and progress requests on the port after the bridge.
    Handler.reload_port = args.native_port + 1
    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    except OSError as exc:
        if args.started_by_fm24:
            parser.exit(0, f"Another RegenerateFM companion is already running (port {args.port}: {exc}); "
                           "this one is stopping.\n")
        parser.exit(1, f"Companion port 127.0.0.1:{args.port} is unavailable ({exc}). "
                       "Is another companion already running?\n")
    from native_listener import start_native_listener
    try:
        start_native_listener(args.port, args.native_port, token, Handler, generate=generate_for_profile,
                              has_face=lambda person_id: face_exists(Handler, person_id),
                              resolve=resolve_reference, key_ready=lambda: ensure_key(Handler))
    except RuntimeError as exc:
        parser.exit(1, f"{exc}\n")
    # Index the portraits now, so the first popup's face check is quick (the
    # game archive only if FM24 is already running).
    threading.Thread(target=lambda: _quietly(face_exists, Handler, "1"), daemon=True).start()
    if not args.preview_only and sys.platform == "darwin":
        threading.Thread(target=_quietly, args=(_check_saved_key, Handler), daemon=True).start()
    if args.started_by_fm24:
        threading.Thread(target=_quietly, args=(_first_start, Handler, args.game_dir, args.preview_only),
                         daemon=True).start()
    # People by handle, for profiles whose header shows no ID, where FM24's
    # person table isn't mapped.
    if LAYOUT["browser_reference"] is not None and LAYOUT["person_table"] is None:
        try:
            build_in_background(find_game_pid())
        except (MemoryUnavailable, OSError, ValueError):
            pass  # FM24 isn't running yet; the first lookup indexes it.
    print(f"FM24 companion listening on 127.0.0.1:{args.port} "
          f"({'preview' if args.preview_only else 'generation'} mode)", flush=True)
    server.serve_forever()


def ask_for_key() -> None:
    """With no key in .env or RegenerateFM's key file, ask for one in this window.

    The key is checked with OpenAI (free) and saved in RegenerateFM's key file,
    which the FM26 plugin also reads: encrypted for the Windows user in
    %APPDATA%\\RegenerateFM, or readable only by this user in macOS's
    ~/Library/Application Support/RegenerateFM.
    """
    import getpass
    import keystore
    store = keystore.default_path()
    if store is None or not sys.stdin or not sys.stdin.isatty():
        return
    if load_env(use_key_file=False).get("OPENAI_API_KEY", "").strip():
        return  # .env (or the environment) sets the key
    saved = load_env().get("OPENAI_API_KEY", "").strip()
    if saved:
        # A saved key can be revoked later; checking it is free. Ask again only
        # when OpenAI says no, not when it can't be reached.
        if keystore.check(saved, base=_api(saved)) not in (keystore.REJECTED, keystore.SIGNED_OUT):
            return
        print(_rejected_text(), flush=True)
    print(_credits_text().replace("Choose Buy credits / Sign in.", "Type credits and press Enter to sign in.") + "\n"
          f"For a key, create one at {keystore.KEYS_PAGE}, then paste it here (right-click or Ctrl+V) or type it. "
          "It isn't shown as you type. Press Enter on an empty line to skip.", flush=True)
    while True:
        try:
            entered = getpass.getpass("OpenAI API key: ")
        except (EOFError, KeyboardInterrupt):
            print()
            return
        if not entered.strip():
            print("No key saved. Faces can't be made until you restart the companion and enter one.", flush=True)
            return
        if entered.strip().lower() == "credits":
            class _Terminal:
                reload_port = None
                key_rejected = False
            problem = sign_in_with_credits(_Terminal)
            if problem is None:
                return
            print(problem, flush=True)
            continue
        try:
            key = keystore.clean(entered)
        except ValueError as exc:
            print(exc, flush=True)
            continue
        print("Checking the key with OpenAI (this is free)...", flush=True)
        problem = keystore.check(key, base=_api(key))
        if problem:
            print(problem, flush=True)
            continue
        try:
            keystore.save(store, key)
        except (OSError, ValueError) as exc:
            print(f"Could not save the key: {exc}", flush=True)
            return
        stored = "readable only by you" if sys.platform == "darwin" else "encrypted for your Windows user"
        print(f"Key saved, {stored}, in {store.parent}. "
              "Delete that folder to forget it.", flush=True)
        return


def _setup_if_needed() -> Path | None:
    """A double-clicked companion sets FM24 up itself the first time (and
    after an update); FM24 then starts it. Returns FM24's folder."""
    import setup_fm24
    try:
        game, user_data = setup_fm24.resolve_dirs(None, None)
    except SystemExit as exc:
        print(exc, flush=True)
        return None
    companion = setup_fm24.packaged_companion()
    if not setup_fm24.is_set_up(game, user_data / "skins" / "regeneratefm", ROOT / ".fm24-auth", companion):
        print("Setting up RegenerateFM for FM24 (one time only).", flush=True)
        setup_fm24.setup(game, user_data, ROOT / ".fm24-auth", companion)
    return game


def _main_packaged() -> None:
    """The packaged companion. FM24's DLL starts it with --started-by-fm24
    (no window; it logs to regeneratefm-fm24.log). Double-clicked, it sets FM24 up
    and asks for the key, keeping its window readable on exit.

    `regeneratefm-fm24.exe setup [--game-dir ...] [--remove]` runs only the setup.
    """
    if "--started-by-fm24" in sys.argv:
        _log_to_file()
        try:
            main()
        except Exception:
            import traceback
            traceback.print_exc()
            raise SystemExit(1)
        return

    def pause(prompt: str = "Press Enter to close."):
        try:
            input(prompt)
        except (EOFError, OSError):
            pass
    try:
        if sys.argv[1:2] == ["setup"]:
            import setup_fm24
            setup_fm24.main(sys.argv[2:])
            pause()
            return
        if len(sys.argv) == 1:
            game = _setup_if_needed()
            if game is None:
                pause()
                return
            ask_for_key()
            import setup_fm24
            if not setup_fm24.native_install.game_running(game):
                print("\nRegenerateFM is set up. It starts by itself whenever you start FM24, "
                      "so there's nothing else to run.", flush=True)
                pause()
                return
            # FM24 is already running; its companion normally is too.
        main()
    except SystemExit as exc:
        if exc.code not in (None, 0):
            pause()
        raise
    except Exception:
        import traceback
        traceback.print_exc()
        pause()
        raise SystemExit(1)


if __name__ == "__main__":
    _main_packaged() if getattr(sys, "frozen", False) else main()
