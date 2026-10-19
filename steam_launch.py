#!/usr/bin/env python3
"""Set FM Face's Steam launch option for FM24 or FM26, so Play in Steam starts it.

Steam keeps launch options per account in userdata/<account>/config/
localconfig.vdf, under UserLocalConfigStore > Software > Valve > Steam > apps
> <app ID> > LaunchOptions. Steam reads the file when it starts and writes it
back when it quits, so the file is only changed while Steam is closed. Only
accounts that have the game get the option, the rest of the file is kept byte
for byte, and the first change keeps a copy as localconfig.vdf.fmface-backup.
A launch option the player already has is kept: FM Face's part goes in front.

    python3 steam_launch.py fm26 [--remove]
    python3 steam_launch.py fm24 [--remove]
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

GAMES = {
    "fm24": {"app": "2252570", "folder": "Football Manager 2024", "launcher": "run_fmface24.sh",
             "proton": 'WINEDLLOVERRIDES="winmm=n,b"'},
    "fm26": {"app": "3551340", "folder": "Football Manager 26", "launcher": "run_fmface.sh",
             "proton": 'WINEDLLOVERRIDES="winhttp=n,b"'},
}
APPS_PATH = ("userlocalconfigstore", "software", "valve", "steam", "apps")
COMMAND = "%command%"
BACKUP_SUFFIX = ".fmface-backup"


def steam_roots() -> list[Path]:
    roots = []
    if sys.platform == "win32":
        import winreg
        for hive, key, value in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                                 (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")):
            try:
                with winreg.OpenKey(hive, key) as handle:
                    roots.append(Path(winreg.QueryValueEx(handle, value)[0]))
            except OSError:
                pass
    elif sys.platform == "darwin":
        roots.append(Path.home() / "Library" / "Application Support" / "Steam")
    else:
        home = Path.home()
        roots += [home / ".steam" / "steam", home / ".local" / "share" / "Steam",
                  home / ".steam" / "debian-installation",
                  home / ".var" / "app" / "com.valvesoftware.Steam" / ".local" / "share" / "Steam"]
    return roots


def steam_libraries() -> list[Path]:
    """Every Steam library folder (the ones holding steamapps), without duplicates."""
    found: list[Path] = []
    for root in steam_roots():
        candidates = [root]
        try:
            text = (root / "steamapps" / "libraryfolders.vdf").read_text(encoding="utf-8", errors="replace")
            candidates += [Path(match.replace("\\\\", "\\")) for match in re.findall(r'"path"\s+"([^"]+)"', text)]
        except OSError:
            pass
        for library in candidates:
            try:
                resolved = library.resolve()
            except OSError:
                continue
            if (resolved / "steamapps").is_dir() and resolved not in found:
                found.append(resolved)
    return found


def fm_face_option(game: str, game_dir: Path, platform: str = sys.platform) -> str | None:
    """FM Face's part of the launch option, or None where none is needed (Windows)."""
    if platform == "win32":
        return None
    if platform == "darwin":
        return f'"{game_dir / GAMES[game]["launcher"]}"'
    return GAMES[game]["proton"]


def combined(existing: str, prefix: str) -> str:
    """The player's launch option with FM Face's prefix in front of %command%."""
    existing = existing.strip()
    if prefix in existing:
        return existing
    if COMMAND in existing:
        before, after = existing.split(COMMAND, 1)
        return f"{before}{prefix} {COMMAND}{after}"
    return f"{prefix} {COMMAND} {existing}".strip()


def without(existing: str, prefix: str) -> str:
    """The launch option without FM Face's prefix; empty if nothing else was set."""
    option = " ".join(existing.replace(prefix, "").split())
    return "" if option == COMMAND else option


def _tokens(text: str):
    """(kind, start, end, value) for each quoted string and brace; // comments are skipped."""
    index = 0
    while index < len(text):
        char = text[index]
        if char == '"':
            end, value = index + 1, []
            while end < len(text) and text[end] != '"':
                if text[end] == "\\" and end + 1 < len(text):
                    end += 1
                value.append(text[end])
                end += 1
            if end >= len(text):
                raise ValueError("Unterminated string in localconfig.vdf.")
            yield "string", index, end + 1, "".join(value)
            index = end + 1
        elif char in "{}":
            yield char, index, index + 1, char
            index += 1
        elif text.startswith("//", index):
            newline = text.find("\n", index)
            index = len(text) if newline < 0 else newline
        else:
            index += 1


def _escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def _app_block(text: str, app_id: str):
    """(open brace end, close brace start, LaunchOptions value token or None) for the
    app's block under apps, or None if this account doesn't have the game."""
    path: list[str] = []
    key = None
    block = None
    value = None
    for kind, start, end, token in _tokens(text):
        if kind == "string":
            if key is None:
                key = token
            else:
                if (block is not None and len(path) == len(APPS_PATH) + 1 and
                        key.lower() == "launchoptions"):
                    value = (start, end, token)
                key = None
        elif kind == "{":
            if key is None:
                raise ValueError("localconfig.vdf has a block without a name.")
            path.append(key.lower())
            if tuple(path[:-1]) == APPS_PATH and path[-1] == app_id:
                block = end
            key = None
        else:
            if not path:
                raise ValueError("localconfig.vdf has an unmatched brace.")
            if block is not None and len(path) == len(APPS_PATH) + 1:
                return block, start, value
            path.pop()
    return None


