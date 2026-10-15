"""Conservative FM24 squad portrait scan and optional sequential generation.

The table has no portrait state. Open each visible player, verify the title ID,
and classify the actual profile portrait before offering any billed requests.
"""

from __future__ import annotations

import difflib
import json
import math
import os
import re
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageChops
from rapidocr import RapidOCR
from Xlib import X, display
from Xlib.ext import xtest
from Xlib.error import XError

from capture_profile import BASE_SIZE, _ocr, _rect
from read_profile import capture_window, read_identity
from refresh import _activate_game, _click, resolve_window
from menu_overlay import menu_bottom
from live_memory import MemoryUnavailable, find_game_pid
from native_dialog import DialogUnavailable, NativeProgress, notify, show_dialog
from native_portrait import read_portrait_state
from native_profile import squad_club, squad_player_profile
from native_squad import read_full_squad


@dataclass(frozen=True)
class SquadPlayer:
    player_id: str
    name: str
    missing: bool


class ScanCancelled(ValueError):
    """User requested a clean stop before any squad image request."""


class SquadProgress:
    def __init__(self, window_id: str, stop_label: str):
        self.stopped = threading.Event()
        self.process = subprocess.Popen([
            sys.executable, str(Path(__file__).with_name("squad_progress.py")),
            "--window-id", window_id, "--parent-pid", str(os.getpid()),
            "--stop-label", stop_label], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

        def read_actions() -> None:
            if self.process.stdout:
                for line in self.process.stdout:
                    if line.strip() == "stop":
                        self.stopped.set()

        threading.Thread(target=read_actions, daemon=True).start()

    def update(self, message: str, percent: int | None = None) -> None:
        if self.process.poll() is not None or not self.process.stdin:
            self.stopped.set()
            return
        payload = {"text": message}
        if percent is not None:
            payload["percent"] = percent
        try:
            self.process.stdin.write(json.dumps(payload) + "\n")
            self.process.stdin.flush()
        except BrokenPipeError:
            self.stopped.set()

    def close(self) -> None:
        if self.process.poll() is None and self.process.stdin:
            try:
                self.process.stdin.write('{"close":true}\n')
                self.process.stdin.flush()
                self.process.wait(timeout=2)
            except (BrokenPipeError, subprocess.TimeoutExpired):
                self.process.terminate()
        if self.process.stdin:
            self.process.stdin.close()
        if self.process.stdout:
            self.process.stdout.close()


def portrait_state(image: Image.Image) -> str:
    """Only the tested black silhouette is treated as a missing portrait."""
    if image.size != BASE_SIZE:
        raise ValueError("FM24 squad scan requires the tested 1854×1011 window.")
    crop = image.crop((188, 120, 255, 198)).convert("RGB")
    pixels = list(crop.getdata())
    coloured = sum(max(rgb) - min(rgb) > 20 for rgb in pixels) / len(pixels)
    dark = sum(max(rgb) < 40 for rgb in pixels) / len(pixels)
    luminance = [(r + g + b) / 3 for r, g, b in pixels]
    mean = sum(luminance) / len(luminance)
    contrast = math.sqrt(sum((value - mean) ** 2 for value in luminance) / len(luminance))
    if coloured < .01 and .35 < dark < .75 and contrast > 25:
        return "missing"
    if coloured > .10:
        return "present"
    return "unknown"


def _portrait_state_ready(window_id: str, engine: RapidOCR,
                          expected_id: str, first: Image.Image) -> str:
    """Use native graphics first; verify missing faces before billing."""
    try:
        native = read_portrait_state(expected_id)
    except (MemoryUnavailable, OSError, ValueError):
        native = "unknown"
    if native == "present":
        return "present"
    image = first
    deadline = time.monotonic() + 45
    while True:
        state = portrait_state(image)
        if state != "unknown":
            if read_identity(image, engine).player_id != expected_id:
                raise ValueError("The FM24 player changed while waiting for the portrait.")
            return state
        if time.monotonic() >= deadline:
            return "unknown"
        time.sleep(.5)
        image = capture_window(window_id)


def _squad_title(image: Image.Image, engine: RapidOCR) -> str:
    items = _ocr(_rect(image, (370, 10, 700, 55)), engine)
    if not any(text.strip().casefold() == "squad" for text, _, _, _ in items):
        raise ValueError("FM24 is no longer on the Squad screen; no image request was made.")
    sub = _ocr(_rect(image, (370, 31, 1100, 56)), engine)
    ids = re.findall(r"\bID:\s*(\d+)\)", " ".join(text for text, _, _, _ in sub))
    if len(ids) != 1:
        raise ValueError("Could not verify the current FM24 squad ID.")
    return ids[0]


def squad_menu_bottom(image: Image.Image, engine: RapidOCR) -> int | None:
    """Recognize the Squad Overview popup before adding its temporary row."""
    bottom = menu_bottom(image)
    if bottom is None or not 245 <= bottom <= 255:
        return None
    try:
        _squad_title(image, engine)
    except ValueError:
        return None
    labels = {text.casefold() for text, _, _, score in
              _ocr(_rect(image, (180, 99, 460, bottom)), engine) if score >= .75}
    required = {"overview", "numbers", "registration", "all players"}
    return bottom if required <= labels else None


def squad_rows(image: Image.Image, engine: RapidOCR) -> list[tuple[int, str]]:
    if image.size != BASE_SIZE:
        raise ValueError("FM24 squad scan requires the tested 1854×1011 window.")
    items = _ocr(_rect(image, (335, 208, 680, 922)), engine)
    rows = [(round(y + 208), text.strip()) for text, _, y, score in items
            if score >= .78 and 225 <= y + 208 <= 908 and text.strip()]
    rows.sort()
    if (len(rows) < 5 or rows[-1][0] < 880 or
            any(not 26 <= b[0] - a[0] <= 34 for a, b in zip(rows, rows[1:]))):
        raise ValueError("Could not account for every visible FM24 squad row; scan stopped.")
    return rows


def _read_rows(window_id: str, engine: RapidOCR, title: Image.Image) -> list[tuple[int, str]]:
    error = None
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        image = capture_window(window_id)
        if ImageChops.difference(_title_image(image), title).getbbox() is not None:
            raise ValueError("The selected FM24 squad changed during scanning.")
        try:
            return squad_rows(image, engine)
        except ValueError as exc:
            error = exc
            time.sleep(.5)
    raise error


def _scroll(window_id: str, direction: int, steps: int) -> None:
    _activate_game(window_id)
    connection = display.Display()
    try:
        window = connection.create_resource_object("window", int(window_id, 16))
        root = connection.screen().root
        position = root.translate_coords(window, 0, 0)
        root.warp_pointer(position.x + 900, position.y + 600)
        connection.sync()
        for _ in range(steps):
            xtest.fake_input(connection, X.ButtonPress, direction)
            xtest.fake_input(connection, X.ButtonRelease, direction)
            connection.sync()
            time.sleep(.07)
        time.sleep(.4)
    finally:
        connection.close()


def _normal(name: str) -> str:
    return re.sub(r"[^a-z0-9]", "", name.casefold())


def _overlap(previous: tuple[str, ...], current: tuple[str, ...]) -> int:
    """Return the table-row overlap, or abort if scrolling could have skipped rows."""
    if not previous:
        return 0
    for count in range(min(len(previous), len(current)), 0, -1):
        if all(difflib.SequenceMatcher(None, _normal(a), _normal(b)).ratio() >= .78
               for a, b in zip(previous[-count:], current[:count])):
            return count
    raise ValueError("FM24 squad scroll skipped rows; no image request was made.")


def _current_row(window_id: str, engine: RapidOCR, title: Image.Image,
                 target: str) -> int:
    """Find a row again after FM24 scrolls the table on profile return."""
    for _ in range(8):
        rows = _read_rows(window_id, engine, title)
        matches = [y for y, name in rows if
                   difflib.SequenceMatcher(None, _normal(name), _normal(target)).ratio() >= .78]
        if len(matches) == 1:
            return matches[0]
        if len(matches) > 1:
            raise ValueError(f"More than one squad row matched {target!r}; scan stopped.")
        _scroll(window_id, 5, 3)
    raise ValueError(f"Could not relocate squad row {target!r} after returning from a profile.")


def _open_row(window_id: str, engine: RapidOCR, y: int, row_name: str,
              squad_id: str, squad_title: Image.Image):
    observations = []
    for _ in range(2):
        # The name is the link; clicking blank space in a short-name row only
        # selects it. x=355 stays inside even short names such as Ian Fray.
        # FM24 often selects on the first click and opens on the second.
        _click(window_id, 355, y)
        _click(window_id, 355, y)
        # FM24 can finish the click animation several seconds after X11 has
        # delivered the input, particularly while a full roster is loading.
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            time.sleep(.35)
            image = capture_window(window_id)
            if ImageChops.difference(_title_image(image), squad_title).getbbox() is None:
                continue
            try:
                identity = read_identity(image, engine)
            except ValueError as exc:
                observations.append(str(exc))
                continue
            if identity.player_id == squad_id or identity.name.casefold() == "squad":
                observations.append("still on squad")
                continue
            ratio = difflib.SequenceMatcher(None, _normal(identity.name), _normal(row_name)).ratio()
            if ratio < .78:
                raise ValueError(f"Opened {identity.name!r} instead of squad row {row_name!r}; scan stopped.")
            return identity, image
    image = capture_window(window_id)
    try:
        visible = read_identity(image, engine)
    except ValueError:
        visible = None
    if visible and visible.player_id != squad_id:
        ratio = difflib.SequenceMatcher(None, _normal(visible.name), _normal(row_name)).ratio()
        if ratio >= .78:
            return visible, image
        raise ValueError(f"Opened {visible.name!r} instead of squad row {row_name!r}; scan stopped.")
    raise ValueError(f"Could not open squad row {row_name!r}; scan stopped. "
                     f"Last title checks: {observations[-3:]}")


def _title_image(image: Image.Image) -> Image.Image:
    return image.crop((374, 12, 1100, 54))


def _return_to_squad(window_id: str, title: Image.Image, engine: RapidOCR,
                     squad_id: str, *, retry_after: float = 8, attempts: int = 3) -> None:
    """Press Back until the same squad is shown again.

    FM24 can drop a Back click while a profile is still loading. Back is only
    pressed again when the title still shows the exact profile that was open
    before the click and has stayed that way for ``retry_after`` seconds, so a
    slow but accepted Back is never doubled into a second navigation.
    """
    deadline = time.monotonic() + 75
    last_error = "squad screen did not finish loading"
    for attempt in range(attempts):
        profile_title = _title_image(capture_window(window_id))
        _click(window_id, 60, 103)
        retry_at = time.monotonic() + retry_after
        while True:
            if time.monotonic() >= deadline:
                raise ValueError(f"Could not return to the same FM24 squad; scan stopped. {last_error}")
            time.sleep(.5)
            image = capture_window(window_id)
            current = _title_image(image)
            if ImageChops.difference(current, title).getbbox() is None:
                return
            unchanged = ImageChops.difference(current, profile_title).getbbox() is None
            if not unchanged:
                retry_at = float("inf")  # Navigation started; never press Back again.
            try:
                if _squad_title(image, engine) == squad_id:
                    return
                last_error = "a different squad was opened"
            except ValueError as exc:
                last_error = str(exc)
            if unchanged and time.monotonic() >= retry_at and attempt + 1 < attempts:
                print("FM24 did not respond to Back; pressing it again.", flush=True)
                break
    raise ValueError(f"Could not return to the same FM24 squad; scan stopped. {last_error}")


def scan_squad(window_id: str, engine: RapidOCR, *, limit: int | None = None,
               progress=None) -> list[SquadPlayer]:
    """Scan from table top to bottom; any ambiguous row or face aborts."""
    start = capture_window(window_id)
    squad_id = _squad_title(start, engine)
    title = _title_image(start)
    _scroll(window_id, 4, 30)
    players: dict[str, SquadPlayer] = {}
    prior_rows: tuple[str, ...] = ()
    for page in range(30):
        rows = _read_rows(window_id, engine, title)
        if page == 0 and (not 225 <= rows[0][0] <= 232 or rows[-1][0] < 880):
            raise ValueError("FM24 squad table did not start at its first row or OCR missed an edge row.")
        names = tuple(name for _, name in rows)
        if page and names == prior_rows:
            return list(players.values())
        overlap = _overlap(prior_rows, names)
        for _, name in rows[overlap:]:
            if progress:
                progress(len(players), name)
            y = _current_row(window_id, engine, title, name)
            identity, profile = _open_row(window_id, engine, y, name, squad_id, title)
            state = _portrait_state_ready(window_id, engine, identity.player_id, profile)
            if state == "unknown":
                raise ValueError(f"Could not classify {identity.name}'s portrait; scan stopped.")
            if identity.player_id in players:
                raise ValueError("A squad player appeared twice in non-overlapping rows; scan stopped.")
            players[identity.player_id] = SquadPlayer(identity.player_id, identity.name, state == "missing")
            _return_to_squad(window_id, title, engine, squad_id)
            if limit is not None and len(players) >= limit:
                return list(players.values())
        # The game can recenter the table on the last opened player. Use the
        # current viewport for the next overlap instead of stale page rows.
        prior_rows = tuple(name for _, name in _read_rows(window_id, engine, title))
        _scroll(window_id, 5, 20)
    raise ValueError("FM24 squad scan exceeded 30 table pages.")


def _zenity_info(text: str) -> None:
    subprocess.run(["zenity", "--info", "--title=FM Face", "--text=" + text], check=False)


def _zenity_error(text: str) -> None:
    subprocess.run(["zenity", "--error", "--title=FM Face", "--text=" + text], check=False)


def confirm_batch(missing: int, total: int, *, preview_only: bool = False,
                  native: tuple[int, str] | None = None) -> bool:
    """Ask before any request: in FM24's own dialog when ``native`` (the DLL's
    port and token) is given and works, otherwise with zenity."""
    if not missing:
        text = f"All {total} squad players already have portraits."
        if native:
            notify(*native, text, _zenity_info)
        else:
            _zenity_info(text)
        return False
    action = "Preview faces" if preview_only else "Generate faces"
    bill = "No API requests will be made in preview mode." if preview_only else \
        "Each face makes one billed API request."
    if native:
        try:
            return show_dialog(*native, "confirm", f"{action}?",
                               f"{missing} of {total} squad players have no portrait. "
                               f"{action} for the {missing} missing players? {bill}",
                               timeout=600) == "yes"
        except TimeoutError:
            return False
        except (DialogUnavailable, ValueError, OSError) as exc:
            print(f"FM24 confirmation dialog unavailable ({exc}); using zenity.", flush=True)
    return subprocess.run([
        "zenity", "--question", "--title=FM Face", "--ok-label=" + action,
        "--cancel-label=Cancel", "--width=420",
        f"--text={missing} of {total} squad players have no portrait.\n\n"
        f"{action} for the {missing} missing players? {bill}"
    ], check=False).returncode == 0


def _find_player(window_id: str, engine: RapidOCR, target: SquadPlayer,
                 squad_id: str, title: Image.Image) -> Image.Image:
    _scroll(window_id, 4, 30)
    prior_rows: tuple[str, ...] = ()
    for _ in range(30):
        rows = _read_rows(window_id, engine, title)
        names = tuple(name for _, name in rows)
        if names == prior_rows:
            break
        _overlap(prior_rows, names)
        for y, name in rows:
            if difflib.SequenceMatcher(None, _normal(name), _normal(target.name)).ratio() < .78:
                continue
            identity, profile = _open_row(window_id, engine, y, name, squad_id, title)
            if identity.player_id != target.player_id:
                raise ValueError(f"{target.name}'s ID changed before generation; stopped.")
            if _portrait_state_ready(window_id, engine, identity.player_id, profile) != "missing":
                raise ValueError(f"{target.name} no longer has the expected missing portrait; stopped.")
            return profile
        prior_rows = names
        _scroll(window_id, 5, 20)
    raise ValueError(f"Could not find {target.name} in the current FM24 squad.")


def run_squad_batch(window_id: str, engine: RapidOCR, port: int, token: str,
                    handler, invoke) -> None:
    """Click-listener entry point: scan, confirm, then reuse single-face requests."""
    scan_dialog = None
    batch_dialog = None
    try:
        start = capture_window(window_id)
        squad_id = _squad_title(start, engine)
        title = _title_image(start)
        print("FM24 squad scan started (no API requests).", flush=True)
        scan_dialog = SquadProgress(window_id, "Cancel scan")

        def scan_progress(n: int, name: str) -> None:
            if scan_dialog.stopped.is_set():
                raise ScanCancelled("Squad scan cancelled; no image request was made.")
            scan_dialog.update(f"Checked {n} players; opening {name}")
            print(f"FM24 squad scan: {n} checked; {name}", flush=True)

        players = scan_squad(window_id, engine,
                             progress=scan_progress)
        if scan_dialog.stopped.is_set():
            raise ScanCancelled("Squad scan cancelled; no image request was made.")
        scan_dialog.close()
        scan_dialog = None
        missing = [player for player in players if player.missing]
        print(f"FM24 squad scan complete: {len(missing)} missing of {len(players)} players.", flush=True)
        if not confirm_batch(len(missing), len(players), preview_only=handler.preview_only):
            return
        batch_dialog = SquadProgress(window_id, "Stop after current face")
        completed = 0
        for index, player in enumerate(missing):
            if batch_dialog.stopped.is_set():
                print(f"FM24 squad batch stopped after {completed} faces.", flush=True)
                return
            batch_dialog.update(f"Opening {player.name} ({index + 1}/{len(missing)})",
                                round(index * 100 / len(missing)))
            # A fresh visible profile and exact ID check precede each request.
            _find_player(window_id, engine, player, squad_id, title)
            if batch_dialog.stopped.is_set():
                print(f"FM24 squad batch stopped after {completed} faces.", flush=True)
                return
            batch_dialog.update(f"Generating {player.name} ({index + 1}/{len(missing)})")
            result = invoke(port, token, handler, expected_id=player.player_id)
            if not result.get("ok") or result.get("playerId") != player.player_id:
                raise ValueError(f"Generation stopped at {player.name}: {result.get('error', 'profile mismatch')}")
            if not handler.preview_only and not result.get("refreshed"):
                raise ValueError(f"Generation stopped after saving {player.name}: " +
                                 result.get("warning", "FM24 reload failed"))
            completed += 1
            batch_dialog.update(f"Loaded {player.name} ({completed}/{len(missing)})",
                                round(completed * 100 / len(missing)))
            print(f"FM24 squad batch: {completed}/{len(missing)} {player.name}", flush=True)
            _return_to_squad(window_id, title, engine, squad_id)
            # Allow time to stop through the confirmation/progress dialog before
            # making another billed request.
            if index + 1 < len(missing):
                time.sleep(5)
        batch_dialog.close()
        batch_dialog = None
        subprocess.run(["zenity", "--info", "--title=FM Face",
                        f"--text={'Previewed' if handler.preview_only else 'Generated'} "
                        f"{completed} squad faces."], check=False)
    except ScanCancelled as exc:
        print(f"FM24 {exc}", flush=True)
    except (ValueError, OSError, RuntimeError, XError) as exc:
        print(f"FM24 squad batch stopped: {exc}", flush=True)
        subprocess.run(["zenity", "--error", "--title=FM Face", "--text=" + str(exc)], check=False)
    finally:
        for dialog in (scan_dialog, batch_dialog):
            if dialog:
                dialog.close()


def _finish(progress, native, text: str, fallback) -> None:
    """The batch result: in the FM24 progress dialog if it is still open,
    else a new FM24 message, else zenity."""
    if isinstance(progress, NativeProgress):
        progress.finish(text, fallback)
        return
    if progress:
        progress.close()
    notify(*(native or (None, "")), text, fallback)


def run_native_squad_batch(window_id: str | None, handler, generate, describe=str, *,
                           pid: int | None = None) -> None:
    """Native-menu squad command: read the roster, portrait state and every
    prompt from FM24's memory, then generate without opening any profile.

    ``generate(person_id, profile)`` returns the companion's result dict.
    Undrawn rows come from the table's row model; the X11 window (found only
    if needed) is used just when that or FM24's own dialogs are unavailable.
    """
    batch_dialog = None
    # FM24's own dialogs through the DLL's authenticated loopback listener.
    native = (handler.reload_port, handler.token) if getattr(handler, "reload_port", None) else None
    try:
        print("FM24 squad scan started (game memory; no API requests).", flush=True)
        pid = pid or find_game_pid()

        def scroll(down: bool) -> None:
            # FM24 may still be closing its menu; activation can briefly fail.
            for attempt in range(4):
                try:
                    _scroll(resolve_window(window_id), 5 if down else 4, 3)
                    break
                except ValueError:
                    if attempt == 3:
                        raise
                    time.sleep(.5)
            time.sleep(.3)

        state = read_full_squad(scroll, pid)
        missing = state.missing
        print(f"FM24 squad scan complete: {len(missing)} missing of {state.total} players.", flush=True)
        if not confirm_batch(len(missing), state.total, preview_only=handler.preview_only, native=native):
            return
        club = squad_club(pid, [row.record for row in state.rows])
        # Every prompt is validated before the first billed request.
        for row in missing:
            squad_player_profile(pid, row.record, row.name, club, state.today)
        action = "Previewing" if handler.preview_only else "Generating"
        try:
            if not native:
                raise DialogUnavailable("no native DLL port")
            batch_dialog = NativeProgress(*native, "FM Face squad faces",
                                          f"{action} {missing[0].name} (1/{len(missing)})")
        except DialogUnavailable:
            batch_dialog = SquadProgress(resolve_window(window_id), "Stop after current face")
        completed = 0
        for index, row in enumerate(missing):
            if batch_dialog.stopped.is_set():
                print(f"FM24 squad batch stopped after {completed} faces.", flush=True)
                _finish(batch_dialog, native,
                        f"Stopped after {completed} of {len(missing)} squad faces.", _zenity_info)
                batch_dialog = None
                return
            batch_dialog.update(f"{action} {row.name} ({index + 1}/{len(missing)})",
                                round(index * 100 / len(missing)))
            # Read again just before the request: the record must still be this player.
            profile = squad_player_profile(pid, row.record, row.name, club, state.today)
            result = generate(row.person_id, profile)
            print(describe(result), flush=True)
            if not result.get("ok") or result.get("playerId") != row.person_id:
                raise ValueError(f"Generation stopped at {row.name}: {result.get('error', 'player mismatch')}")
            if not handler.preview_only and not result.get("refreshed"):
                raise ValueError(f"Generation stopped after saving {row.name}: " +
                                 result.get("warning", "FM24 reload failed"))
            completed += 1
            batch_dialog.update(f"Loaded {row.name} ({completed}/{len(missing)})",
                                round(completed * 100 / len(missing)))
            print(f"FM24 squad batch: {completed}/{len(missing)} {row.name}", flush=True)
            # A short gap to stop before the next billed request.
            for _ in range(10 if index + 1 < len(missing) and not handler.preview_only else 0):
                if batch_dialog.stopped.is_set():
                    break
                time.sleep(.3)
        _finish(batch_dialog, native,
                f"{'Previewed' if handler.preview_only else 'Generated'} {completed} squad faces.", _zenity_info)
        batch_dialog = None
    except (ValueError, OSError, RuntimeError, XError, MemoryUnavailable) as exc:
        print(f"FM24 squad batch stopped: {exc}", flush=True)
        _finish(batch_dialog, native, f"Squad faces stopped: {exc}", _zenity_error)
        batch_dialog = None
    finally:
        if batch_dialog:
            batch_dialog.close()
