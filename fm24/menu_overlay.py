#!/usr/bin/env python3
"""Temporary X11 row below FM24's open player Overview menu."""

from __future__ import annotations

import argparse
import os
import tkinter as tk

from Xlib import X, display

from read_profile import capture_window


def menu_bottom(image) -> int | None:
    """Find the left edge of the tested skin's open Overview popup."""
    if image.size != (1854, 1011):
        return None
    pixels = image.load()
    if any(max(pixels[178, y]) - min(pixels[178, y]) > 1 for y in (105, 125, 145)):
        return None
    missed = 0
    for y in range(150, 600):
        rgb = pixels[178, y]
        inside = max(rgb) - min(rgb) <= 1 and 35 <= rgb[0] <= 85
        missed = 0 if inside else missed + 1
        if missed >= 4:
            bottom = y - 3
            return bottom if 210 <= bottom <= 590 else None
    return None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--window-id", required=True)
    parser.add_argument("--bottom", required=True, type=int)
    parser.add_argument("--parent-pid", required=True, type=int)
    parser.add_argument("--label", default="Generate face")
    args = parser.parse_args()
    width = 284 if "squad" in args.label.casefold() else 236
    game_id = int(args.window_id, 16)
    connection = display.Display()
    root = connection.screen().root
    game = connection.create_resource_object("window", game_id)
    active_atom = connection.intern_atom("_NET_ACTIVE_WINDOW")

    window = tk.Tk(className="FMFaceMenu")
    window.overrideredirect(True)
    window.attributes("-topmost", True)
    window.configure(bg="#2b2b2b")
    label = tk.Label(window, text=args.label, anchor="w", padx=29,
                     bg="#2b2b2b", fg="#ffffff", font=("Sans", 9))
    label.pack(expand=True, fill="both")

    def generate(_event=None) -> None:
        print("generate", flush=True)
        window.destroy()

    label.bind("<Enter>", lambda _: label.configure(bg="#333333"))
    label.bind("<Leave>", lambda _: label.configure(bg="#2b2b2b"))
    label.bind("<Button-1>", generate)
    window.bind("<Escape>", lambda _: window.destroy())

    def tick() -> None:
        try:
            if os.getppid() != args.parent_pid:
                window.destroy()
                return
            position = root.translate_coords(game, 0, 0)
            active = root.get_full_property(active_atom, X.AnyPropertyType)
            if not active or active.value[0] not in (game_id, window.winfo_id()):
                window.destroy()
                return
            if menu_bottom(capture_window(args.window_id)) != args.bottom:
                window.destroy()
                return
            window.geometry(f"{width}x28+{position.x + 175}+{position.y + args.bottom}")
        except Exception:
            window.destroy()
            return
        window.after(500, tick)

    window.after(100, tick)
    try:
        window.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
