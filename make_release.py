#!/usr/bin/env python3
"""Create the RegenerateFM release ZIP.

Layout of regeneratefm-<version>.zip, in regeneratefm-<version>/:
  FM26/        copy its contents into the "Football Manager 26" folder: BepInEx
               (downloaded once and checked against its pinned SHA-256) and the
               RegenerateFM plugin, so FM26 needs no installer, Python or .NET SDK
  FM26-macOS/  the same for FM26 on macOS: BepInEx for macOS with the pieces
               hotreload/macos/build.py makes, and run_regeneratefm.sh; only when
               they have been built
  FM24/        regeneratefm-fm24.exe (fm24/build_exe.py), which sets FM24 up itself
  source/      the public source, docs and tests
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
    "regeneratefm.py",
    "appearance.py",
    "keystore.py",
    "credits.py",
    "steam_launch.py",
    "install_service.py",
    "make_release.py",
    "hotreload/RegenerateFMHotReload.cs",
    "hotreload/FaceGenerator.cs",
    "hotreload/RegenerateFMHotReload.csproj",
    "hotreload/README.md",
    "hotreload/macos/README.md",
    "hotreload/macos/BepInEx.cfg",
    "hotreload/macos/build.py",
    "hotreload/macos/make_installer.py",
    "hotreload/macos/installer/RegenerateFMInstaller.swift",
    "hotreload/macos/installer/AppIcon.png",
    "hotreload/macos/run_regeneratefm.sh",
    "hotreload/macos/patches/funchook-rosetta.patch",
    "hotreload/macos/patches/il2cppinterop-macos.patch",
    "global.json",
    "fm24/README.md",
    "fm24/requirements.txt",
    "fm24/build_skin.py",
    "fm24/setup_fm24.py",
    "fm24/install-linux.sh",
    "fm24/build_exe.py",
    "fm24/build_companion_macos.py",
    "fm24/requirements-exe.txt",
    "fm24/companion.py",
    "fm24/companion_client.py",
    "fm24/existing_portrait.py",
    "fm24/game_archive.py",
    "fm24/graphics.py",
    "fm24/key_dialog.py",
    "fm24/layout.py",
    "fm24/live_memory.py",
    "fm24/memory_probe.py",
    "fm24/native_dialog.py",
    "fm24/native_listener.py",
    "fm24/native_profile.py",
    "fm24/native_reload.py",
    "fm24/native_screen.py",
    "fm24/native_squad.py",
    "fm24/person_handles.py",
    "fm24/probe_object_browser.py",
    "fm24/process_memory.py",
    "fm24/process_memory_linux.py",
    "fm24/process_memory_macos.py",
    "fm24/process_memory_windows.py",
    "fm24/squad_batch.py",
    "fm24/native_menu/README.md",
    "fm24/native_menu/build.py",
    "fm24/native_menu/install.py",
    "fm24/native_menu/skin.py",
    "fm24/native_menu/regeneratefm24.c",
    "fm24/native_menu/supported_build.json",
    "fm24/native_menu/build_macos.py",
    "fm24/native_menu/regeneratefm24_macos.c",
    "fm24/native_menu/key_prompt_macos.m",
    "fm24/native_menu/run_regeneratefm24.sh",
    "fm24/native_menu/supported_build_macos.json",
    "fm24/native_menu/requirements.txt",
    "tests/test_appearance.py",
    "tests/test_keystore.py",
    "tests/test_credits.py",
    "tests/test_macos_installer.py",
    "tests/test_steam_launch.py",
    "tests/test_fm24.py",
    "tests/test_fm24_setup.py",
    "tests/test_fm24_existing_portrait.py",
    "tests/test_fm24_native_dialog.py",
    "tests/test_fm24_native_menu.py",
    "tests/test_fm24_native_squad.py",
    "tests/test_regeneratefm.py",
    "tests/test_fm26_generator.py",
    "tests/fm26_generator/GeneratorCheck.csproj",
    "tests/fm26_generator/Program.cs",
    "licenses/THIRD_PARTY_NOTICES.md",
    "licenses/BepInEx-LICENSE.txt",
    "licenses/Il2CppInterop-LICENSE.txt",
)
PLUGIN_DLL = "hotreload/bin/Release/net6.0/RegenerateFMHotReload.dll"
BEPINEX = ("https://builds.bepinex.dev/projects/bepinex_be/760/"
           "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760%2Ba1afbfb.zip",
           "9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea")
# The first macOS build whose Il2CppInterop (1.5.3) the macOS patch applies to.
BEPINEX_MACOS = ("https://builds.bepinex.dev/projects/bepinex_be/789/"
                 "BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.789%2B6cea286.zip",
                 "cc2341544d271f8bcea4e7415acad10766e968d236aaee30cdd79780ec7db00b")
MACOS_BUILD = ROOT / "hotreload" / "macos" / "build"
MACOS_NATIVE = {"BepInEx/core/libfunchook.dylib": "libfunchook.dylib",
                "BepInEx/core/Il2CppInterop.Runtime.dll": "Il2CppInterop.Runtime.dll",
                "BepInEx/core/Il2CppInterop.Common.dll": "Il2CppInterop.Common.dll"}
# Copied to the release's top level, beside the FM26 and FM24 folders.
TOP_LEVEL = ("README.md", "LICENSE", "licenses/THIRD_PARTY_NOTICES.md",
             "licenses/BepInEx-LICENSE.txt", "licenses/Il2CppInterop-LICENSE.txt")


def bepinex_zip(pinned: tuple[str, str], path: Path | None) -> Path:
    """A pinned BepInEx build: a given file, or a download cached in dist/."""
    url, sha256 = pinned
    target = path or ROOT / "dist" / "cache" / url.rsplit("/", 1)[1].replace("%2B", "+")
    if not target.is_file():
        if path:
            raise SystemExit(f"{path} does not exist.")
        target.parent.mkdir(parents=True, exist_ok=True)
        print(f"Downloading BepInEx from {url}")
        # builds.bepinex.dev refuses Python's default User-Agent (HTTP 403).
        request = urllib.request.Request(url, headers={"User-Agent": "regeneratefm-make-release"})
        with urllib.request.urlopen(request, timeout=120) as response:
            data = response.read()
        temporary = target.with_suffix(".part")
        temporary.write_bytes(data)
        temporary.replace(target)
    if hashlib.sha256(target.read_bytes()).hexdigest() != sha256:
        raise SystemExit(f"{target} is not the pinned BepInEx build {target.name} (SHA-256 mismatch).")
    return target


def bepinex_files(archive: Path, loader: str, leave_out: tuple[str, ...] = ()) -> dict[str, tuple[bytes, int]]:
    """BepInEx's files by path in the game folder, with their Unix modes (its changelog left out)."""
    files = {}
    with zipfile.ZipFile(archive) as source:
        for info in source.infolist():
            name = PurePosixPath(info.filename.replace("\\", "/"))
            if (info.is_dir() or name.is_absolute() or ".." in name.parts
                    or str(name) in ("changelog.txt",) + leave_out):
                continue
            files[str(name)] = (source.read(info), info.external_attr >> 16)
    if loader not in files or "BepInEx/core/BepInEx.Unity.IL2CPP.dll" not in files:
        raise SystemExit(f"The BepInEx archive is missing {loader} or BepInEx.Unity.IL2CPP.dll.")
    return files


