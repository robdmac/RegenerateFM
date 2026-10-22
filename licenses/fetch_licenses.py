#!/usr/bin/env python3
"""Fetch the licence texts of the software bundled in the release into licenses/third-party.

    .venv-fm24/bin/python licenses/fetch_licenses.py

Each text comes from the component's own repository, at the commit or tag the
release bundles where it is pinned. Pillow's comes from the installed wheel,
since only the wheel's licence lists the libraries it bundles; run this with
the Python the companion is built with (fm24/requirements-exe.txt).
THIRD_PARTY_NOTICES.md says which file covers which component.
"""

from __future__ import annotations

import importlib.metadata
import urllib.request
from pathlib import Path

OUTPUT = Path(__file__).resolve().parent / "third-party"

# File name, repository, commit or tag, path of the licence in it.
SOURCES = (
    ("UnityDoorstop", "NeighTools/UnityDoorstop", "v4.5.0", "LICENSE"),
    ("HarmonyX", "BepInEx/HarmonyX", "master", "LICENSE"),
    ("MonoMod", "MonoMod/MonoMod", "reorganize", "LICENSE"),
    ("Mono.Cecil", "jbevain/cecil", "master", "LICENSE.txt"),
    ("Cpp2IL", "SamboyCoding/Cpp2IL", "development", "LICENSE"),
    ("Disarm", "SamboyCoding/Disarm", "master", "LICENSE"),
    ("AsmResolver", "Washi1337/AsmResolver", "master", "LICENSE.md"),
    ("AssetRipper.Primitives", "AssetRipper/AssetRipper.Primitives", "master", "License.md"),
    ("AssetRipper.CIL", "AssetRipper/AssetRipper.CIL", "main", "LICENSE"),
    ("Iced", "icedland/iced", "master", "LICENSE.txt"),
    ("Capstone.NET", "9ee1/Capstone.NET", "master", "LICENSE"),
    ("Capstone", "capstone-engine/capstone", "4.0.2", "LICENSE.TXT"),
    ("Capstone-LLVM", "capstone-engine/capstone", "4.0.2", "LICENSE_LLVM.TXT"),
    ("Dobby", "jmpews/Dobby", "master", "LICENSE"),
    ("SemanticVersioning", "adamreeve/semver.net", "master", "LICENSE"),
    ("dotnet-runtime", "dotnet/runtime", "main", "LICENSE.TXT"),
    ("dotnet-runtime-THIRD-PARTY-NOTICES", "dotnet/runtime", "main", "THIRD-PARTY-NOTICES.TXT"),
    ("funchook", "kubo/funchook", "88388db3c69e16c1560fee65c6857d75f5ce6fd5", "LICENSE"),
    ("diStorm3", "gdabah/distorm", "ab59d6e193948cfa5d1482fb6c7e64870e9e93b9", "COPYING"),
    ("Python", "python/cpython", "3.11", "LICENSE"),
    ("python-zstandard", "indygreg/python-zstandard", "main", "LICENSE"),
    ("zstd", "facebook/zstd", "dev", "LICENSE"),
    ("PyInstaller", "pyinstaller/pyinstaller", "v6.10.0", "COPYING.txt"),
)


def fetch(repository: str, ref: str, path: str) -> bytes:
    url = f"https://raw.githubusercontent.com/{repository}/{ref}/{path}"
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def pillow_licence() -> bytes:
    for file in importlib.metadata.distribution("pillow").files or ():
        if file.name == "LICENSE":
            return Path(file.locate()).read_bytes()
    raise SystemExit("Pillow's wheel licence wasn't found; run this with the companion's build Python.")


def main() -> None:
    OUTPUT.mkdir(exist_ok=True)
    for name, repository, ref, path in SOURCES:
        (OUTPUT / f"{name}.txt").write_bytes(fetch(repository, ref, path))
        print(f"{name}: {repository}@{ref}/{path}")
    version = importlib.metadata.version("pillow")
    (OUTPUT / "Pillow.txt").write_bytes(pillow_licence())
    print(f"Pillow: the installed {version} wheel")


if __name__ == "__main__":
    main()
