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
    "hotreload/FMFaceHotReload.csproj",
    "hotreload/README.md",
    "fm24/README.md",
    "fm24/requirements.txt",
    "fm24/build_skin.py",
    "fm24/companion.py",
    "fm24/capture_profile.py",
    "fm24/capture_staff.py",
    "fm24/read_profile.py",
    "fm24/refresh.py",
    "fm24/graphics.py",
    "fm24/overlay.py",
    "fm24/click_listener.py",
    "fm24/menu_overlay.py",
    "fm24/portrait_menu.py",
    "fm24/squad_batch.py",
    "fm24/squad_progress.py",
    "fm24/memory_probe.py",
    "fm24/process_memory.py",
    "fm24/companion_client.py",
    "fm24/live_memory.py",
    "fm24/native_profile.py",
    "fm24/native_portrait.py",
    "fm24/native_listener.py",
    "fm24/native_menu/README.md",
    "fm24/native_menu/build.py",
    "fm24/native_menu/install.py",
    "fm24/native_menu/skin.py",
    "fm24/native_menu/fmface24.c",
    "fm24/native_menu/supported_build.json",
    "fm24/native_menu/requirements.txt",
    "tests/test_fm24.py",
    "tests/test_fm24_native_menu.py",
    "tests/test_fmface.py",
    "tests/test_appearance.py",
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
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in PUBLIC_FILES:
            archive.write(ROOT / name, f"fmface-{version}/{name}")
    print(f"Created {target} with {len(PUBLIC_FILES)} public files")


if __name__ == "__main__":
    main()
