"""Route FM24 portrait and optional skin clicks to the local companion."""

from __future__ import annotations

import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

from rapidocr import RapidOCR
from Xlib import X, XK, display
from Xlib.ext import record, xtest
from Xlib.protocol import rq

from capture_profile import _ocr, _rect
from companion_client import describe_result, request_generation
from menu_overlay import menu_bottom
from read_profile import capture_window, read_identity
from refresh import _activate_game
from squad_batch import run_squad_batch, squad_menu_bottom

OVERVIEW_AREA = (175, 65, 260, 96)
PORTRAIT_AREA = (180, 118, 267, 200)
PORTRAIT_MENU_AREA = (178, 203, 368, 233)


def _game_click(window_id: str, root_x: int, root_y: int) -> tuple[int, int] | None:
    connection = display.Display()
    try:
        window = connection.create_resource_object("window", int(window_id, 16))
        root = connection.screen().root
        position = root.translate_coords(window, 0, 0)
        x, y = root_x - position.x, root_y - position.y
        geometry = window.get_geometry()
        return (x, y) if 0 <= x < geometry.width and 0 <= y < geometry.height else None
    finally:
        connection.close()


def _overview_menu_bottom(window_id: str, engine: RapidOCR) -> int | None:
    screenshot = capture_window(window_id)
    bottom = menu_bottom(screenshot)
    if bottom is None:
        return None
    try:
        read_identity(screenshot, engine)
    except ValueError:
        return None
    labels = {text.casefold() for text, _, y, score in
              _ocr(_rect(screenshot, (175, 95, 410, bottom)), engine)
              if score >= .8 and y + 95 < bottom - 5}
    return bottom if {"profile", "attributes", "happiness", "information"} <= labels else None


def _dismiss_game_menu(window_id: str) -> None:
    _activate_game(window_id)
    connection = display.Display()
    try:
        keycode = connection.keysym_to_keycode(XK.string_to_keysym("Escape"))
        if not keycode:
            raise RuntimeError("Could not find the Escape key for FM24.")
        xtest.fake_input(connection, X.KeyPress, keycode)
        xtest.fake_input(connection, X.KeyRelease, keycode)
        connection.sync()
        time.sleep(.2)
    finally:
        connection.close()


