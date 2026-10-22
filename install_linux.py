#!/usr/bin/env python3
"""Install or remove RegenerateFM on Linux (Steam/Proton): FM26, the FM26 Demo and FM24.

The release's install-linux.sh runs this with the system's python3, which is
all it needs. With Steam quit and the games closed, it:

  FM26, FM26 Demo  copies the release's FM26 folder (BepInEx and the plugin) into
                   the game folder, and sets the launch option that makes Proton
                   load BepInEx's winhttp.dll
  FM24             copies the packaged companion to ~/.local/share/RegenerateFM/FM24,
                   so the downloaded release can be deleted, and runs
                   fm24/setup_fm24.py for it

Faces are kept: they are in the games' user-data folders, inside their Proton
prefixes.

    ./install-linux.sh [fm26] [fm26demo] [fm24] [--game-dir DIR] [--remove]

Without a game name it sets up every one it finds in the Steam libraries.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "fm24"))

import steam_launch  # noqa: E402
from process_memory import running_executables  # noqa: E402

GAMES = ("fm26", "fm26demo", "fm24")
PLUGIN = "BepInEx/plugins/RegenerateFMHotReload"
# What the FM26 folder adds beside fm.exe. Faces and settings are in FM26's
# user-data folder, so removing these keeps them.
FM26_ADDED = ("BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version")
# The packaged companion's own files. A .env, its log and the key it saved stay.
COMPANION_FILES = ("regeneratefm-fm24.exe", "_internal", ".fm24-auth")
QUIT_STEAM = ("Steam is running. Quit Steam first (Steam > Exit, not just closing its window), then run "
              "install-linux.sh again: Steam only keeps a game's launch option set while it is closed.")


class InstallError(Exception):
    pass


def name(game: str) -> str:
    return steam_launch.GAMES[game]["folder"]


def find_game(game: str) -> Path | None:
    for library in steam_launch.steam_libraries():
        folder = library / "steamapps" / "common" / name(game)
        if (folder / "fm.exe").is_file():
            return folder
    return None


def game_running(folder: Path) -> bool:
    """The game in this folder is running (FM24 and FM26 both run fm.exe)."""
    suffix = f"/{folder.name}/fm.exe".lower()
    return any(path.replace("\\", "/").lower().endswith(suffix) for _, path in running_executables())


def companion_dir() -> Path:
    data = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
    return Path(data) / "RegenerateFM" / "FM24"


def copy_tree(source: Path, target: Path, *, skip: tuple[str, ...] = ()) -> int:
    """Copy source's files over target's, each replaced in one step; the number copied."""
    copied = 0
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source)
        if relative.parts[0] in skip or path.is_dir():
            continue
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_name(f".{destination.name}.regeneratefm-tmp")
        shutil.copy2(path, temporary)
        os.replace(temporary, destination)
        copied += 1
    return copied


def remove(path: Path) -> None:
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    elif path.exists() or path.is_symlink():
        path.unlink()


def installed(game: str, folder: Path) -> bool:
    return (folder / "regeneratefm24-install.json").is_file() if game == "fm24" else (folder / PLUGIN).is_dir()


def install_fm26(game: str, folder: Path, release: Path) -> list[str]:
    payload = release / "FM26"
    if not (payload / PLUGIN / "RegenerateFMHotReload.dll").is_file():
        raise InstallError(f"The release's FM26 folder is missing: {payload}")
    return [f"Copied {copy_tree(payload, folder)} files into {folder}."]


def remove_fm26(folder: Path) -> list[str]:
    """Remove what install_fm26 added. Another mod's BepInEx plugins are kept,
    with BepInEx, and then only RegenerateFM's plugin goes."""
    plugins = folder / "BepInEx" / "plugins"
    others = sorted(entry.name for entry in plugins.iterdir()
                    if entry.name != "RegenerateFMHotReload" and not entry.name.startswith(".")) \
        if plugins.is_dir() else []
    if others:
        remove(folder / PLUGIN)
        return ["Removed the RegenerateFM plugin. BepInEx was left in place because it has other plugins: "
                + ", ".join(others) + "."]
    for added in FM26_ADDED:
        remove(folder / added)
    return [f"Removed RegenerateFM from {folder.name}. Your faces are kept in FM26's user-data folder."]


