#!/usr/bin/env python3
"""Fetch the licence texts of the software bundled in the release into licenses/third-party.

    python3 licenses/fetch_licenses.py

Each text comes from the component's own repository, at the commit or tag the
release bundles where it is pinned. THIRD_PARTY_NOTICES.md says which file
covers which component.
"""

from __future__ import annotations

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
    # The desktop programs' packages (src/RegenerateFM.Desktop). StbImageSharp
    # is a port of stb_image and says it is under stb's own licence (public
    # domain or MIT); ZstdSharp is a port of zstd.
    ("ZstdSharp", "oleg-st/ZstdSharp", "2cd0c019693bc786a5fe5c3be94e107b24e7267e", "LICENSE"),
    ("zstd", "facebook/zstd", "dev", "LICENSE"),
    ("StbImageSharp", "nothings/stb", "master", "LICENSE"),
)


def fetch(repository: str, ref: str, path: str) -> bytes:
    url = f"https://raw.githubusercontent.com/{repository}/{ref}/{path}"
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def main() -> None:
    OUTPUT.mkdir(exist_ok=True)
    for name, repository, ref, path in SOURCES:
        (OUTPUT / f"{name}.txt").write_bytes(fetch(repository, ref, path))
        print(f"{name}: {repository}@{ref}/{path}")


if __name__ == "__main__":
    main()