def start_listener(window_id: str, port: int, token: str, engine: RapidOCR, handler,
                   *, menu_overlay: bool = False, portrait_menu: bool = False) -> None:
    """Start daemon threads that record FM24 left clicks and open the portrait menu."""
    control = display.Display()
    recording = display.Display()
    if not recording.has_extension("RECORD"):
        control.close()
        recording.close()
        raise RuntimeError("X11 RECORD is required for the FM24 portrait menu.")
    clicks: queue.Queue[tuple[int, int, float]] = queue.Queue(maxsize=32)
    ready = threading.Event()
    startup_error: list[Exception] = []
    game_id = int(window_id, 16)
    context = recording.record_create_context(0, [record.AllClients], [{
        "core_requests": (0, 0), "core_replies": (0, 0),
        "ext_requests": (0, 0, 0, 0), "ext_replies": (0, 0, 0, 0),
        "delivered_events": (X.ButtonPress, X.ButtonPress), "device_events": (0, 0),
        "errors": (0, 0), "client_started": False, "client_died": False,
    }])

    def on_record(reply) -> None:
        if reply.category == record.StartOfData:
            ready.set()
            return
        if reply.category != record.FromServer or reply.client_swapped:
            return
        payload = reply.data
        while payload:
            event, payload = rq.EventField(None).parse_binary_value(payload, recording.display, None, None)
            if event.type == X.ButtonPress and event.detail == 1 and event.window.id == game_id:
                try:
                    clicks.put_nowait((event.root_x, event.root_y, time.monotonic()))
                except queue.Full:
                    pass

    def record_loop() -> None:
        try:
            recording.record_enable_context(context, on_record)
        except Exception as error:
            startup_error.append(error)
            ready.set()
            print(f"FM24 click listener stopped: {error}", flush=True)
        finally:
            recording.close()
            control.close()

    def worker_loop() -> None:
        overlay = None
        overlay_hitbox = None
        batch_busy = threading.Event()

        def read_overlay(process, *, game_menu: bool, squad: bool = False) -> None:
            try:
                if process.stdout and process.stdout.readline().strip() == "generate":
                    if game_menu:
                        _dismiss_game_menu(window_id)
                    if squad:
                        batch_busy.set()
                        try:
                            run_squad_batch(window_id, engine, port, token, handler, request_generation)
                        finally:
                            batch_busy.clear()
                    else:
                        print(describe_result(request_generation(port, token, handler)), flush=True)
            except Exception as error:
                print(f"FM24 menu click could not be handled: {error}", flush=True)
            finally:
                if process.stdout:
                    process.stdout.close()

        while True:
            root_x, root_y, issued = clicks.get()
            if time.monotonic() - issued > 2 or handler.in_flight or batch_busy.is_set():
                continue
            try:
                position = _game_click(window_id, root_x, root_y)
                if not position:
                    continue
                x, y = position
                on_portrait = (portrait_menu and PORTRAIT_AREA[0] <= x < PORTRAIT_AREA[2]
                               and PORTRAIT_AREA[1] <= y < PORTRAIT_AREA[3])
                if overlay and overlay.poll() is None:
                    # The X11 recorder can see the press before Tk receives it.
                    # Repeated portrait clicks must not close a menu that has
                    # only just appeared. The menu itself receives its row clicks.
                    if on_portrait or (overlay_hitbox and overlay_hitbox[0] <= x < overlay_hitbox[2]
                                       and overlay_hitbox[1] <= y < overlay_hitbox[3]):
                        continue
                    overlay.terminate()
                    overlay = None
                    overlay_hitbox = None
                if menu_overlay and OVERVIEW_AREA[0] <= x <= OVERVIEW_AREA[2] \
                        and OVERVIEW_AREA[1] <= y <= OVERVIEW_AREA[3]:
                    time.sleep(.2)
                    bottom = _overview_menu_bottom(window_id, engine)
                    is_squad = False
                    if bottom is None:
                        bottom = squad_menu_bottom(capture_window(window_id), engine)
                        is_squad = bottom is not None
                    if bottom is not None:
                        command = [sys.executable, str(Path(__file__).with_name("menu_overlay.py")),
                             "--window-id", window_id, "--bottom", str(bottom),
                             "--parent-pid", str(os.getpid())]
                        if is_squad:
                            command.extend(("--label", "Generate missing squad faces…"))
                        overlay = subprocess.Popen(
                            command,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                        )
                        overlay_hitbox = (175, bottom, 459 if is_squad else 411, bottom + 28)
                        threading.Thread(target=read_overlay, args=(overlay,),
                                         kwargs={"game_menu": True, "squad": is_squad},
                                         name="fm24-menu-result", daemon=True).start()
                    continue
                if on_portrait:
                    # Opening is cheap and reliable; the callback validates
                    # the whole visible profile before any paid API request.
                    overlay = subprocess.Popen(
                        [sys.executable, str(Path(__file__).with_name("portrait_menu.py")),
                         "--window-id", window_id, "--parent-pid", str(os.getpid())],
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                    )
                    overlay_hitbox = PORTRAIT_MENU_AREA
                    threading.Thread(target=read_overlay, args=(overlay,), kwargs={"game_menu": False},
                                     name="fm24-portrait-result", daemon=True).start()
                    continue
            except Exception as error:
                print(f"FM24 click could not be handled: {error}", flush=True)

    threading.Thread(target=record_loop, name="fm24-xrecord", daemon=True).start()
    if not ready.wait(3) or startup_error:
        raise RuntimeError(f"FM24 X11 click listener could not start: {startup_error[0] if startup_error else 'timed out'}")
    threading.Thread(target=worker_loop, name="fm24-click-worker", daemon=True).start()
