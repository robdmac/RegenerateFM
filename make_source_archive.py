#!/usr/bin/env python3
"""Make dist/RegenerateFM-third-party-source.zip, the source of the GPL and LGPL software the release bundles.

    python3 make_source_archive.py

The release ZIP and the macOS installer carry BepInEx, UnityDoorstop and
Il2CppInterop (LGPL) and, on the Mac, a patched Il2CppInterop and funchook
(GPL-2.0-or-later). Their licences ask for their source to be offered with
them, so this archive goes on every GitHub release beside the ZIP and the DMG.
It holds each project's source at the commit the release bundles, as GitHub
serves it, with the Mac patches and the script that applies them and builds
the patched parts (hotreload/macos/build.py). The name stays the same in every
release, like the DMG's.
"""

from __future__ import annotations

import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "hotreload" / "macos"))
import build as macos_build  # noqa: E402

ARCHIVE = ROOT / "dist" / "RegenerateFM-third-party-source.zip"
CACHE = ROOT / "dist" / "cache" / "source"


def github(url: str) -> str:
    return url.removeprefix("https://github.com/").removesuffix(".git")


# Name in the archive, repository, commit, what it is.
SOURCES = (
    ("BepInEx-be.760", "BepInEx/BepInEx", "a1afbfb169610ffbc1813d83e946c9e2cc0d2d63",
     "BepInEx 6.0.0-be.760, in the release ZIP's FM26 folder (Windows)"),
    ("BepInEx-be.789", "BepInEx/BepInEx", "6cea286d204eb3754d3b8363a5a1fd6640164a49",
     "BepInEx 6.0.0-be.789, in the macOS installer's FM26-macOS folder"),
    ("UnityDoorstop-4.5.0", "NeighTools/UnityDoorstop", "33dab9a6733862eb81869ff08431d9478b28784b",
     "UnityDoorstop 4.5.0 (winhttp.dll and libdoorstop.dylib), in both BepInEx builds"),
    ("Il2CppInterop-1.5.1-ci.829", "BepInEx/Il2CppInterop", "6d9007c18cc8440830379c5e1d5714085e7ec577",
     "Il2CppInterop in BepInEx be.760 (Windows), unmodified"),
    ("Il2CppInterop-1.5.3", github(macos_build.IL2CPPINTEROP[0]), macos_build.IL2CPPINTEROP[1],
     "Il2CppInterop 1.5.3 for macOS, before patches/il2cppinterop-macos.patch"),
    ("funchook-1.1.3", github(macos_build.FUNCHOOK[0]), macos_build.FUNCHOOK[1],
     "funchook 1.1.3 for macOS, before patches/funchook-rosetta.patch"),
    ("diStorm3", "gdabah/distorm", macos_build.DISTORM_COMMIT, "diStorm3, built into libfunchook.dylib"),
)
# The changes made to the macOS parts, and how they are applied and built.
OWN_FILES = ("hotreload/macos/build.py", "hotreload/macos/patches/il2cppinterop-macos.patch",
             "hotreload/macos/patches/funchook-rosetta.patch", "LICENSE",
             "licenses/BepInEx-LICENSE.txt", "licenses/Il2CppInterop-LICENSE.txt",
             "licenses/third-party/UnityDoorstop.txt", "licenses/third-party/funchook.txt",
             "licenses/third-party/diStorm3.txt")


def download(repository: str, commit: str) -> bytes:
    cached = CACHE / f"{repository.replace('/', '-')}-{commit}.zip"
    if not cached.is_file():
        CACHE.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(f"https://codeload.github.com/{repository}/zip/{commit}", timeout=300) as response:
            cached.write_bytes(response.read())
    data = cached.read_bytes()
    if not data.startswith(b"PK"):
        raise SystemExit(f"{repository} at {commit} didn't download as a ZIP.")
    return data


def readme() -> str:
    lines = ["Source of the GPL and LGPL software in the RegenerateFM release.", "",
             "Each ZIP in source/ is the project's own source at the commit the release bundles,",
             "as https://codeload.github.com/<repository>/zip/<commit> serves it:", ""]
    for name, repository, commit, what in SOURCES:
        lines.append(f"- source/{name}.zip: https://github.com/{repository} at {commit}")
        lines.append(f"  {what}")
    lines += ["", "The macOS installer's Il2CppInterop.Runtime.dll, Il2CppInterop.Common.dll and",
              "libfunchook.dylib are those sources with the patches in hotreload/macos/patches,",
              "built by hotreload/macos/build.py (on a Mac with git, CMake, Xcode's command-line",
              "tools and the .NET 8 SDK). Everything else is unmodified. The licences are in",
              "LICENSE (the GNU GPL version 3, which the LGPL version 3 builds on) and licenses/.", ""]
    return "\n".join(lines)


def main() -> None:
    ARCHIVE.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(ARCHIVE, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("README.txt", readme())
        for name in OWN_FILES:
            archive.write(ROOT / name, name)
        for name, repository, commit, _ in SOURCES:
            # Already compressed; stored as GitHub made it.
            archive.writestr(f"source/{name}.zip", download(repository, commit), zipfile.ZIP_STORED)
            print(f"{name}: {repository}@{commit[:7]}")
    print(f"Created {ARCHIVE} ({ARCHIVE.stat().st_size // 1_000_000} MB)")


if __name__ == "__main__":
    main()
