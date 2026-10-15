#!/usr/bin/env python3
"""Install pinned FM26 button dependencies from downloaded release ZIPs."""

from __future__ import annotations

import argparse
import io
import json
import shutil
import sys
import zipfile
from pathlib import Path, PurePosixPath

BUTTON_CONFIG = {
    "app": {"eventsUrl": "http://127.0.0.1:8767/fm-events", "startIfClosed": ""},
    "playerProfile": {
        "buttons": [],
        "embeddedPanels": [],
    },
    "debug": {"logScreenTree": False},
}


def _safe_name(name: str) -> PurePosixPath:
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"Unsafe archive path: {name}")
    return path


def build_plan(loader_zip: Path, actionlink_zip: Path) -> dict[PurePosixPath, bytes]:
    files: dict[PurePosixPath, bytes] = {}
    with zipfile.ZipFile(loader_zip) as loader:
        for info in loader.infolist():
            if info.is_dir():
                continue
            name = _safe_name(info.filename)
            if name in files:
                raise ValueError(f"Duplicate loader path: {name}")
            files[name] = loader.read(info)
    with zipfile.ZipFile(actionlink_zip) as plugin:
        for info in plugin.infolist():
            if info.is_dir():
                continue
            name = _safe_name(info.filename)
            if name.parts[:2] != ("plugins", "FMActionLink"):
                continue
            destination = PurePosixPath("BepInEx") / name
            if destination in files:
                raise ValueError(f"Duplicate plugin path: {destination}")
            files[destination] = plugin.read(info)
    config = PurePosixPath("BepInEx/plugins/FMActionLink/FMActionLink.json")
    if config not in files:
        raise ValueError("ActionLink archive has no expected configuration file")
    files[config] = (json.dumps(BUTTON_CONFIG, indent=2) + "\n").encode("utf-8")
    if PurePosixPath("winhttp.dll") not in files:
        raise ValueError("BepInEx loader archive is missing winhttp.dll")
    return files


def install(game: Path, files: dict[PurePosixPath, bytes]) -> int:
    if not (game / "fm.exe").is_file():
        raise ValueError(f"FM26 executable not found in {game}")
    collisions = [str(game / name) for name in files if (game / name).exists()]
    if collisions:
        raise FileExistsError(f"Refusing to overwrite existing files, starting with {collisions[0]}")
    written: list[Path] = []
    try:
        for name, content in files.items():
            destination = game / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            with destination.open("xb") as output:
                shutil.copyfileobj(io.BytesIO(content), output)
            written.append(destination)
    except Exception:
        for destination in reversed(written):
            destination.unlink(missing_ok=True)
        raise
    return len(written)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--loader-zip", type=Path, required=True)
    parser.add_argument("--actionlink-zip", type=Path, required=True)
    parser.add_argument("--game", type=Path, required=True,
                        help="Path to the Football Manager 26 installation")
    parser.add_argument("--apply", action="store_true", help="Actually write files; otherwise only validate and report")
    args = parser.parse_args()
    files = build_plan(args.loader_zip, args.actionlink_zip)
    collisions = [str(args.game / name) for name in files if (args.game / name).exists()]
    print(f"Prepared {len(files)} files for {args.game}")
    if collisions:
        print(f"Existing file collision: {collisions[0]}", file=sys.stderr)
        raise SystemExit(1)
    if args.apply:
        print(f"Installed {install(args.game, files)} files")
    else:
        print("Dry run only. Pass --apply to install.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, FileExistsError, OSError, zipfile.BadZipFile) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        raise SystemExit(1)
