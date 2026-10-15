#!/usr/bin/env python3
"""Install/remove the build-pinned experimental FM24 native menu integration."""

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

SUPPORTED_SHA256 = json.loads(Path(__file__).with_name("supported_build.json").read_text())["sha256"]
PANEL_FILES = {"player": "panels/player/player overview panel.xml",
               "staff": "panels/non_player/non player profile.xml"}
# Earlier versions added a Squad screen "Faces" button here. The squad command
# now lives in FM24's own Squad menu, so an upgrade removes that panel change.
LEGACY_SQUAD_PANEL = "panels/team/team squad.xml"
BACKUP_SUFFIX = ".fmface-native-backup"


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

    def write(self, path: Path, data: bytes, *, private: bool = True) -> None:
        if not path.parent.exists():
            self.mkdir(path.parent)
        self._record(path)
        _atomic_write(path, data, 0o600 if private else 0o644)

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
    descriptor, name = tempfile.mkstemp(prefix=f".{path.name}.fmface-", suffix=".tmp", dir=path.parent)
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
        raise ValueError("Unsupported native FM Face installation manifest.")
    return metadata


def game_running() -> bool:
    for _, executable in running_executables():
        if executable.replace("\\", "/").lower().endswith("/fm.exe"):
            return True
    return False


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
    parser.add_argument("--dll", type=Path, default=Path(__file__).parent / "build/winmm.dll")
    parser.add_argument("--auth-file", type=Path)
    parser.add_argument("--port", type=int, default=8769)
    parser.add_argument("--remove", action="store_true")
    args = parser.parse_args(argv)
    game = args.game_dir.expanduser().resolve()
    skin = args.skin_dir.expanduser().resolve()
    if game_running():
        parser.error("Close FM24 before installing or removing its native DLL.")
    target = game / "winmm.dll"
    config = game / "fmface24.ini"
    manifest = game / "fmface24-install.json"
    try:
        metadata = load_manifest(manifest)
    except (ValueError, KeyError, json.JSONDecodeError) as exc:
        parser.error(f"Could not read {manifest}: {exc}")
    if target.exists() and (not metadata or digest(target) != metadata["dllSha256"]):
        parser.error("There is an unrecognized winmm.dll in this game folder; it will not be overwritten.")
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
                parser.error("No native FM Face installation was found in this folder.")
            _remove(skin, metadata, transaction, [target, config, manifest])
            print("Removed native DLL/configuration and restored the original FM Face panels.")
            return

        if digest(game / "fm.exe") != SUPPORTED_SHA256:
            parser.error("This FM24 executable is not the validated 24.4.2 build; no hook was installed.")
        if not args.auth_file:
            parser.error("--auth-file is required for installation")
        token = args.auth_file.read_text(encoding="ascii").strip()
        if not re.fullmatch(r"[A-Za-z0-9_-]{40,128}", token) or not 1024 <= args.port <= 65535:
            parser.error("Invalid private token or bridge port.")
        dll = args.dll.read_bytes()
        if dll[:2] != b"MZ" or b"FMFACE24_NATIVE_MENU_HOOK_INSTALLED" not in dll:
            parser.error("--dll is not an FM Face native menu build.")

        # Work out every panel's original text before writing anything.
        installed = metadata["panels"] if metadata else {}
        modified = {}
        for kind, name in PANEL_FILES.items():
            panel, backup = skin / name, skin / (name + BACKUP_SUFFIX)
            if name in installed:
                original = backup.read_text(encoding="utf-8-sig")
            elif not panel.exists():
                parser.error(f"The FM Face skin is missing {panel}; build the skin first.")
            elif backup.exists():
                parser.error(f"Unowned panel backup already exists; refusing to replace it: {backup}")
            else:
                original = panel.read_text(encoding="utf-8-sig")
            try:
                modified[name] = modify_panel(original, kind)
            except ValueError as exc:
                parser.error(f"{name}: {exc}")

        # All build, token, source-panel, and existing-install checks precede writes.
        if LEGACY_SQUAD_PANEL in installed:
            _restore_panel(skin, LEGACY_SQUAD_PANEL, installed[LEGACY_SQUAD_PANEL], transaction)
        panels = {}
        for name, text in modified.items():
            panel = skin / name
            if name not in installed:
                transaction.write(skin / (name + BACKUP_SUFFIX), panel.read_bytes())
            transaction.write(panel, text.encode("utf-8"))
            panels[name] = {"sha256": digest(panel), "created": False}
        transaction.write(target, dll, private=False)
        transaction.write(config, f"[fmface]\nenabled=1\nport={args.port}\ntoken={token}\n".encode("ascii"))
        transaction.write(manifest, (json.dumps({"version": 2, "dllSha256": digest(target),
                                                 "panels": panels}, indent=2) + "\n").encode())
    except BaseException as exc:
        if isinstance(exc, SystemExit) and not transaction.changes:
            raise
        failures = transaction.rollback()
        if failures:
            print("FM Face could not undo every change. Restore these files manually:\n  " +
                  "\n  ".join(failures), file=sys.stderr)
        else:
            print("The native FM Face change failed; all files were restored.", file=sys.stderr)
        raise
    print("Installed the FM24 native portrait menus and the Squad menu's "
          "\"Generate missing squad faces…\" item with an authenticated loopback bridge.")
    if LEGACY_SQUAD_PANEL in installed:
        print("Removed the earlier Squad screen Faces button; the command is now in the Squad menu.")
    if sys.platform == "win32":
        print("On Windows, FM24 loads the DLL from its folder; no launch option is needed.")
    else:
        print('Proton launch option: WINEDLLOVERRIDES="winmm=n,b" %command%')
    print("Start the companion with --native-menu (use --preview-only for no-charge tests).")


if __name__ == "__main__":
    main()
