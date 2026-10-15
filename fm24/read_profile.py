#!/usr/bin/env python3
"""Capture the FM24 game window and read its visible player identity."""

from __future__ import annotations

import argparse
import re
from dataclasses import dataclass
from pathlib import Path

from PIL import Image
from rapidocr import RapidOCR
from Xlib import X, display

ID_LINE = re.compile(r"(?P<role>.+?)\s+-\s+(?P<club>.+?)\s+\(ID:\s*(?P<id>\d{1,20})\)")
SHIRT_NUMBER = re.compile(r"^\d+\.\s*")


@dataclass(frozen=True)
class PlayerIdentity:
    player_id: str
    name: str
    role: str
    club: str


def capture_window(window_id: str) -> Image.Image:
    if not re.fullmatch(r"0x[0-9a-fA-F]+", window_id):
        raise ValueError("Window ID must be a hexadecimal X11 ID, such as 0x6400003.")
    connection = display.Display()
    try:
        window = connection.create_resource_object("window", int(window_id, 16))
        if "steam_app_2252570" not in (window.get_wm_class() or ()):
            raise ValueError("The selected X11 window is not FM24.")
        geometry = window.get_geometry()
        width, height = geometry.width, geometry.height
        if not (800 <= width <= 8192 and 600 <= height <= 8192):
            raise ValueError("Unsupported FM24 window size.")
        # Mutter can leave a maximized Proton window unredirected, making
        # Composite fail. Its decorated frame exposes the same game pixels.
        parent = window.query_tree().parent
        parent_geometry = parent.get_geometry()
        frame_covers_game = parent.get_wm_class() == ("mutter-x11-frames", "mutter-x11-frames") \
            and geometry.x >= 0 and geometry.y >= 0 \
            and geometry.x + width <= parent_geometry.width \
            and geometry.y + height <= parent_geometry.height
        if frame_covers_game:
            result = parent.get_image(geometry.x, geometry.y, width, height,
                                      X.ZPixmap, 0xffffffff)
        else:
            if not connection.has_extension("Composite"):
                raise ValueError("X11 Composite or a readable Mutter frame is required to capture FM24.")
            pixmap = window.composite_name_window_pixmap()
            try:
                result = pixmap.get_image(0, 0, width, height, X.ZPixmap, 0xffffffff)
            finally:
                pixmap.free()
        if len(result.data) != width * height * 4:
            raise ValueError("Unsupported FM24 window pixel format.")
        return Image.frombytes("RGB", (width, height), result.data, "raw", "BGRX")
    finally:
        connection.close()


def read_identity(image: Image.Image, engine: RapidOCR | None = None) -> PlayerIdentity:
    # The identity occupies the top profile title on FM24's 24.4.2 vanilla skin.
    # Crop only this strip so names in the biography cannot be mistaken for the
    # selected player. The crop scales with the game window and is magnified for OCR.
    width, height = image.size
    if width < 800 or height < 600:
        raise ValueError("FM24 window is too small to read the player title.")
    crop = image.crop((int(width * .20), 4, int(width * .60), int(height * .065)))
    crop = crop.resize((crop.width * 3, crop.height * 3))
    result = (engine or RapidOCR())(crop)
    lines = tuple(result.txts or ())
    ids = [match for line in lines if (match := ID_LINE.search(line))]
    names = [SHIRT_NUMBER.sub("", line).strip() for line in lines if not ID_LINE.search(line)]
    if len(ids) != 1 or len(names) != 1 or not names[0]:
        raise ValueError("Could not read one player name and ID in the FM24 title. Enable screen IDs in Preferences and open a player profile.")
    identity = ids[0]
    return PlayerIdentity(identity["id"], names[0],
                          identity["role"].strip(), identity["club"].strip())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--image", type=Path, help="Existing screenshot of the FM24 game window")
    source.add_argument("--window-id", help="X11 ID of the FM24 game window")
    args = parser.parse_args()
    screenshot = Image.open(args.image) if args.image else capture_window(args.window_id)
    print(read_identity(screenshot))


if __name__ == "__main__":
    main()