def set_launch_option(text: str, app_id: str, prefix: str, *, remove: bool = False) -> str | None:
    """The file's text with FM Face's launch option set (or removed), or None
    when this account doesn't have the game."""
    found = _app_block(text, app_id)
    if found is None:
        return None
    opened, closing, value = found
    existing = value[2] if value else ""
    option = without(existing, prefix) if remove else combined(existing, prefix)
    if value:
        return text[:value[0]] + f'"{_escape(option)}"' + text[value[1]:]
    if not option:
        return text
    line_start = text.rfind("\n", 0, closing) + 1
    indent = text[line_start:closing] if not text[line_start:closing].strip() else ""
    entry = f'{indent}\t"LaunchOptions"\t\t"{_escape(option)}"\n'
    return text[:line_start] + entry + text[line_start:]


def steam_running() -> bool:
    if sys.platform == "win32":
        result = subprocess.run(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/NH"],
                                capture_output=True, text=True, check=False)
        return "steam.exe" in result.stdout.lower()
    name = "steam_osx" if sys.platform == "darwin" else "steam"
    return subprocess.run(["pgrep", "-x", name], capture_output=True, check=False).returncode == 0


def update_accounts(app_id: str, prefix: str, *, remove: bool = False,
                    roots: list[Path] | None = None) -> list[Path]:
    """Change every account's localconfig.vdf that has the game; the files changed."""
    changed = []
    for root in roots if roots is not None else steam_roots():
        for config in sorted((root / "userdata").glob("*/config/localconfig.vdf")):
            text = config.read_text(encoding="utf-8")
            updated = set_launch_option(text, app_id, prefix, remove=remove)
            if updated is None or updated == text:
                continue
            backup = config.with_name(config.name + BACKUP_SUFFIX)
            if not backup.exists():
                shutil.copy2(config, backup)
            temporary = config.with_name(config.name + ".fmface-tmp")
            temporary.write_text(updated, encoding="utf-8")
            os.replace(temporary, config)
            changed.append(config)
    return changed


def find_game_dir(game: str) -> Path | None:
    for library in steam_libraries():
        folder = library / "steamapps" / "common" / GAMES[game]["folder"]
        if folder.is_dir():
            return folder
    return None


def needs_change(game: str, game_dir: Path, *, remove: bool = False,
                 roots: list[Path] | None = None) -> bool:
    """True if some account's launch option for the game isn't as FM Face wants it yet."""
    prefix = fm_face_option(game, game_dir)
    if prefix is None:
        return False
    for root in roots if roots is not None else steam_roots():
        for config in (root / "userdata").glob("*/config/localconfig.vdf"):
            try:
                text = config.read_text(encoding="utf-8")
                updated = set_launch_option(text, GAMES[game]["app"], prefix, remove=remove)
            except (OSError, UnicodeError, ValueError):
                return True
            if updated is not None and updated != text:
                return True
    return False


def apply(game: str, game_dir: Path, *, remove: bool = False) -> str:
    """Set or remove FM Face's launch option; returns a message for the player."""
    prefix = fm_face_option(game, game_dir)
    if prefix is None:
        return "On Windows FM Face needs no Steam launch option."
    manual = f"set it yourself in Steam (Properties > General > Launch Options): {prefix} {COMMAND}"
    if not needs_change(game, game_dir, remove=remove):
        return (f"{GAMES[game]['folder']}'s Steam launch option is already set for FM Face." if not remove else
                "No Steam account had FM Face's launch option.")
    if steam_running():
        return ("Steam is running, so FM Face didn't change its launch option. Quit Steam and run "
                f"the setup again, or {manual}" if not remove else
                "Steam is running; quit it and run this again to remove FM Face's launch option.")
    try:
        changed = update_accounts(GAMES[game]["app"], prefix, remove=remove)
    except (OSError, UnicodeError, ValueError) as exc:
        return f"FM Face couldn't change Steam's launch option ({exc}); {manual}"
    if not changed:
        if remove:
            return "No Steam account had FM Face's launch option."
        return f"No Steam account on this computer had the game's settings to change; {manual}"
    action = "Removed FM Face from" if remove else "Set"
    return f"{action} the Steam launch option for {GAMES[game]['folder']} ({len(changed)} account(s))."


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("game", choices=sorted(GAMES))
    parser.add_argument("--game-dir", type=Path, help="the game folder (found through Steam)")
    parser.add_argument("--remove", action="store_true", help="take FM Face's part out of the launch option")
    args = parser.parse_args(argv)
    game_dir = args.game_dir.expanduser().resolve() if args.game_dir else find_game_dir(args.game)
    if game_dir is None:
        raise SystemExit(f"Couldn't find {GAMES[args.game]['folder']} in your Steam libraries; pass --game-dir.")
    print(apply(args.game, game_dir, remove=args.remove))


if __name__ == "__main__":
    main()
