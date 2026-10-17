#!/usr/bin/env python3
"""Set up FM Face for FM24 in one step: find the game, build the FM Face skin
from FM24's own panels and install the native library (the DLL on Windows and
Proton, libfmface24.dylib and run_fmface24.sh on macOS).

The packaged companion runs this itself the first time it starts. From
source: python3 fm24/setup_fm24.py [--game-dir ...] [--user-data-dir ...]
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "native_menu"))
sys.path.insert(0, str(HERE.parent))

import build_skin  # noqa: E402
import game_archive  # noqa: E402
import install as native_install  # noqa: E402
from fmface import ROOT  # noqa: E402

APP_ID = "2252570"
GAME_FOLDER = "Football Manager 2024"
PANELS = {"player": "/panels/player/player overview panel.xml",
          "staff": "/panels/non_player/non player profile.xml"}


def bundled_dll() -> Path:
    """The DLL inside the packaged companion, or the library fm24/native_menu/build.py
    (build_macos.py on macOS) made."""
    # Packaged in its own folder, so the companion itself never loads it as WINMM.
    if hasattr(sys, "_MEIPASS"):
        return Path(sys._MEIPASS) / "fm24-dll" / native_install.LIBRARY
    return native_install.DEFAULT_LIBRARY


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


def find_game_dir() -> Path | None:
    for library in steam_libraries():
        game = library / "steamapps" / "common" / GAME_FOLDER
        if (game / native_install.EXECUTABLE).is_file():
            return game
    return None


def find_user_data_dir(game: Path) -> Path | None:
    """FM24's user-data folder (the one with skins and graphics), not the game folder."""
    if sys.platform == "win32":
        import ctypes
        buffer = ctypes.create_unicode_buffer(260)
        if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buffer) != 0:  # CSIDL_PERSONAL
            return None
        documents = Path(buffer.value)
    elif sys.platform == "darwin":
        documents = Path.home() / "Library" / "Application Support"
    else:
        # Steam/Proton keeps it in the game's Wine prefix beside the library's common folder.
        documents = (game.parent.parent / "compatdata" / APP_ID / "pfx" / "drive_c" / "users" /
                     "steamuser" / "Documents")
    user_data = documents / "Sports Interactive" / GAME_FOLDER
    return user_data if user_data.is_dir() else None


def build_skin_from_game(game: Path, skin_dir: Path) -> None:
    """The FM Face skin, made from the panels in FM24's own panels.fmf."""
    archive = game / "data" / "game" / "panels.fmf"
    index = game_archive.read_index(archive)
    with tempfile.TemporaryDirectory() as scratch:
        sources = {}
        for kind, name in PANELS.items():
            entry = index.get(name.casefold())
            if entry is None:
                raise ValueError(f"FM24's {archive.name} has no {name}; is this FM24 24.4.2?")
            sources[kind] = Path(scratch) / f"{kind}.xml"
            sources[kind].write_bytes(game_archive.read_file(archive, entry))
        build_skin.add_player_panel(sources["player"], skin_dir)
        build_skin.add_staff_panel(sources["staff"], skin_dir)


def installed_token(game: Path) -> str | None:
    """The token in the DLL's settings (fmface24.ini), if FM Face was installed before."""
    try:
        text = (game / "fmface24.ini").read_text(encoding="ascii")
    except (OSError, UnicodeError):
        return None
    match = re.search(r"^token=([A-Za-z0-9_-]{40,128})$", text, re.MULTILINE)
    return match.group(1) if match else None


