#!/usr/bin/env python3
"""Create an FM Face archive from explicitly selected public files."""

from __future__ import annotations

import re
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PUBLIC_FILES = (
    ".env.example",
    "LICENSE",
    "README.md",
    "RELEASE_CHECKLIST.md",
    "fmface.py",
    "appearance.py",
    "install_fm26.py",
    "install_service.py",
    "make_release.py",
    "hotreload/FMFaceHotReload.cs",
    "hotreload/FaceGenerator.cs",
    "hotreload/FMFaceHotReload.csproj",
    "hotreload/README.md",
    "fm24/README.md",
    "fm24/requirements.txt",
    "fm24/build_skin.py",
    "fm24/build_exe.py",
    "fm24/requirements-exe.txt",
    "fm24/companion.py",
    "fm24/companion_client.py",
    "fm24/existing_portrait.py",
    "fm24/game_archive.py",
    "fm24/graphics.py",
    "fm24/live_memory.py",
    "fm24/memory_probe.py",
    "fm24/native_dialog.py",
    "fm24/native_listener.py",
    "fm24/native_profile.py",
    "fm24/native_reload.py",
    "fm24/native_screen.py",
    "fm24/native_squad.py",
    "fm24/process_memory.py",
    "fm24/process_memory_linux.py",
    "fm24/process_memory_windows.py",
    "fm24/squad_batch.py",
    "fm24/native_menu/README.md",
    "fm24/native_menu/build.py",
    "fm24/native_menu/install.py",
    "fm24/native_menu/skin.py",
    "fm24/native_menu/fmface24.c",
    "fm24/native_menu/supported_build.json",
    "fm24/native_menu/requirements.txt",
    "tests/test_appearance.py",
    "tests/test_fm24.py",
    "tests/test_fm24_existing_portrait.py",
    "tests/test_fm24_native_dialog.py",
    "tests/test_fm24_native_menu.py",
    "tests/test_fm24_native_squad.py",
    "tests/test_fmface.py",
    "tests/test_fm26_generator.py",
    "tests/fm26_generator/GeneratorCheck.csproj",
    "tests/fm26_generator/Program.cs",
    "hotreload/bin/Release/net6.0/FMFaceHotReload.dll",
)


def main() -> None:
    source = (ROOT / "hotreload/FMFaceHotReload.cs").read_text()
    match = re.search(r'BepInPlugin\("fmface\.hotreload", "FM Face", "([^"]+)"\)', source)
    if not match:
        raise SystemExit("Could not read the plugin version.")
    version = match.group(1)
    target = ROOT / "dist" / f"fmface-{version}.zip"
    missing = [name for name in PUBLIC_FILES if not (ROOT / name).is_file()]
    if missing:
        raise SystemExit("Release files are missing: " + ", ".join(missing))
    # The packaged FM24 companion (fm24/build_exe.py), when it has been built.
    companion = ROOT / "dist" / "fmface-fm24"
    packaged = sorted(path for path in companion.rglob("*") if path.is_file()) if companion.is_dir() else []
    if packaged and not (companion / "fmface-fm24.exe").is_file():
        raise SystemExit("dist/fmface-fm24 has no fmface-fm24.exe; rebuild it with fm24/build_exe.py.")
    if any(path.name in (".env", ".fm24-auth") for path in packaged):
        raise SystemExit("dist/fmface-fm24 holds a private .env or .fm24-auth; remove it first.")
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in PUBLIC_FILES:
            archive.write(ROOT / name, f"fmface-{version}/{name}")
        for path in packaged:
            archive.write(path, f"fmface-{version}/fmface-fm24/{path.relative_to(companion).as_posix()}")
    note = f" and the {len(packaged)}-file FM24 companion" if packaged else " (FM24 companion not built)"
    print(f"Created {target} with {len(PUBLIC_FILES)} public files{note}")


if __name__ == "__main__":
    main()
