#!/usr/bin/env python3
"""Package the FM24 companion as dist/fmface-fm24/fmface-fm24.exe.

Run with a Windows Python 3.11 that has the packages in
fm24/requirements-exe.txt; on Linux, a Windows Python under Wine works:

    wine python.exe -m pip install -r fm24/requirements-exe.txt
    wine python.exe fm24/build_exe.py

The result is a folder rather than a single file: one-file packages unpack
themselves to a temporary folder on every start, which is slower and is more
often flagged by antivirus software. The exe includes the FM24 DLL built by
fm24/native_menu/build.py: on its first start it finds FM24 through Steam,
builds the FM Face skin from FM24's own panels, installs the DLL and asks for
the OpenAI key (or reads OPENAI_API_KEY from a .env beside it).
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NAME = "fmface-fm24"


def main() -> None:
    if sys.platform != "win32":
        sys.exit("Run this with Windows Python (under Wine on Linux); it builds a Windows executable.")
    import PyInstaller.__main__

    dll = ROOT / "fm24" / "native_menu" / "build" / "winmm.dll"
    if not dll.is_file():
        sys.exit(f"Build the FM24 DLL first (fm24/native_menu/build.py); {dll} is missing.")

    dist = ROOT / "dist"
    work = ROOT / "dist" / "pyinstaller-build"
    PyInstaller.__main__.run([
        str(ROOT / "fm24" / "companion.py"),
        "--name", NAME,
        "--onedir", "--console", "--noconfirm", "--clean",
        "--paths", str(ROOT), "--paths", str(ROOT / "fm24"), "--paths", str(ROOT / "fm24" / "native_menu"),
        # The setup step installs this DLL into FM24's folder.
        "--add-data", f"{dll};fm24-dll", "--hidden-import", "setup_fm24",
        "--add-data", f"{ROOT / 'fm24' / 'native_menu' / 'supported_build.json'};.",
        "--distpath", str(dist), "--workpath", str(work), "--specpath", str(work),
        # Linux-only memory reader and modules the companion never uses.
        "--exclude-module", "process_memory_linux",
        "--exclude-module", "tkinter", "--exclude-module", "unittest", "--exclude-module", "pydoc",
    ])
    output = dist / NAME
    shutil.copy2(ROOT / ".env.example", output / ".env.example")
    shutil.rmtree(work, ignore_errors=True)
    print(f"Built {output / (NAME + '.exe')}")


if __name__ == "__main__":
    main()