def is_set_up(game: Path, skin_dir: Path, auth_file: Path) -> bool:
    """True when this library (and on macOS its launcher), the skin and the companion's token are installed."""
    manifest = game / "fmface24-install.json"
    dll = game / native_install.LIBRARY
    try:
        metadata = native_install.load_manifest(manifest)
        return bool(metadata and (skin_dir / "skin_config.xml").is_file() and
                    native_install.digest(dll) == native_install.digest(bundled_dll()) ==
                    metadata["dllSha256"] and
                    installed_token(game) == auth_file.read_text(encoding="ascii").strip() and
                    (not native_install.LAUNCHER or native_install.digest(game / native_install.LAUNCHER) ==
                     native_install.digest(native_install.HERE / native_install.LAUNCHER)))
    except (OSError, ValueError, KeyError):
        return False


def setup(game: Path, user_data: Path, auth_file: Path) -> None:
    skin_dir = user_data / "skins" / "fmface"
    if is_set_up(game, skin_dir, auth_file):
        print("FM Face is already set up for FM24.", flush=True)
        return
    if native_install.game_running():
        raise SystemExit("Close FM24, then run the FM Face setup again.")
    if not bundled_dll().is_file():
        builder = "build_macos.py" if native_install.MACOS else "build.py"
        raise SystemExit(f"The FM Face library is missing: {bundled_dll()}. Build it with fm24/native_menu/{builder}.")
    # A companion in a new folder (an update, say) keeps the token FM24 already has.
    existing = installed_token(game)
    if existing and not auth_file.exists():
        auth_file.parent.mkdir(parents=True, exist_ok=True)
        with open(auth_file, "x", encoding="ascii", opener=lambda path, flags: os.open(path, flags, 0o600)) as handle:
            handle.write(existing + "\n")
    build_skin.ensure_token(auth_file)
    if not (skin_dir / "skin_config.xml").is_file():
        build_skin_from_game(game, skin_dir)
        print(f"Built the FM Face skin in {skin_dir}", flush=True)
    (user_data / "graphics" / "fmface").mkdir(parents=True, exist_ok=True)
    native_install.main(["--game-dir", str(game), "--skin-dir", str(skin_dir),
                         "--auth-file", str(auth_file), "--dll", str(bundled_dll())])
    print("\nIn FM24, open Preferences > Interface and:\n"
          "  1. choose the \"FM Face\" skin;\n"
          "  2. tick \"Show screen IDs in Title Bar to assist skinning\".\n", flush=True)


def resolve_dirs(game_dir: Path | None, user_data_dir: Path | None) -> tuple[Path, Path]:
    game = game_dir.expanduser().resolve() if game_dir else find_game_dir()
    if game is None or not (game / native_install.EXECUTABLE).is_file():
        raise SystemExit("FM Face couldn't find FM24 in your Steam libraries. Run the setup again with "
                         "--game-dir \"<path to Football Manager 2024>\".")
    user_data = user_data_dir.expanduser().resolve() if user_data_dir else find_user_data_dir(game)
    if user_data is None or not user_data.is_dir():
        where = ("~/Library/Application Support/Sports Interactive/Football Manager 2024"
                 if native_install.MACOS else "Documents\\Sports Interactive\\Football Manager 2024")
        raise SystemExit(f"FM Face couldn't find FM24's user-data folder ({where}). "
                         "Start FM24 once to create it, or pass --user-data-dir.")
    return game, user_data


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, help="the Football Manager 2024 game folder (found through Steam)")
    parser.add_argument("--user-data-dir", type=Path,
                        help="FM24's user-data folder, with skins and graphics (found in Documents)")
    parser.add_argument("--auth-file", type=Path, default=ROOT / ".fm24-auth")
    parser.add_argument("--remove", action="store_true", help="uninstall the DLL and restore the skin's panels")
    args = parser.parse_args(argv)
    game, user_data = resolve_dirs(args.game_dir, args.user_data_dir)
    print(f"FM24: {game}\nFM24 user data: {user_data}", flush=True)
    if args.remove:
        native_install.main(["--game-dir", str(game), "--skin-dir", str(user_data / "skins" / "fmface"), "--remove"])
        return
    setup(game, user_data, args.auth_file)


if __name__ == "__main__":
    main()
