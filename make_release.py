#!/usr/bin/env python3
"""Create the RegenerateFM release ZIP.

Layout of dist/RegenerateFM-Windows-Linux.zip, in regeneratefm-<version>/:
  FM26/        copy its contents into the "Football Manager 26" folder: BepInEx
               (downloaded once and checked against its pinned SHA-256) and the
               RegenerateFM plugin, so FM26 needs no installer, Python or .NET SDK
  FM24/        regeneratefm-fm24.exe (fm24/build_exe.py), which sets FM24 up itself
  install-linux.sh  sets up FM26, the FM26 Demo and FM24 on Linux (install_linux.py)
  source/      the public source, docs and tests

FM26 on macOS is in RegenerateFM-macOS.dmg instead (hotreload/macos/make_installer.py,
which uses macos_files below).
"""

from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import subprocess
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent
PUBLIC_FILES = (
    ".env.example",
    "LICENSE",
    "NOTICE.md",
    "README.md",
    "RELEASE_CHECKLIST.md",
    "regeneratefm.py",
    "appearance.py",
    "keystore.py",
    "credits.py",
    "steam_launch.py",
    "install_service.py",
    "install_linux.py",
    "install-linux.sh",
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
    "tests/test_linux_installer.py",
    "tests/test_make_release.py",
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
    "licenses/fetch_licenses.py",
    "make_source_archive.py",
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


def licence_files() -> tuple[str, ...]:
    """RegenerateFM's licence and the bundled software's (licenses/), which
    the release, the macOS installer and the packaged companions all carry."""
    texts = sorted(path.relative_to(ROOT).as_posix() for path in (ROOT / "licenses").rglob("*")
                   if path.is_file() and path.suffix in (".txt", ".md"))
    return ("LICENSE", "NOTICE.md", *texts)


def copy_licences(target: Path) -> None:
    for name in licence_files():
        (target / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target / name)


PUBLIC_FILES += tuple(name for name in licence_files() if name not in PUBLIC_FILES)
# Copied to the release's top level, beside the FM26 and FM24 folders.
TOP_LEVEL = ("README.md", *licence_files())


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


def has_crlf(data: bytes) -> bool:
    """Whether text (not a binary file) has Windows line endings."""
    return b"\r\n" in data and b"\0" not in data[:8192]


def crlf_files(root: Path, names: tuple[str, ...], companion: Path, packaged: list[Path]) -> list[str]:
    """The release's own text files with Windows line endings, as in a Windows
    checkout made before .gitattributes or a file an editor saved that way;
    install-linux.sh then fails on Linux. The companion's own text files
    (outside _internal) are copies of the checkout's."""
    found = [name for name in names if has_crlf((root / name).read_bytes())]
    found += [path.relative_to(root).as_posix() for path in packaged
              if "_internal" not in path.relative_to(companion).parts and has_crlf(path.read_bytes())]
    return found


def git_modes() -> dict[str, int]:
    """Each tracked file's mode as git records it. A Windows checkout has no
    executable bits (every file reads as 0o666), so scripts take theirs from git."""
    try:
        listing = subprocess.run(["git", "ls-files", "--stage"], cwd=ROOT, capture_output=True,
                                 text=True, encoding="utf-8", check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        raise SystemExit("make_release.py needs git, run in a checkout, to know which files are executable.")
    modes = {}
    for line in listing.splitlines():
        info, _, name = line.partition("\t")
        modes[name] = 0o755 if info.startswith("100755") else 0o644
    return modes


def file_mode(path: Path, modes: dict[str, int] | None = None) -> int:
    """A file's mode for the release: git's for a tracked file, else whether
    the file is executable (Windows reports that for .exe files)."""
    name = path.relative_to(ROOT).as_posix() if path.is_relative_to(ROOT) else None
    if modes is not None and name in modes:
        return modes[name]
    return 0o755 if path.stat().st_mode & 0o111 else 0o644


def plugin_version() -> str:
    source = (ROOT / "hotreload/RegenerateFMHotReload.cs").read_text(encoding="utf-8")
    match = re.search(r'BepInPlugin\("regeneratefm\.hotreload", "RegenerateFM", "([^"]+)"\)', source)
    if not match:
        raise SystemExit("Could not read the plugin version.")
    return match.group(1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bepinex-zip", type=Path, help="a local copy of the pinned BepInEx be.760 ZIP")
    args = parser.parse_args()
    version = plugin_version()
    # The same name in every release, so GitHub's releases/latest/download link
    # (the front page's Download for Windows & Linux) always finds it.
    target = ROOT / "dist" / "RegenerateFM-Windows-Linux.zip"
    missing = [name for name in PUBLIC_FILES + (PLUGIN_DLL,) if not (ROOT / name).is_file()]
    if missing:
        raise SystemExit("Release files are missing: " + ", ".join(missing))
    # The packaged FM24 companion (fm24/build_exe.py), when it has been built.
    companion = ROOT / "dist" / "regeneratefm-fm24"
    # Its log, from a test run, stays out.
    packaged = sorted(path for path in companion.rglob("*") if path.is_file() and path.suffix != ".log") \
        if companion.is_dir() else []
    if packaged and not (companion / "regeneratefm-fm24.exe").is_file():
        raise SystemExit("dist/regeneratefm-fm24 has no regeneratefm-fm24.exe; rebuild it with fm24/build_exe.py.")
    if any(path.name in (".env", ".fm24-auth") for path in packaged):
        raise SystemExit("dist/regeneratefm-fm24 holds a private .env or .fm24-auth; remove it first.")
    crlf = crlf_files(ROOT, PUBLIC_FILES, companion, packaged)
    if crlf:
        raise SystemExit(f"{len(crlf)} files have Windows line endings, starting with {', '.join(crlf[:3])}. "
                         "Check the files out again so .gitattributes applies (with nothing uncommitted: "
                         "git rm -r -q --cached . && git reset -q --hard), then rebuild the FM24 companion.")
    modes = git_modes()
    loader = bepinex_files(bepinex_zip(BEPINEX, args.bepinex_zip), "winhttp.dll")
    top = f"regeneratefm-{version}"
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        # Every file goes through write(): a fixed date, and a mode from git
        # rather than from a Windows checkout.
        for name in TOP_LEVEL:
            write(archive, f"{top}/{name}", (ROOT / name).read_bytes(), file_mode(ROOT / name, modes))
        write(archive, f"{top}/install-linux.sh", (ROOT / "install-linux.sh").read_bytes(), 0o755)
        for name, (data, mode) in loader.items():
            write(archive, f"{top}/FM26/{name}", data, mode)
        write(archive, f"{top}/FM26/BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll",
              (ROOT / PLUGIN_DLL).read_bytes(), 0o644)
        for path in packaged:
            write(archive, f"{top}/FM24/{path.relative_to(companion).as_posix()}", path.read_bytes(), file_mode(path))
        if packaged:
            write(archive, f"{top}/FM24/install-linux.sh", (ROOT / "fm24/install-linux.sh").read_bytes(), 0o755)
        for name in PUBLIC_FILES:
            write(archive, f"{top}/source/{name}", (ROOT / name).read_bytes(), file_mode(ROOT / name, modes))
    note = f"the {len(packaged)}-file FM24 companion" if packaged else "no FM24 companion (not built)"
    print(f"Created {target}: FM26 folder ({len(loader)} BepInEx files and the plugin), {note}, "
          f"and {len(PUBLIC_FILES)} source files")


if __name__ == "__main__":
    main()