def run_fm24_setup(argv: list[str]) -> None:
    import setup_fm24
    try:
        setup_fm24.main(argv)
    except SystemExit as exc:
        if exc.code not in (None, 0):
            raise InstallError(str(exc.code)) from None


def install_fm24(folder: Path, release: Path) -> list[str]:
    payload = release / "FM24"
    if not (payload / "regeneratefm-fm24.exe").is_file():
        raise InstallError(f"The release's FM24 folder is missing: {payload}")
    target = companion_dir()
    copied = copy_tree(payload, target, skip=("install-linux.sh",))
    print(f"Copied the companion ({copied} files) to {target}.", flush=True)
    run_fm24_setup(["--game-dir", str(folder), "--companion", str(target / "regeneratefm-fm24.exe")])
    return []


def remove_fm24(folder: Path) -> list[str]:
    run_fm24_setup(["--game-dir", str(folder), "--remove"])
    target = companion_dir()
    if not (target / "regeneratefm-fm24.exe").is_file():
        return []
    for entry in COMPANION_FILES:
        remove(target / entry)
    for directory in (target, target.parent):
        try:
            directory.rmdir()
        except OSError:
            break
    return [f"Removed the companion from {target}."]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    # Not choices=: Python before 3.12 checks an empty list against them.
    parser.add_argument("games", nargs="*", metavar="game", help="fm26, fm26demo or fm24 (default: every one found)")
    parser.add_argument("--game-dir", type=Path, help="the game folder, with one game named (found through Steam)")
    parser.add_argument("--release", type=Path, default=HERE.parent,
                        help="the unpacked release, with the FM26 and FM24 folders (default: beside source)")
    parser.add_argument("--remove", action="store_true", help="uninstall RegenerateFM and its launch options")
    args = parser.parse_args(argv)
    if unknown := [game for game in args.games if game not in GAMES]:
        parser.error(f"Unknown game {unknown[0]!r}: name fm26, fm26demo or fm24.")
    if args.game_dir and len(args.games) != 1:
        parser.error("--game-dir needs exactly one game: fm26, fm26demo or fm24.")
    release = args.release.expanduser().resolve()

    found = {}
    for game in args.games or GAMES:
        folder = args.game_dir.expanduser().resolve() if args.game_dir else find_game(game)
        if folder is not None and (folder / "fm.exe").is_file():
            found[game] = folder
        elif args.games:
            parser.error(f"Couldn't find {name(game)} in your Steam libraries; pass --game-dir.")
    if not found:
        parser.error("Couldn't find FM26, the FM26 Demo or FM24 in your Steam libraries. Name the game and "
                     "pass --game-dir \"<its folder>\".")
    if args.remove:
        found = {game: folder for game, folder in found.items() if installed(game, folder)}
        if not found:
            print("RegenerateFM isn't installed in any game found.")
            return 0
    # Check everything before changing anything.
    running = [name(game) for game, folder in found.items() if game_running(folder)]
    if running:
        raise SystemExit(f"Close {' and '.join(running)}, then run install-linux.sh again.")
    if steam_launch.steam_running() and any(steam_launch.needs_change(game, folder, remove=args.remove)
                                            for game, folder in found.items()):
        raise SystemExit(QUIT_STEAM)

    failed = []
    for game, folder in found.items():
        print(f"\n{name(game)}: {folder}", flush=True)
        try:
            if game == "fm24":
                lines = remove_fm24(folder) if args.remove else install_fm24(folder, release)
            else:
                # Without BepInEx, Steam couldn't start the game with the launch option still set.
                if args.remove:
                    print(steam_launch.apply(game, folder, remove=True), flush=True)
                lines = remove_fm26(folder) if args.remove else install_fm26(game, folder, release)
                if not args.remove:
                    lines.append(steam_launch.apply(game, folder))
        except (InstallError, OSError) as exc:
            print(f"RegenerateFM couldn't {'remove' if args.remove else 'install'} it: {exc}", file=sys.stderr)
            failed.append(name(game))
            continue
        if lines:
            print("\n".join(lines), flush=True)
    if failed:
        print(f"\nNot done: {', '.join(failed)}.", file=sys.stderr)
        return 1
    if not args.remove:
        print("\nStart Steam and press Play. The first time you choose a face command, RegenerateFM asks for "
              "your OpenAI key or a RegenerateFM sign-in.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
