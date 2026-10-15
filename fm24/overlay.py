#!/usr/bin/env python3
"""Small X11 progress window positioned over the FM24 profile portrait."""

from __future__ import annotations

import argparse
import select
import sys
import time
import tkinter as tk

from Xlib import display


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--window-id", required=True)
    parser.add_argument("--timeout", type=int, default=240)
    args = parser.parse_args()
    game_id = int(args.window_id, 16)
    connection = display.Display()
    game = connection.create_resource_object("window", game_id)
    root_window = connection.screen().root
    active_atom = connection.intern_atom("_NET_ACTIVE_WINDOW")

    window = tk.Tk(className="FMFaceProgress")
    window.overrideredirect(True)
    window.attributes("-topmost", True)
    window.attributes("-alpha", .86)
    window.configure(bg="#15191d")
    label = tk.Label(window, text="Generating\nface", fg="#ffab21", bg="#15191d",
                     font=("Sans", 10, "bold"), justify="center")
    label.pack(expand=True, fill="both")
    window.geometry("112x106+0+0")
    started = time.monotonic()
    frames = ("◐", "◓", "◑", "◒")
    frame = 0
    mode = "generate"

    def tick() -> None:
        nonlocal frame, mode
        if time.monotonic() - started > args.timeout:
            window.destroy()
            return
        if select.select([sys.stdin], [], [], 0)[0]:
            command = sys.stdin.readline().strip()
            if command == "hide" or not command:
                window.destroy()
                return
            if command == "error":
                mode = "error"
                label.configure(text="Face failed\nSee log", fg="#ffffff", bg="#8b1f1f")
                window.configure(bg="#8b1f1f")
                window.after(12000, window.destroy)
            elif command == "reload":
                mode = "reload"
                label.configure(text="Loading\nface…")
        try:
            position = root_window.translate_coords(game, 0, 0)
            active = root_window.get_full_property(active_atom, 0)
            visible = active and active.value[0] in (game_id, window.winfo_id())
            if visible:
                window.geometry(f"112x106+{position.x + 170}+{position.y + 101}")
                window.deiconify()
            else:
                window.withdraw()
        except Exception:
            window.destroy()
            return
        if mode == "generate":
            frame = (frame + 1) % len(frames)
            label.configure(text=f"{frames[frame]}\nGenerating face")
        window.after(350, tick)

    window.after(100, tick)
    try:
        window.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
