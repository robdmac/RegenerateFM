#!/usr/bin/env python3
"""Install/remove the build-pinned experimental FM24 native menu integration.

On Windows and Proton it is winmm.dll beside fm.exe. On macOS it is
libregeneratefm24.dylib beside fm.app, which FM24 loads through run_regeneratefm24.sh
(its Steam launch option)."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

from skin import modify_panel

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from process_memory import running_executables

HERE = Path(__file__).resolve().parent


def use_system(macos: bool) -> None:
    """Install for FM24 for macOS (the dylib and its launcher) or for fm.exe
    (the DLL, on Windows and Proton). Chosen from this system at import."""
    global MACOS, SUPPORTED_SHA256, EXECUTABLE, LIBRARY, DEFAULT_LIBRARY, LAUNCHER
    MACOS = macos
    SUPPORTED_SHA256 = json.loads((HERE / ("supported_build_macos.json" if macos else "supported_build.json"))
                                  .read_text())["sha256"]
    EXECUTABLE = "fm.app/Contents/MacOS/fm" if macos else "fm.exe"
    LIBRARY = "libregeneratefm24.dylib" if macos else "winmm.dll"
    DEFAULT_LIBRARY = HERE / ("build-macos/libregeneratefm24.dylib" if macos else "build/winmm.dll")
    LAUNCHER = "run_regeneratefm24.sh" if macos else None


use_system(sys.platform == "darwin")
PANEL_FILES = {"player": "panels/player/player overview panel.xml",
               "staff": "panels/non_player/non player profile.xml"}
# Earlier versions added a Squad screen "Faces" button here. The squad command
# now lives in FM24's own Squad menu, so an upgrade removes that panel change.
LEGACY_SQUAD_PANEL = "panels/team/team squad.xml"
BACKUP_SUFFIX = ".regeneratefm-native-backup"
# FM Face, RegenerateFM's earlier name, installed the same DLL under these
# names, with its own skin. Installing RegenerateFM takes it out.
FMFACE = {"config": "fmface24.ini", "manifest": "fmface24-install.json",
          "skin": "fmface", "backup": ".fmface-native-backup"}


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Transaction:
    """Journal every file change so a failed install or removal is undone.

    Each write is atomic (temporary file, then rename). On failure the journal
    is replayed in reverse, restoring prior bytes and removing new files.
    """

    def __init__(self):
        self.changes: list[tuple[str, Path, bytes | None, int]] = []

    def _record(self, path: Path) -> None:
        if path.exists():
            self.changes.append(("file", path, path.read_bytes(), path.stat().st_mode & 0o777))
        else:
            self.changes.append(("file", path, None, 0))

    def write(self, path: Path, data: bytes, *, private: bool = True, mode: int | None = None) -> None:
        if not path.parent.exists():
            self.mkdir(path.parent)
        self._record(path)
        _atomic_write(path, data, mode if mode is not None else 0o600 if private else 0o644)

    def remove(self, path: Path) -> None:
        if not path.exists():
            return
        self._record(path)
        path.unlink()

    def mkdir(self, path: Path) -> None:
        if not path.parent.exists():
            self.mkdir(path.parent)
        path.mkdir()
        self.changes.append(("dir", path, None, 0))

    def remove_empty_dirs(self, path: Path, stop: Path) -> None:
        while path != stop and path.is_dir() and not any(path.iterdir()):
            mode = path.stat().st_mode & 0o777
            path.rmdir()
            self.changes.append(("rmdir", path, None, mode))
            path = path.parent

    def rollback(self) -> list[str]:
        failures = []
        for kind, path, previous, mode in reversed(self.changes):
            try:
                if kind == "dir":
                    path.rmdir()
                elif kind == "rmdir":
                    path.mkdir()
                    path.chmod(mode)
                elif previous is None:
                    path.unlink(missing_ok=True)
                else:
                    _atomic_write(path, previous, mode)
            except OSError as exc:
                failures.append(f"{path}: {exc}")
        self.changes.clear()
        return failures


def _atomic_write(path: Path, data: bytes, mode: int) -> None:
    descriptor, name = tempfile.mkstemp(prefix=f".{path.name}.regeneratefm-", suffix=".tmp", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


def load_manifest(path: Path) -> dict | None:
    """Read a manifest; upgrade the original two-panel format in memory."""
    if not path.exists():
        return None
    metadata = json.loads(path.read_text())
    if "version" not in metadata:
        metadata = {"version": 2, "dllSha256": metadata["dllSha256"],
                    "panels": {PANEL_FILES[kind]: {"sha256": value, "created": False}
                               for kind, value in zip(("player", "staff"), metadata["panelSha256"])}}
    if metadata.get("version") != 2:
        raise ValueError("Unsupported native RegenerateFM installation manifest.")
    return metadata


def has_popup(panel: Path) -> bool:
    return any(f'id="{widget}"' in panel.read_text(encoding="utf-8-sig") for widget in ("fmnP", "fmnS"))


def config_text(port: int, token: str, companion: str | None, python: str | None = None,
                python_arch: str | None = None) -> bytes:
    """regeneratefm24.ini. The DLL reads it with GetPrivateProfileString, which
    takes UTF-16 with a byte-order mark, so any companion path works. On
    macOS it is UTF-8: the library reads only its port and token, and
    run_regeneratefm24.sh starts the companion (companion.py) with ``python``,
    as ``python_arch`` (arm64 or x86_64, which its packages are built for)."""
    text = f"[regeneratefm]\nenabled=1\nport={port}\ntoken={token}\n"
    for key, value in (("companion", companion), ("python", python if MACOS else None),
                       ("python_arch", python_arch if MACOS else None)):
        if value:
            if "\n" in value or "\r" in value:
                raise ValueError(f"Invalid {key} path.")
            text += f"{key}={value}\n"
    if MACOS:
        return text.encode("utf-8")
    return text.replace("\n", "\r\n").encode("utf-16")


def read_config(path: Path) -> dict[str, str]:
    """regeneratefm24.ini's settings, in either encoding."""
    data = path.read_bytes()
    text = data.decode("utf-16") if data[:2] in (b"\xff\xfe", b"\xfe\xff") else data.decode("utf-8")
    return dict(line.split("=", 1) for line in text.splitlines() if "=" in line)


