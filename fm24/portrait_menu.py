#!/usr/bin/env python3
"""Small X11 menu opened by clicking an FM24 profile portrait."""

from __future__ import annotations

import argparse
import os
import tkinter as tk

from PIL import ImageChops
from Xlib import display

from read_profile import capture_window


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--window-id", required=True)
    parser.add_argument("--parent-pid", required=True, type=int)
    args = parser.parse_args()
    game_id = int(args.window_id, 16)
    connection = display.Display()
    root = connection.screen().root
    game = connection.create_resource_object("window", game_id)
    title = None

    window = tk.Tk(className="FMFacePortraitMenu")
    # Let Mutter manage stacking under Wayland/Xwayland. An override-redirect
    # window can be mapped in X11 yet absent from the composited game view.
    window.attributes("-type", "popup_menu")
    window.attributes("-topmost", True)
    window.configure(bg="#2b2b2b", highlightbackground="#777777", highlightthickness=1)
    position = root.translate_coords(game, 0, 0)
    window.geometry(f"190x30+{position.x + 178}+{position.y + 203}")
    label = tk.Label(window, text="Generate new face", anchor="w", padx=11,
                     bg="#2b2b2b", fg="#ffffff", font=("Sans", 9))
    label.pack(expand=True, fill="both")

    def generate(_event=None) -> None:
        print("generate", flush=True)
        window.destroy()

    label.bind("<Enter>", lambda _: label.configure(bg="#3a3a3a"))
    label.bind("<Leave>", lambda _: label.configure(bg="#2b2b2b"))
    label.bind("<Button-1>", generate)
    window.bind("<Escape>", lambda _: window.destroy())

    def tick() -> None:
        nonlocal title
        try:
            if os.getppid() != args.parent_pid:
                window.destroy()
                return
            current = capture_window(args.window_id)
            if current.size != (1854, 1011):
                window.destroy()
                return
            current_title = current.crop((320, 0, 1200, 65))
            if title is not None and ImageChops.difference(title, current_title).getbbox():
                window.destroy()
                return
            title = current_title
            position = root.translate_coords(game, 0, 0)
            window.geometry(f"190x30+{position.x + 178}+{position.y + 203}")
        except Exception:
            window.destroy()
            return
        window.after(1000, tick)

    window.after(100, tick)
    try:
        window.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
