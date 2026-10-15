"""Small always-visible X11 progress panel for FM24 squad scans and batches."""

from __future__ import annotations

import argparse
import json
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import ttk

from Xlib import display


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--window-id", required=True)
    parser.add_argument("--parent-pid", required=True, type=int)
    parser.add_argument("--stop-label", required=True)
    args = parser.parse_args()
    connection = display.Display()
    root = connection.screen().root
    game = connection.create_resource_object("window", int(args.window_id, 16))
    inbox: queue.Queue[dict] = queue.Queue()

    def read_commands() -> None:
        for line in sys.stdin:
            try:
                inbox.put(json.loads(line))
            except json.JSONDecodeError:
                continue
        inbox.put({"close": True})

    threading.Thread(target=read_commands, daemon=True).start()
    window = tk.Tk(className="FMFaceSquadProgress")
    window.title("FM Face squad")
    window.attributes("-topmost", True)
    window.configure(bg="#252525")
    window.resizable(False, False)
    label = tk.Label(window, text="Starting squad scan…", anchor="w", justify="left",
                     bg="#252525", fg="#ffffff", font=("Sans", 10), wraplength=350)
    label.pack(fill="x", padx=14, pady=(12, 6))
    progress = ttk.Progressbar(window, length=350, mode="indeterminate")
    progress.pack(padx=14, pady=(0, 8))
    progress.start(25)

    def stop() -> None:
        print("stop", flush=True)
        button.configure(state="disabled", text="Stopping…")

    button = tk.Button(window, text=args.stop_label, command=stop)
    button.pack(anchor="e", padx=14, pady=(0, 12))
    window.protocol("WM_DELETE_WINDOW", stop)

    def tick() -> None:
        if os.getppid() != args.parent_pid:
            window.destroy()
            return
        while not inbox.empty():
            command = inbox.get_nowait()
            if command.get("close"):
                window.destroy()
                return
            if "text" in command:
                label.configure(text=str(command["text"])[:180])
            if "percent" in command:
                progress.stop()
                progress.configure(mode="determinate", value=max(0, min(100, int(command["percent"]))))
        try:
            position = root.translate_coords(game, 0, 0)
            geometry = game.get_geometry()
            # Keep the top-right FM menu and its Preferences row unobstructed:
            # skin reload uses that menu after every generated squad face.
            window.geometry(f"380x125+{position.x + geometry.width - 405}+{position.y + geometry.height - 180}")
        except Exception:
            window.destroy()
            return
        window.after(200, tick)

    window.after(100, tick)
    try:
        window.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