def game_running(game: Path) -> bool:
    """FM24 from this folder is running (not FM26, whose executable is fm.exe too)."""
    suffix = f"/{game.name}/{EXECUTABLE}".lower()
    for _, executable in running_executables():
        if executable.replace("\\", "/").lower().endswith(suffix):
            return True
    return False


def _is_library(data: bytes) -> bool:
    """A RegenerateFM build of the native library for this system."""
    magic = data[:4] in (b"\xcf\xfa\xed\xfe",) if MACOS else data[:2] == b"MZ"
    return magic and b"REGENERATEFM24_NATIVE_MENU_HOOK_INSTALLED" in data


def _is_fmface_library(data: bytes) -> bool:
    return data[:2] == b"MZ" and b"FMFACE24_" in data


def fmface_install(game: Path) -> dict | None:
    """An FM Face install of the DLL in this game folder (Windows and Proton):
    its manifest, or an empty one if only its DLL is left."""
    if MACOS or not (game / LIBRARY).is_file():
        return None
    manifest = game / FMFACE["manifest"]
    try:
        metadata = load_manifest(manifest) if manifest.is_file() else None
    except (ValueError, KeyError, json.JSONDecodeError):
        metadata = None
    if metadata and digest(game / LIBRARY) == metadata["dllSha256"]:
        return metadata
    return {"panels": {}} if _is_fmface_library((game / LIBRARY).read_bytes()) else None


