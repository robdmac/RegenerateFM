#!/usr/bin/env python3
"""Create the FM Face release ZIP.

Layout of fmface-<version>.zip, in fmface-<version>/:
  FM26/    copy its contents into the "Football Manager 26" folder: BepInEx
           (downloaded once and checked against its pinned SHA-256) and the
           FM Face plugin, so FM26 needs no installer, Python or .NET SDK
  FM24/    fmface-fm24.exe (fm24/build_exe.py), which sets FM24 up itself
  source/  the public source, docs and tests
"""

from __future__ import annotations

import argparse
import hashlib
import re
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent
PUBLIC_FILES = (
    ".env.example",
    "LICENSE",
    "README.md",
    "RELEASE_CHECKLIST.md",
    "fmface.py",
    "appearance.py",
    "keystore.py",
    "install_service.py",
    "make_release.py",
    "hotreload/FMFaceHotReload.cs",
    "hotreload/FaceGenerator.cs",
    "hotreload/FMFaceHotReload.csproj",
    "hotreload/README.md",
    "fm24/README.md",
    "fm24/requirements.txt",
    "fm24/build_skin.py",
    "fm24/setup_fm24.py",
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
    "tests/test_keystore.py",
    "tests/test_fm24.py",
    "tests/test_fm24_setup.py",
    "tests/test_fm24_existing_portrait.py",
    "tests/test_fm24_native_dialog.py",
    "tests/test_fm24_native_menu.py",
    "tests/test_fm24_native_squad.py",
    "tests/test_fmface.py",
    "tests/test_fm26_generator.py",
    "tests/fm26_generator/GeneratorCheck.csproj",
    "tests/fm26_generator/Program.cs",
    "licenses/THIRD_PARTY_NOTICES.md",
    "licenses/BepInEx-LICENSE.txt",
    "licenses/Il2CppInterop-LICENSE.txt",
)
PLUGIN_DLL = "hotreload/bin/Release/net6.0/FMFaceHotReload.dll"
BEPINEX_URL = ("https://builds.bepinex.dev/projects/bepinex_be/760/"
               "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760%2Ba1afbfb.zip")
BEPINEX_SHA256 = "9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea"
# Copied to the release's top level, beside the FM26 and FM24 folders.
TOP_LEVEL = ("README.md", "LICENSE", "licenses/THIRD_PARTY_NOTICES.md",
             "licenses/BepInEx-LICENSE.txt", "licenses/Il2CppInterop-LICENSE.txt")


def bepinex_zip(path: Path | None) -> Path:
    """The pinned BepInEx build: a given file, or a download cached in dist/."""
    target = path or ROOT / "dist" / "cache" / "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760+a1afbfb.zip"
    if not target.is_file():
        if path:
            raise SystemExit(f"{path} does not exist.")
        target.parent.mkdir(parents=True, exist_ok=True)
        print(f"Downloading BepInEx from {BEPINEX_URL}")
        with urllib.request.urlopen(BEPINEX_URL, timeout=120) as response:
            data = response.read()
        temporary = target.with_suffix(".part")
        temporary.write_bytes(data)
        temporary.replace(target)
    if hashlib.sha256(target.read_bytes()).hexdigest() != BEPINEX_SHA256:
        raise SystemExit(f"{target} is not the pinned BepInEx be.760 build (SHA-256 mismatch).")
    return target


def bepinex_files(archive: Path) -> dict[str, bytes]:
    """BepInEx's files by path in the game folder (its changelog left out)."""
    files = {}
    with zipfile.ZipFile(archive) as source:
        for info in source.infolist():
            name = PurePosixPath(info.filename.replace("\\", "/"))
            if info.is_dir() or name.is_absolute() or ".." in name.parts or str(name) == "changelog.txt":
                continue
            files[str(name)] = source.read(info)
    if "winhttp.dll" not in files or "BepInEx/core/BepInEx.Unity.IL2CPP.dll" not in files:
        raise SystemExit("The BepInEx archive is missing winhttp.dll or BepInEx.Unity.IL2CPP.dll.")
    return files


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bepinex-zip", type=Path, help="a local copy of the pinned BepInEx be.760 ZIP")
    args = parser.parse_args()
    source = (ROOT / "hotreload/FMFaceHotReload.cs").read_text()
    match = re.search(r'BepInPlugin\("fmface\.hotreload", "FM Face", "([^"]+)"\)', source)
    if not match:
        raise SystemExit("Could not read the plugin version.")
    version = match.group(1)
    target = ROOT / "dist" / f"fmface-{version}.zip"
    missing = [name for name in PUBLIC_FILES + (PLUGIN_DLL,) if not (ROOT / name).is_file()]
    if missing:
        raise SystemExit("Release files are missing: " + ", ".join(missing))
    # The packaged FM24 companion (fm24/build_exe.py), when it has been built.
    companion = ROOT / "dist" / "fmface-fm24"
    packaged = sorted(path for path in companion.rglob("*") if path.is_file()) if companion.is_dir() else []
    if packaged and not (companion / "fmface-fm24.exe").is_file():
        raise SystemExit("dist/fmface-fm24 has no fmface-fm24.exe; rebuild it with fm24/build_exe.py.")
    if any(path.name in (".env", ".fm24-auth") for path in packaged):
        raise SystemExit("dist/fmface-fm24 holds a private .env or .fm24-auth; remove it first.")
    loader = bepinex_files(bepinex_zip(args.bepinex_zip))
    top = f"fmface-{version}"
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in TOP_LEVEL:
            archive.write(ROOT / name, f"{top}/{Path(name).name}")
        for name, data in loader.items():
            archive.writestr(f"{top}/FM26/{name}", data)
        archive.write(ROOT / PLUGIN_DLL, f"{top}/FM26/BepInEx/plugins/FMFaceHotReload/FMFaceHotReload.dll")
        for path in packaged:
            archive.write(path, f"{top}/FM24/{path.relative_to(companion).as_posix()}")
        for name in PUBLIC_FILES:
            archive.write(ROOT / name, f"{top}/source/{name}")
    note = f"the {len(packaged)}-file FM24 companion" if packaged else "no FM24 companion (not built)"
    print(f"Created {target}: FM26 folder ({len(loader)} BepInEx files and the plugin), {note}, "
          f"and {len(PUBLIC_FILES)} source files")


if __name__ == "__main__":
    main()
