#!/usr/bin/env python3
"""Package the FM24 companion for macOS as dist/regeneratefm-fm24-macos/regeneratefm-fm24.

Run on an Apple Silicon Mac, with a Python that has the packages in
fm24/requirements-exe.txt, after fm24/native_menu/build_macos.py:

    python3 -m venv .venv-mac-build
    .venv-mac-build/bin/pip install -r fm24/requirements-exe.txt
    .venv-mac-build/bin/python fm24/build_companion_macos.py

Like the Windows build (build_exe.py) it is a folder, not one file, and it
carries the Mac library (libregeneratefm24.dylib) and run_regeneratefm24.sh:
`regeneratefm-fm24 setup --game-dir ...` installs them into FM24's folder and
records the companion in regeneratefm24.ini, so run_regeneratefm24.sh starts it
with FM24 without Python. The macOS installer (hotreload/macos/make_installer.py)
ships it and signs it.
"""

from __future__ import annotations

import platform
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import make_release  # noqa: E402

NATIVE = ROOT / "fm24" / "native_menu"
NAME = "regeneratefm-fm24"
OUTPUT = ROOT / "dist" / f"{NAME}-macos"


def main() -> None:
    if sys.platform != "darwin" or platform.machine() != "arm64":
        sys.exit("Run this with an arm64 Python on an Apple Silicon Mac; FM24's Mac library is arm64 only.")
    import PyInstaller.__main__

    library = NATIVE / "build-macos" / "libregeneratefm24.dylib"
    if not library.is_file():
        sys.exit(f"Build the Mac library first (fm24/native_menu/build_macos.py); {library} is missing.")
    work = ROOT / "dist" / "pyinstaller-build-macos"
    PyInstaller.__main__.run([
        str(ROOT / "fm24" / "companion.py"),
        "--name", NAME,
        "--onedir", "--console", "--noconfirm", "--clean", "--target-arch", "arm64",
        "--paths", str(ROOT), "--paths", str(ROOT / "fm24"), "--paths", str(NATIVE),
        # The setup step installs these into FM24's folder.
        "--add-data", f"{library}:fm24-dll", "--hidden-import", "setup_fm24",
        "--add-data", f"{NATIVE / 'run_regeneratefm24.sh'}:.",
        "--add-data", f"{NATIVE / 'supported_build_macos.json'}:.",
        "--add-data", f"{NATIVE / 'supported_build.json'}:.",
        "--distpath", str(OUTPUT), "--workpath", str(work), "--specpath", str(work),
        # Other platforms' memory readers and modules the companion never uses.
        "--exclude-module", "process_memory_linux", "--exclude-module", "process_memory_windows",
        "--exclude-module", "tkinter", "--exclude-module", "unittest", "--exclude-module", "pydoc",
    ])
    shutil.copy2(ROOT / ".env.example", OUTPUT / NAME / ".env.example")
    # PyInstaller leaves the bundled packages' licences out; these cover them.
    make_release.copy_licences(OUTPUT / NAME)
    shutil.rmtree(work, ignore_errors=True)
    print(f"Built {OUTPUT / NAME / NAME}")


if __name__ == "__main__":
    main()
