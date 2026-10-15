"""Reload FM24 graphics through the tested X11 windowed-mode interface."""

from __future__ import annotations

import time

from PIL import Image
from rapidocr import RapidOCR
from Xlib import X, display, protocol
from Xlib.ext import xtest
from Xlib.error import XError

from capture_profile import BASE_SIZE, _ocr, _rect
from capture_staff import read_staff_identity
from read_profile import capture_window, read_identity


GAME_CLASS = "steam_app_2252570"
GAME_TITLE = "Football Manager 2024"


def find_game_window() -> str:
    """Return the one viewable FM24 game window, as a hex X11 window ID."""
    connection = display.Display()
    try:
        found = []
        pending = [connection.screen().root]
        while pending:
            try:
                children = pending.pop().query_tree().children
            except XError:
                continue
            pending.extend(children)
            for window in children:
                try:
                    if (GAME_CLASS in (window.get_wm_class() or ()) and
                            window.get_wm_name() == GAME_TITLE and
                            window.get_attributes().map_state == X.IsViewable):
                        found.append(window.id)
                except XError:
                    continue
    finally:
        connection.close()
    if len(found) != 1:
        raise ValueError("Could not find exactly one visible FM24 game window "
                         f"({len(found)} found); start FM24 or pass --window-id.")
    return hex(found[0])


def _activate_game(window_id: str) -> None:
    connection = display.Display()
    try:
        window = connection.create_resource_object("window", int(window_id, 16))
        classes = window.get_wm_class() or ()
        if "steam_app_2252570" not in classes:
            raise ValueError("The selected X11 window is not FM24.")
        root = connection.screen().root
        event = protocol.event.ClientMessage(
            window=window, client_type=connection.intern_atom("_NET_ACTIVE_WINDOW"),
            data=(32, [1, X.CurrentTime, 0, 0, 0]),
        )
        root.send_event(event, event_mask=X.SubstructureRedirectMask | X.SubstructureNotifyMask)
        connection.sync()
        time.sleep(.35)
        active = root.get_full_property(connection.intern_atom("_NET_ACTIVE_WINDOW"), X.AnyPropertyType)
        if not active or active.value[0] != window.id:
            raise ValueError("Could not bring the FM24 window to the foreground.")
    finally:
        connection.close()


def _click(window_id: str, x: int, y: int) -> None:
    _activate_game(window_id)
    connection = display.Display()
    try:
        root = connection.screen().root
        window = connection.create_resource_object("window", int(window_id, 16))
        position = root.translate_coords(window, 0, 0)
        root.warp_pointer(position.x + x, position.y + y)
        connection.sync()
        time.sleep(.12)
        xtest.fake_input(connection, X.ButtonPress, 1)
        connection.sync()
        time.sleep(.12)
        xtest.fake_input(connection, X.ButtonRelease, 1)
        connection.sync()
        time.sleep(.35)
    finally:
        connection.close()


def _text_center(image: Image.Image, engine: RapidOCR,
                 rect: tuple[int, int, int, int], label: str) -> tuple[int, int]:
    matches = [(x, y) for text, x, y, score in _ocr(_rect(image, rect), engine, scale=3)
               if text.lower() == label.lower() and score >= .8]
    if len(matches) != 1:
        raise ValueError(f"Could not find one {label!r} control in FM24.")
    return round(rect[0] + matches[0][0]), round(rect[1] + matches[0][1])


def _click_labeled(window_id: str, engine: RapidOCR,
                   rect: tuple[int, int, int, int], label: str) -> None:
    for _ in range(20):
        try:
            center = _text_center(capture_window(window_id), engine, rect, label)
        except ValueError:
            time.sleep(.3)
            continue
        _click(window_id, *center)
        return
    raise ValueError(f"Could not find {label!r} in the FM24 interface.")


def _reload_control(window_id: str, engine: RapidOCR) -> None:
    try:
        _click_labeled(window_id, engine, (1000, 900, 1280, 990), "Reload Skin")
        return
    except ValueError:
        image = capture_window(window_id)
        title = [text.upper() for text, _, _, _ in _ocr(_rect(image, (800, 0, 1100, 60)), engine)]
        buttons = [text.lower() for text, _, _, _ in _ocr(_rect(image, (980, 900, 1410, 990)), engine)]
        if "PREFERENCES" not in title or "confirm" not in buttons or "cancel" not in buttons:
            raise ValueError("FM24 Preferences did not show the Reload Skin control.")
        # The checked screen and window size fix this control at the tested position.
        _click(window_id, 1154, 951)


def _preferences_open(image: Image.Image, engine: RapidOCR) -> bool:
    return any(text.strip().upper() == "PREFERENCES" for text, _, _, score in
               _ocr(_rect(image, (800, 0, 1100, 60)), engine) if score >= .8)


def _open_preferences(window_id: str, engine: RapidOCR) -> None:
    # The FM menu and Preferences screen may appear well after the click on a
    # busy save. Verify each transition instead of assuming it completed.
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        image = capture_window(window_id)
        if _preferences_open(image, engine):
            return
        try:
            center = _text_center(image, engine, (1490, 45, 1690, 650), "Preferences")
        except ValueError:
            _click(window_id, 1510, 33)
        else:
            _click(window_id, *center)
        time.sleep(2)
    raise ValueError("FM24 did not open Preferences after repeated menu clicks.")


def _open_id(image: Image.Image, engine: RapidOCR, staff: bool) -> str:
    return read_staff_identity(image, engine)[0] if staff else read_identity(image, engine).player_id


def reload_skin(window_id: str, expected_player_id: str, engine: RapidOCR, *, staff: bool = False) -> None:
    before = capture_window(window_id)
    if before.size != BASE_SIZE:
        raise ValueError("Automatic FM24 reload currently requires the tested 1854×1011 window.")
    if _open_id(before, engine, staff) != expected_player_id:
        raise ValueError("The open FM24 profile changed before graphics reload.")
    _activate_game(window_id)
    _open_preferences(window_id, engine)
    _reload_control(window_id, engine)
    for _ in range(30):
        time.sleep(.5)
        try:
            after = capture_window(window_id)
            if _open_id(after, engine, staff) == expected_player_id:
                return
        except ValueError:
            continue
    raise TimeoutError("FM24 did not return to the player profile after reloading its skin.")