def _remove_fmface(game: Path, skin: Path, metadata: dict, transaction: Transaction) -> list[Path]:
    """Restore FM Face's skin panels and remove its DLL and settings; the panels
    edited since, which are left as they are."""
    fmface_skin, kept = skin.parent / FMFACE["skin"], []
    for name, entry in metadata["panels"].items():
        panel, backup = fmface_skin / name, fmface_skin / (name + FMFACE["backup"])
        if not panel.is_file() or digest(panel) != entry["sha256"] or \
                not (entry["created"] or backup.is_file()):
            kept.append(panel)
        elif entry["created"]:
            transaction.remove(panel)
            transaction.remove_empty_dirs(panel.parent, fmface_skin)
        else:
            transaction.write(panel, backup.read_bytes())
            transaction.remove(backup)
    for name in (LIBRARY, FMFACE["config"], FMFACE["manifest"]):
        transaction.remove(game / name)
    return kept


def _restore_panel(skin: Path, name: str, entry: dict, transaction: Transaction) -> None:
    panel = skin / name
    if entry["created"]:
        transaction.remove(panel)
        transaction.remove_empty_dirs(panel.parent, skin)
    else:
        backup = skin / (name + BACKUP_SUFFIX)
        transaction.write(panel, backup.read_bytes())
        transaction.remove(backup)


