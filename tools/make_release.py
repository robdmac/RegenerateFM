#!/usr/bin/env python3
"""Create the RegenerateFM release ZIP for Windows and Linux.

    python3 tools/make_release.py [--bepinex-zip FILE] [--no-publish]

Layout of dist/RegenerateFM-Windows-Linux.zip, in regeneratefm-<version>/:
  FM26/        copy its contents into the "Football Manager 26" folder: BepInEx
               (downloaded once and checked against its pinned SHA-256) and the
               RegenerateFM plugin, so FM26 needs no installer or .NET
  FM24/        regeneratefm-fm24.exe, the FM24 companion, which sets FM24 up itself
  linux/       regeneratefm, the Linux installer install-linux.sh runs
  install-linux.sh  sets up FM26, the FM26 Demo and FM24 on Linux
  source/      the public source, docs and tests

It needs the FM26 plugin built first (see src/RegenerateFM.FM26/README.md: it
is built against a game folder), FM24's DLL (native/fm24/build.py) and FM26's
progress window for the Mac (native/fm26progress/build.py --macos, used by
macos/make_installer.py). It
publishes the companion and the Linux installer itself with dotnet, unless
--no-publish. FM26 and FM24 on macOS are in RegenerateFM-macOS.dmg instead
(macos/make_installer.py, which uses macos_files below).
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent.parent
PLUGIN_SOURCE = ROOT / "src" / "RegenerateFM.FM26" / "Plugin.cs"
PLUGIN_DLL = ROOT / "src" / "RegenerateFM.FM26" / "bin" / "Release" / "net6.0" / "RegenerateFM.dll"
PLUGIN_FOLDER = "BepInEx/plugins/RegenerateFM"
DESKTOP = ROOT / "src" / "RegenerateFM.Desktop"
PUBLISHED = ROOT / "dist" / "publish"
# The desktop program as each system's build: (runtime, the file it makes).
COMPANION = ("win-x64", "regeneratefm-fm24.exe")
LINUX_INSTALLER = ("linux-x64", "regeneratefm")
BEPINEX = ("https://builds.bepinex.dev/projects/bepinex_be/760/"
           "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760%2Ba1afbfb.zip",
           "9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea")
# The first macOS build whose Il2CppInterop (1.5.3) the macOS patch applies to.
BEPINEX_MACOS = ("https://builds.bepinex.dev/projects/bepinex_be/789/"
                 "BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.789%2B6cea286.zip",
                 "cc2341544d271f8bcea4e7415acad10766e968d236aaee30cdd79780ec7db00b")
# Doorstop 4.6.0 from a later build: be.789's 4.5.0 can't read FM27's UnityPlayer,
# whose Mach-O uses chained fixups, so BepInEx never starts in FM27.
DOORSTOP_MACOS = ("https://builds.bepinex.dev/projects/bepinex_be/791/"
                  "BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.791%2Bb782da4.zip",
                  "63a69c40b7930b88e2d986d765c38d0101df0e50149a0b54e3e22f10cd547e3a")
MACOS_BUILD = ROOT / "macos" / "build"
# FM26's progress window while BepInEx prepares it (native/fm26progress/build.py),
# and on Windows the assembly Doorstop starts it from, before BepInEx.
PROGRESS_BUILD = ROOT / "native" / "fm26progress" / "build"
START_PROJECT = ROOT / "src" / "RegenerateFM.FM26.Start"
START_DLL = START_PROJECT / "bin" / "Release" / "net6.0" / "RegenerateFM.Start.dll"
START_TARGET = "target_assembly = BepInEx\\core\\RegenerateFM.Start.dll"
# Not on Windows yet: Epic's FM26 Demo in a Windows VM, started through
# RegenerateFM.Start.dll, kept saying "The server is not reachable" (TODO.md,
# "Progress window on Windows"), so Doorstop starts BepInEx directly there,
# as before. The Mac's window is shipped.
WINDOWS_PROGRESS = False
MACOS_NATIVE = {"BepInEx/core/libfunchook.dylib": "libfunchook.dylib",
                "BepInEx/core/Il2CppInterop.Runtime.dll": "Il2CppInterop.Runtime.dll",
                "BepInEx/core/Il2CppInterop.Common.dll": "Il2CppInterop.Common.dll",
                "BepInEx/core/Il2CppInterop.Generator.dll": "Il2CppInterop.Generator.dll",
                "BepInEx/core/LibCpp2IL.dll": "LibCpp2IL.dll"}
# Tracked files left out of the release's source folder: the README's
# recording and historical research.
NOT_PUBLIC = ("docs/", "native/fm24/research/")


def git_files() -> list[str]:
    try:
        listing = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        raise SystemExit("make_release.py needs git, run in a checkout, to know which files are RegenerateFM's.")
    return sorted(name for name in listing.decode("utf-8").split("\0") if name)


def public_files() -> list[str]:
    return [name for name in git_files() if not name.startswith(NOT_PUBLIC) and (ROOT / name).is_file()]


def licence_files() -> tuple[str, ...]:
    """RegenerateFM's licence and the bundled software's (licenses/), which
    the release, the macOS installer and the packaged companion all carry."""
    texts = sorted(path.relative_to(ROOT).as_posix() for path in (ROOT / "licenses").rglob("*")
                   if path.is_file() and path.suffix in (".txt", ".md"))
    return ("LICENSE", "NOTICE.md", *texts)


def copy_licences(target: Path) -> None:
    for name in licence_files():
        (target / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target / name)


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


def macos_files(archive: Path, doorstop: Path) -> dict[str, tuple[bytes, int]]:
    """The FM26-macOS folder: BepInEx for macOS, the rebuilt hooking pieces, its config and launcher."""
    files = bepinex_files(archive, "libdoorstop.dylib", leave_out=("run_bepinex.sh",))
    later = bepinex_files(doorstop, "libdoorstop.dylib")
    for name in ("libdoorstop.dylib", ".doorstop_version"):
        files[name] = later[name]
    for name in MACOS_NATIVE:
        if name != "BepInEx/core/libfunchook.dylib" and name not in files:
            raise SystemExit(f"The macOS BepInEx archive has no {name} to replace.")
    for name, built in MACOS_NATIVE.items():
        files[name] = ((MACOS_BUILD / built).read_bytes(), 0o644)
    files["BepInEx/config/BepInEx.cfg"] = ((ROOT / "macos/BepInEx.cfg").read_bytes(), 0o644)
    files["run_regeneratefm.sh"] = ((ROOT / "macos/run_regeneratefm.sh").read_bytes(), 0o755)
    files["clear-exception-ports"] = ((MACOS_BUILD / "clear-exception-ports").read_bytes(), 0o755)
    files["regeneratefm-progress"] = (prebuilt(PROGRESS_BUILD / "regeneratefm-progress", "native/fm26progress/build.py --macos"), 0o755)
    files["BepInEx/core/RegenerateFM.Start.dll"] = (prebuilt(START_DLL, f"dotnet build {START_PROJECT.relative_to(ROOT)} -c Release"), 0o644)
    return files


def prebuilt(path: Path, how: str) -> bytes:
    if not path.is_file():
        raise SystemExit(f"{path} is missing; build it with {how}.")
    return path.read_bytes()


def windows_progress_files(loader: dict[str, tuple[bytes, int]]) -> dict[str, tuple[bytes, int]]:
    """FM26's progress window on Windows: the assembly Doorstop now starts, which
    starts the window and then BepInEx, and the window itself."""
    config = loader["doorstop_config.ini"][0].decode("utf-8")
    updated, count = re.subn(r"(?m)^target_assembly = [^\r\n]*", lambda _: START_TARGET, config)
    if count != 1:
        raise SystemExit("BepInEx's doorstop_config.ini has no target_assembly line to change.")
    return {"doorstop_config.ini": (updated.encode("utf-8"), loader["doorstop_config.ini"][1]),
            "BepInEx/core/RegenerateFM.Start.dll": (prebuilt(START_DLL, f"dotnet build {START_PROJECT.relative_to(ROOT)} -c Release"), 0o644),
            "BepInEx/core/regeneratefm-progress.exe": (prebuilt(PROGRESS_BUILD / "regeneratefm-progress.exe", "native/fm26progress/build.py --windows"), 0o755)}


def publish(runtime: str) -> Path:
    """The desktop program published for ``runtime``: one self-contained file."""
    output = PUBLISHED / runtime
    shutil.rmtree(output, ignore_errors=True)
    dotnet = shutil.which("dotnet") or str(Path.home() / ".local" / "dotnet" / "dotnet")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    print(f"Publishing the desktop program for {runtime}")
    subprocess.run([dotnet, "publish", str(DESKTOP), "-c", "Release", "-r", runtime, "-o", str(output), "-v", "q", "-nologo"],
                   check=True, env=env)
    return output


def write(archive: zipfile.ZipFile, name: str, data: bytes, mode: int) -> None:
    info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    # Archive Utility and ditto apply the mode only with the regular-file type bits.
    info.external_attr = (0o100000 | ((mode or 0o644) & 0o7777)) << 16
    archive.writestr(info, data)


def has_crlf(data: bytes) -> bool:
    """Whether text (not a binary file) has Windows line endings."""
    return b"\r\n" in data and b"\0" not in data[:8192]


def crlf_files(root: Path, names) -> list[str]:
    """The release's own text files with Windows line endings, as in a Windows
    checkout made before .gitattributes or a file an editor saved that way;
    install-linux.sh then fails on Linux."""
    return [name for name in names if has_crlf((root / name).read_bytes())]


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
    source = PLUGIN_SOURCE.read_text(encoding="utf-8")
    match = re.search(r'BepInPlugin\("fm\.regenerate", "RegenerateFM", "([^"]+)"\)', source)
    if not match:
        raise SystemExit("Could not read the plugin version.")
    return match.group(1)


def check_private(name: str, data: bytes) -> None:
    """Refuses a file that names this checkout's folder or holds a key."""
    home = str(Path.home()).encode()
    if home in data or str(ROOT).encode() in data or b"sk-proj-" in data:
        raise SystemExit(f"{name} names this computer's folders or holds a key; it was not released.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bepinex-zip", type=Path, help="a local copy of the pinned BepInEx be.760 ZIP")
    parser.add_argument("--no-publish", action="store_true", help="use the desktop program already in dist/publish")
    args = parser.parse_args()
    version = plugin_version()
    # The same name in every release, so GitHub's releases/latest/download link
    # (the front page's Download for Windows & Linux) always finds it.
    target = ROOT / "dist" / "RegenerateFM-Windows-Linux.zip"
    if not PLUGIN_DLL.is_file():
        raise SystemExit(f"Build the FM26 plugin first: {PLUGIN_DLL} is missing (see src/RegenerateFM.FM26/README.md).")
    programs = {}
    for runtime, name in (COMPANION, LINUX_INSTALLER):
        folder = PUBLISHED / runtime if args.no_publish else publish(runtime)
        if not (folder / name).is_file():
            raise SystemExit(f"{folder / name} is missing; publish it, or leave out --no-publish.")
        programs[runtime] = (folder / name).read_bytes()
    public = public_files()
    crlf = crlf_files(ROOT, public)
    if crlf:
        raise SystemExit(f"{len(crlf)} files have Windows line endings, starting with {', '.join(crlf[:3])}. "
                         "Check the files out again so .gitattributes applies (with nothing uncommitted: "
                         "git rm -r -q --cached . && git reset -q --hard).")
    modes = git_modes()
    loader = bepinex_files(bepinex_zip(BEPINEX, args.bepinex_zip), "winhttp.dll")
    if WINDOWS_PROGRESS:
        if not args.no_publish:
            subprocess.run([shutil.which("dotnet") or str(Path.home() / ".local" / "dotnet" / "dotnet"), "build", str(START_PROJECT),
                            "-c", "Release", "-v", "q", "-nologo"], check=True)
        loader.update(windows_progress_files(loader))
    top = f"regeneratefm-{version}"
    plugin = PLUGIN_DLL.read_bytes()
    for name, data in (("the FM26 plugin", plugin), (COMPANION[1], programs[COMPANION[0]]),
                       (LINUX_INSTALLER[1], programs[LINUX_INSTALLER[0]])):
        check_private(name, data)
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        # Every file goes through write(): a fixed date, and a mode from git
        # rather than from a Windows checkout.
        for name in TOP_LEVEL:
            write(archive, f"{top}/{name}", (ROOT / name).read_bytes(), file_mode(ROOT / name, modes))
        write(archive, f"{top}/install-linux.sh", (ROOT / "install-linux.sh").read_bytes(), 0o755)
        for name, (data, mode) in loader.items():
            write(archive, f"{top}/FM26/{name}", data, mode)
        write(archive, f"{top}/FM26/{PLUGIN_FOLDER}/RegenerateFM.dll", plugin, 0o644)
        write(archive, f"{top}/FM24/{COMPANION[1]}", programs[COMPANION[0]], 0o755)
        write(archive, f"{top}/linux/{LINUX_INSTALLER[1]}", programs[LINUX_INSTALLER[0]], 0o755)
        for name in public:
            write(archive, f"{top}/source/{name}", (ROOT / name).read_bytes(), file_mode(ROOT / name, modes))
    print(f"Created {target}: FM26 folder ({len(loader)} BepInEx files and the plugin), the FM24 companion, "
          f"the Linux installer and {len(public)} source files")


if __name__ == "__main__":
    main()
