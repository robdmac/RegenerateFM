#!/usr/bin/env python3
"""Set up RegenerateFM for FM24 in one step: find the game, build the optional FM
Face skin from FM24's own panels and install the native library (the DLL on
Windows and Proton, libregeneratefm24.dylib and run_regeneratefm24.sh on macOS).

The packaged companion runs this itself the first time it starts, and the DLL
then starts that companion with FM24. On Linux, the release's
install-linux.sh runs it with plain python3 (no packages needed) for the
packaged companion, which runs inside FM24's Proton prefix:
    python3 fm24/setup_fm24.py --companion <path to regeneratefm-fm24.exe>
From source: python3 fm24/setup_fm24.py [--game-dir ...] [--user-data-dir ...]
"""

from __future__ import annotations

import argparse
import os
import platform
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "native_menu"))
sys.path.insert(0, str(HERE.parent))

import build_skin  # noqa: E402
import install as native_install  # noqa: E402
from skin import modify_panel  # noqa: E402
import steam_launch  # noqa: E402
from regeneratefm import ROOT  # noqa: E402
from steam_launch import steam_libraries, steam_roots  # noqa: E402,F401

APP_ID = "2252570"
GAME_FOLDER = "Football Manager 2024"
PANELS = {"player": "/panels/player/player overview panel.xml",
          "staff": "/panels/non_player/non player profile.xml"}


def bundled_dll(companion: Path | None = None) -> Path:
    """The DLL inside the packaged companion (this one, or the one being set
    up from Linux), or the library fm24/native_menu/build.py (build_macos.py
    on macOS) made."""
    # Packaged in its own folder, so the companion itself never loads it as WINMM.
    if hasattr(sys, "_MEIPASS"):
        return Path(sys._MEIPASS) / "fm24-dll" / native_install.LIBRARY
    if companion is not None:
        packaged = companion.parent / "_internal" / "fm24-dll" / native_install.LIBRARY
        if packaged.is_file():
            return packaged
    return native_install.DEFAULT_LIBRARY


def packaged_companion() -> Path | None:
    """This packaged companion, which FM24 then starts itself; on macOS, this
    source's companion.py, which run_regeneratefm24.sh starts with this Python."""
    if sys.platform == "darwin":
        return HERE / "companion.py"
    return Path(sys.executable).resolve() if getattr(sys, "frozen", False) and sys.platform == "win32" else None


def windows_path(companion: Path) -> str:
    """The companion's path as FM24 sees it: as is on Windows and macOS, under
    Wine's Z: drive on Linux."""
    if sys.platform in ("win32", "darwin"):
        return str(companion)
    return "Z:" + str(companion).replace("/", "\\")


# The companion's packages (fm24/requirements.txt), which the Python FM24
# starts it with on macOS needs.
COMPANION_MODULES = ("PIL", "zstandard")


def missing_modules() -> list[str]:
    import importlib.util
    return [name for name in COMPANION_MODULES if importlib.util.find_spec(name) is None]


def find_game_dir() -> Path | None:
    for library in steam_launch.steam_libraries():
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
    """The RegenerateFM skin, made from the panels in FM24's own panels.fmf.
    Raises ImportError without the zstandard package."""
    import game_archive
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


def build_skin_with_popups(game: Path, skin_dir: Path) -> bool:
    """With FM24 running, from the companion it started: the RegenerateFM skin and
    its portrait popups, if the setup couldn't build it (plain python3 on
    Linux). The installer adopts these panels the next time it runs. True if
    it was built now."""
    if (skin_dir / "skin_config.xml").is_file():
        return False
    build_skin_from_game(game, skin_dir)
    for kind, name in native_install.PANEL_FILES.items():
        panel = skin_dir / name
        panel.write_text(modify_panel(panel.read_text(encoding="utf-8-sig"), kind), encoding="utf-8")
    return True


def installed_config(game: Path) -> dict[str, str]:
    """The DLL's settings (regeneratefm24.ini), if RegenerateFM was installed before."""
    try:
        return native_install.read_config(game / "regeneratefm24.ini")
    except (OSError, UnicodeError, ValueError):
        return {}


def installed_token(game: Path) -> str | None:
    """The token in the DLL's settings, if RegenerateFM was installed before."""
    token = installed_config(game).get("token", "")
    return token if re.fullmatch(r"[A-Za-z0-9_-]{40,128}", token) else None


def is_set_up(game: Path, skin_dir: Path, auth_file: Path, companion: Path | None = None) -> bool:
    """True when this library (and on macOS its launcher), the skin, the
    companion's token and the companion FM24 starts are installed."""
    manifest = game / "regeneratefm24-install.json"
    dll = game / native_install.LIBRARY
    try:
        metadata = native_install.load_manifest(manifest)
        return bool(metadata and (skin_dir / "skin_config.xml").is_file() and
                    native_install.digest(dll) == native_install.digest(bundled_dll(companion)) ==
                    metadata["dllSha256"] and
                    (companion is None or installed_config(game).get("companion") == windows_path(companion)) and
                    (not native_install.MACOS or (installed_config(game).get("python"),
                                                  installed_config(game).get("python_arch")) ==
                     (sys.executable, platform.machine())) and
                    installed_token(game) == auth_file.read_text(encoding="ascii").strip() and
                    (not native_install.LAUNCHER or native_install.digest(game / native_install.LAUNCHER) ==
                     native_install.digest(native_install.HERE / native_install.LAUNCHER)))
    except (OSError, ValueError, KeyError):
        return False


QUIT_STEAM = ("Steam is running. Quit Steam first (Steam > Exit, not just closing its window), "
              "then run the RegenerateFM setup again: Steam only keeps a game's launch option set while it is closed.")