def macos_files(archive: Path) -> dict[str, tuple[bytes, int]]:
    """The FM26-macOS folder: BepInEx for macOS, the rebuilt hooking pieces, its config and launcher."""
    files = bepinex_files(archive, "libdoorstop.dylib", leave_out=("run_bepinex.sh",))
    for name in MACOS_NATIVE:
        if name != "BepInEx/core/libfunchook.dylib" and name not in files:
            raise SystemExit(f"The macOS BepInEx archive has no {name} to replace.")
    for name, built in MACOS_NATIVE.items():
        files[name] = ((MACOS_BUILD / built).read_bytes(), 0o644)
    files["BepInEx/config/BepInEx.cfg"] = ((ROOT / "hotreload/macos/BepInEx.cfg").read_bytes(), 0o644)
    files["run_regeneratefm.sh"] = ((ROOT / "hotreload/macos/run_regeneratefm.sh").read_bytes(), 0o755)
    return files


def write(archive: zipfile.ZipFile, name: str, data: bytes, mode: int) -> None:
    info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    # Archive Utility and ditto apply the mode only with the regular-file type bits.
    info.external_attr = (0o100000 | ((mode or 0o644) & 0o7777)) << 16
    archive.writestr(info, data)


def plugin_version() -> str:
    source = (ROOT / "hotreload/RegenerateFMHotReload.cs").read_text()
    match = re.search(r'BepInPlugin\("regeneratefm\.hotreload", "RegenerateFM", "([^"]+)"\)', source)
    if not match:
        raise SystemExit("Could not read the plugin version.")
    return match.group(1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bepinex-zip", type=Path, help="a local copy of the pinned BepInEx be.760 ZIP")
    parser.add_argument("--bepinex-macos-zip", type=Path, help="a local copy of the pinned macOS BepInEx be.789 ZIP")
    args = parser.parse_args()
    version = plugin_version()
    target = ROOT / "dist" / f"regeneratefm-{version}.zip"
    missing = [name for name in PUBLIC_FILES + (PLUGIN_DLL,) if not (ROOT / name).is_file()]
    if missing:
        raise SystemExit("Release files are missing: " + ", ".join(missing))
    # The packaged FM24 companion (fm24/build_exe.py), when it has been built.
    companion = ROOT / "dist" / "regeneratefm-fm24"
    packaged = sorted(path for path in companion.rglob("*") if path.is_file()) if companion.is_dir() else []
    if packaged and not (companion / "regeneratefm-fm24.exe").is_file():
        raise SystemExit("dist/regeneratefm-fm24 has no regeneratefm-fm24.exe; rebuild it with fm24/build_exe.py.")
    if any(path.name in (".env", ".fm24-auth") for path in packaged):
        raise SystemExit("dist/regeneratefm-fm24 holds a private .env or .fm24-auth; remove it first.")
    loader = bepinex_files(bepinex_zip(BEPINEX, args.bepinex_zip), "winhttp.dll")
    # The macOS hooking pieces (hotreload/macos/build.py), when they have been built.
    macos = (macos_files(bepinex_zip(BEPINEX_MACOS, args.bepinex_macos_zip))
             if all((MACOS_BUILD / built).is_file() for built in MACOS_NATIVE.values()) else {})
    top = f"regeneratefm-{version}"
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in TOP_LEVEL:
            archive.write(ROOT / name, f"{top}/{Path(name).name}")
        for name, (data, mode) in loader.items():
            write(archive, f"{top}/FM26/{name}", data, mode)
        archive.write(ROOT / PLUGIN_DLL, f"{top}/FM26/BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll")
        for name, (data, mode) in macos.items():
            write(archive, f"{top}/FM26-macOS/{name}", data, mode)
        if macos:
            archive.write(ROOT / PLUGIN_DLL, f"{top}/FM26-macOS/BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll")
        for path in packaged:
            archive.write(path, f"{top}/FM24/{path.relative_to(companion).as_posix()}")
        if packaged:
            write(archive, f"{top}/FM24/install-linux.sh", (ROOT / "fm24/install-linux.sh").read_bytes(), 0o755)
        for name in PUBLIC_FILES:
            archive.write(ROOT / name, f"{top}/source/{name}")
    note = f"the {len(packaged)}-file FM24 companion" if packaged else "no FM24 companion (not built)"
    mac = (f"FM26-macOS folder ({len(macos)} files and the plugin)" if macos
           else "no FM26-macOS folder (hotreload/macos/build.py not run)")
    print(f"Created {target}: FM26 folder ({len(loader)} BepInEx files and the plugin), {mac}, {note}, "
          f"and {len(PUBLIC_FILES)} source files")


if __name__ == "__main__":
    main()