def _remove(skin: Path, metadata: dict, transaction: Transaction, files: list[Path]) -> None:
    for name, entry in metadata["panels"].items():
        _restore_panel(skin, name, entry, transaction)
    for path in files:
        transaction.remove(path)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--skin-dir", required=True, type=Path)
    # Accepted for old scripts; the squad command no longer needs a panel.
    parser.add_argument("--squad-panel-source", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--dll", type=Path, default=DEFAULT_LIBRARY,
                        help=f"the RegenerateFM library to install (default: {DEFAULT_LIBRARY})")
    parser.add_argument("--auth-file", type=Path)
    parser.add_argument("--port", type=int, default=8769)
    parser.add_argument("--companion", help="the companion FM24 starts: regeneratefm-fm24.exe as Windows sees it, "
                                            "or on macOS fm24/companion.py")
    parser.add_argument("--python", help="macOS: the Python run_regeneratefm24.sh starts the companion with")
    parser.add_argument("--python-arch", choices=("arm64", "x86_64"), help="macOS: the architecture to run it as")
    parser.add_argument("--remove", action="store_true")
    args = parser.parse_args(argv)
    game = args.game_dir.expanduser().resolve()
    skin = args.skin_dir.expanduser().resolve()
    if game_running(game):
        parser.error("Close FM24 before installing or removing its native DLL.")
    target = game / LIBRARY
    launcher = game / LAUNCHER if LAUNCHER else None
    config = game / "regeneratefm24.ini"
    manifest = game / "regeneratefm24-install.json"
    try:
        metadata = load_manifest(manifest)
    except (ValueError, KeyError, json.JSONDecodeError) as exc:
        parser.error(f"Could not read {manifest}: {exc}")
    fmface = None if args.remove or metadata else fmface_install(game)
    if target.exists() and not fmface and (not metadata or digest(target) != metadata["dllSha256"]):
        parser.error(f"There is an unrecognized {LIBRARY} in this game folder; it will not be overwritten.")
    if metadata:
        for name, entry in metadata["panels"].items():
            panel = skin / name
            if not panel.exists() or digest(panel) != entry["sha256"]:
                parser.error(f"The native panel has been edited since installation: {panel}")
            if not entry["created"] and not (skin / (name + BACKUP_SUFFIX)).is_file():
                parser.error(f"Native menu panel backup is missing for {panel}.")

    transaction = Transaction()
    try:
        if args.remove:
            if not metadata:
                parser.error("No native RegenerateFM installation was found in this folder.")
            _remove(skin, metadata, transaction, [target, config, manifest] + ([launcher] if launcher else []))
            print("Removed the native library and configuration and restored the original RegenerateFM panels.")
            if launcher:
                print("FM24's Steam launch option is removed by fm24/setup_fm24.py --remove "
                      "(or steam_launch.py fm24 --remove).")
            return

        if digest(game / EXECUTABLE) != SUPPORTED_SHA256:
            parser.error("This FM24 executable is not the validated 24.4.2 build; no hook was installed.")
        if not args.auth_file:
            parser.error("--auth-file is required for installation")
        token = args.auth_file.read_text(encoding="ascii").strip()
        if not re.fullmatch(r"[A-Za-z0-9_-]{40,128}", token) or not 1024 <= args.port <= (65533 if MACOS else 65535):
            parser.error("Invalid private token or bridge port.")
        dll = args.dll.read_bytes()
        if not _is_library(dll):
            parser.error("--dll is not a RegenerateFM native menu build for this system.")

        # Work out every panel's original text before writing anything.
        installed = metadata["panels"] if metadata else {}
        # The skin is optional: without it (or a panel of it) only the
        # portrait popup is missing. The companion builds it later if needed.
        modified, adopted = {}, {}
        for kind, name in PANEL_FILES.items():
            panel, backup = skin / name, skin / (name + BACKUP_SUFFIX)
            if name in installed:
                original = backup.read_text(encoding="utf-8-sig")
            elif not panel.exists():
                continue
            elif has_popup(panel):
                # Built with the popup by the companion: owned, removed on uninstall.
                adopted[name] = {"sha256": digest(panel), "created": True}
                continue
            elif backup.exists():
                parser.error(f"Unowned panel backup already exists; refusing to replace it: {backup}")
            else:
                original = panel.read_text(encoding="utf-8-sig")
            try:
                modified[name] = modify_panel(original, kind)
            except ValueError as exc:
                parser.error(f"{name}: {exc}")

        # All build, token, source-panel, and existing-install checks precede writes.
        fmface_kept = _remove_fmface(game, skin, fmface, transaction) if fmface else []
        if LEGACY_SQUAD_PANEL in installed:
            _restore_panel(skin, LEGACY_SQUAD_PANEL, installed[LEGACY_SQUAD_PANEL], transaction)
        panels = dict(adopted)
        for name, text in modified.items():
            panel = skin / name
            if name not in installed:
                transaction.write(skin / (name + BACKUP_SUFFIX), panel.read_bytes())
            transaction.write(panel, text.encode("utf-8"))
            panels[name] = {"sha256": digest(panel), "created": False}
        transaction.write(target, dll, private=False)
        if launcher:
            transaction.write(launcher, (HERE / LAUNCHER).read_bytes(), mode=0o755)
        transaction.write(config, config_text(args.port, token, args.companion, args.python, args.python_arch))
        transaction.write(manifest, (json.dumps({"version": 2, "dllSha256": digest(target),
                                                 "panels": panels}, indent=2) + "\n").encode())
    except BaseException as exc:
        if isinstance(exc, SystemExit) and not transaction.changes:
            raise
        failures = transaction.rollback()
        if failures:
            print("RegenerateFM could not undo every change. Restore these files manually:\n  " +
                  "\n  ".join(failures), file=sys.stderr)
        else:
            print("The native RegenerateFM change failed; all files were restored.", file=sys.stderr)
        raise
    print("Installed the FM24 native portrait menus and the Squad menu's "
          "\"Generate missing squad faces...\" item with an authenticated loopback bridge.")
    if fmface:
        print("Removed the earlier FM Face install (its faces are kept).")
        for panel in fmface_kept:
            print(f"Left FM Face's edited panel as it is: {panel}")
    if LEGACY_SQUAD_PANEL in installed:
        print("Removed the earlier Squad screen Faces button; the command is now in the Squad menu.")
    if MACOS:
        print(f'Steam launch option for FM24: "{launcher}" %command%')
    elif sys.platform == "win32":
        print("On Windows, FM24 loads the DLL from its folder; no launch option is needed.")
    elif not args.companion:
        print('Proton launch option: WINEDLLOVERRIDES="winmm=n,b" %command%')
    if args.companion:
        print("FM24 starts the RegenerateFM companion itself.")
    else:
        print("Start the companion while you play (add --preview-only for no-charge tests).")


if __name__ == "__main__":
    main()