def check_steam_closed(game: Path, *, remove: bool = False) -> None:
    """On Linux and macOS, FM24's launch option can only be set with Steam closed."""
    if sys.platform != "win32" and steam_launch.steam_running() and \
            steam_launch.needs_change("fm24", game, remove=remove):
        raise SystemExit(QUIT_STEAM)


def setup(game: Path, user_data: Path, auth_file: Path, companion: Path | None = None) -> None:
    skin_dir = user_data / "skins" / "regeneratefm"
    check_steam_closed(game)
    if is_set_up(game, skin_dir, auth_file, companion):
        print("RegenerateFM is already set up for FM24.", flush=True)
        _launch_option(game)
        return
    if native_install.game_running(game):
        raise SystemExit("Close FM24, then run the RegenerateFM setup again.")
    dll = bundled_dll(companion)
    if not dll.is_file():
        builder = "build_macos.py" if native_install.MACOS else "build.py"
        raise SystemExit(f"The RegenerateFM library is missing: {dll}. Build it with fm24/native_menu/{builder}.")
    # A companion in a new folder (an update, say) keeps the token FM24 already has.
    existing = installed_token(game)
    if existing and not auth_file.exists():
        auth_file.parent.mkdir(parents=True, exist_ok=True)
        with open(auth_file, "x", encoding="ascii", opener=lambda path, flags: os.open(path, flags, 0o600)) as handle:
            handle.write(existing + "\n")
    build_skin.ensure_token(auth_file)
    if not (skin_dir / "skin_config.xml").is_file():
        try:
            build_skin_from_game(game, skin_dir)
            print(f"Built the RegenerateFM skin in {skin_dir}", flush=True)
        except ImportError:
            # Plain python3 on Linux: the companion builds it when FM24 first starts it.
            print("The optional RegenerateFM skin will be built when FM24 first starts.", flush=True)
    (user_data / "graphics" / "regeneratefm").mkdir(parents=True, exist_ok=True)
    command = ["--game-dir", str(game), "--skin-dir", str(skin_dir), "--auth-file", str(auth_file), "--dll", str(dll)]
    if companion is not None:
        command += ["--companion", windows_path(companion)]
    if native_install.MACOS:
        # Steam runs the launcher under Rosetta; the companion must run as this
        # Python does now, which its packages were installed for.
        command += ["--python", sys.executable, "--python-arch", platform.machine()]
        if missing := missing_modules():
            print(f"FM24 starts the companion with {sys.executable}, which lacks {', '.join(missing)}. "
                  "Install them (pip install -r fm24/requirements.txt), or run this setup with a Python "
                  "that has them.", flush=True)
    native_install.main(command)
    _launch_option(game)
    print("\nIn FM24, Generate new face and Update face are in a profile's Overview tab menu, with any skin.\n"
          "For them on the portrait too, choose the \"RegenerateFM\" skin in Preferences > Interface.\n", flush=True)


def _launch_option(game: Path, *, remove: bool = False) -> None:
    """Steam's launch option for FM24 (macOS and Proton), so Play starts RegenerateFM."""
    if sys.platform != "win32":
        print(steam_launch.apply("fm24", game, remove=remove), flush=True)


def resolve_dirs(game_dir: Path | None, user_data_dir: Path | None) -> tuple[Path, Path]:
    game = game_dir.expanduser().resolve() if game_dir else find_game_dir()
    if game is None or not (game / native_install.EXECUTABLE).is_file():
        raise SystemExit("RegenerateFM couldn't find FM24 in your Steam libraries. Run the setup again with "
                         "--game-dir \"<path to Football Manager 2024>\".")
    user_data = user_data_dir.expanduser().resolve() if user_data_dir else find_user_data_dir(game)
    if user_data is None or not user_data.is_dir():
        where = ("~/Library/Application Support/Sports Interactive/Football Manager 2024"
                 if native_install.MACOS else "Documents\\Sports Interactive\\Football Manager 2024")
        raise SystemExit(f"RegenerateFM couldn't find FM24's user-data folder ({where}). "
                         "Start FM24 once to create it, or pass --user-data-dir.")
    return game, user_data


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, help="the Football Manager 2024 game folder (found through Steam)")
    parser.add_argument("--user-data-dir", type=Path,
                        help="FM24's user-data folder, with skins and graphics (found in Documents)")
    parser.add_argument("--auth-file", type=Path,
                        help="the companion's token file (default: .fm24-auth beside the companion)")
    parser.add_argument("--companion", type=Path,
                        help="the packaged regeneratefm-fm24.exe for FM24 to start (Linux; the packaged companion sets itself)")
    parser.add_argument("--remove", action="store_true", help="uninstall the DLL and restore the skin's panels")
    args = parser.parse_args(argv)
    companion = args.companion.expanduser().resolve() if args.companion else packaged_companion()
    if companion is not None and not companion.is_file():
        raise SystemExit(f"There is no RegenerateFM companion at {companion}.")
    # The packaged companion keeps its token beside itself; from source, beside RegenerateFM.
    packaged = companion is not None and companion.suffix != ".py"
    auth_file = args.auth_file or (companion.parent if packaged else ROOT) / ".fm24-auth"
    game, user_data = resolve_dirs(args.game_dir, args.user_data_dir)
    print(f"FM24: {game}\nFM24 user data: {user_data}", flush=True)
    if args.remove:
        check_steam_closed(game, remove=True)
        native_install.main(["--game-dir", str(game), "--skin-dir", str(user_data / "skins" / "regeneratefm"), "--remove"])
        _launch_option(game, remove=True)
        return
    setup(game, user_data, auth_file, companion)


if __name__ == "__main__":
    main()
