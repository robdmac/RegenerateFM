#!/usr/bin/env python3
"""Build RegenerateFM Installer.app for FM26 and FM24 on macOS and pack it in a DMG.

    python3 hotreload/macos/make_installer.py [--identity NAME] [--notarize PROFILE]

The app (installer/RegenerateFMInstaller.swift) carries the release ZIP's
FM26-macOS folder (make_release.macos_files and the plugin), so it needs the
plugin built (dotnet build -c Release hotreload) and hotreload/macos/build.py
run first. For FM24 it carries the packaged companion
(fm24/build_companion_macos.py, which needs fm24/native_menu/build_macos.py). It writes dist/RegenerateFM-macOS.dmg; the name stays the same in
every release, so GitHub's releases/latest/download link always finds it.

Every Mach-O file in the payload and the app are signed with the Developer
ID Application certificate in the keychain (or --identity; "-" signs ad hoc
for a local test), with the hardened runtime and a secure timestamp. With
--notarize, the DMG goes to Apple's notary service with the notarytool
keychain profile of that name, made once with
    xcrun notarytool store-credentials <profile> --apple-id <email> --team-id <team>
and the ticket is stapled to it.
"""

from __future__ import annotations

import argparse
import json
import plistlib
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
sys.path.insert(0, str(ROOT))
import make_release  # noqa: E402

APP_NAME = "RegenerateFM Installer"
BUNDLE_ID = "fm.regenerate.installer"
SOURCE = HERE / "installer" / "RegenerateFMInstaller.swift"
# The 1024 px icon, on macOS's icon grid; made into AppIcon.icns.
ICON = HERE / "installer" / "AppIcon.png"
MINIMUM_MACOS = "12.0"
DMG = ROOT / "dist" / "RegenerateFM-macOS.dmg"
FM24_COMPANION = ROOT / "dist" / "regeneratefm-fm24-macos" / "regeneratefm-fm24"
# Shown beside the payload in Contents/Resources.
NOTICES = ("LICENSE", "NOTICE.md", "licenses/THIRD_PARTY_NOTICES.md", "licenses/BepInEx-LICENSE.txt",
           "licenses/Il2CppInterop-LICENSE.txt")
MACHO_MAGIC = (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca")


def run(*command: str | Path | int) -> None:
    subprocess.run([str(part) for part in command], check=True, stdout=subprocess.DEVNULL)


def developer_id() -> str:
    found = subprocess.run(["security", "find-identity", "-v", "-p", "codesigning"],
                           capture_output=True, text=True, check=True).stdout
    names = [line.split('"')[1] for line in found.splitlines() if '"Developer ID Application:' in line]
    if not names:
        raise SystemExit("No Developer ID Application certificate in the keychain; pass --identity "
                         "(or --identity - to sign ad hoc for a local test).")
    return names[0]


def is_macho(path: Path) -> bool:
    with path.open("rb") as handle:
        return handle.read(4) in MACHO_MAGIC


def sign(path: Path, identity: str) -> None:
    options = ["--force", "--options", "runtime", "--sign", identity]
    if identity != "-":
        options.insert(0, "--timestamp")
    run("codesign", *options, path)


def notarize(path: Path, profile: str) -> None:
    result = subprocess.run(["xcrun", "notarytool", "submit", path, "--keychain-profile", profile, "--wait",
                             "--output-format", "json"], capture_output=True, text=True, check=False)
    try:
        answer = json.loads(result.stdout)
    except ValueError:
        raise SystemExit(f"notarytool failed: {result.stderr.strip() or result.stdout.strip()}")
    if answer.get("status") != "Accepted":
        log = subprocess.run(["xcrun", "notarytool", "log", answer.get("id", ""), "--keychain-profile", profile],
                             capture_output=True, text=True, check=False).stdout
        raise SystemExit(f"Apple's notary service answered {answer.get('status')}: {answer.get('message')}\n{log}")
    print(f"Notarised ({answer.get('id')})")


def build_executable(target: Path, work: Path) -> None:
    """A universal (arm64 and x86_64) build of the installer."""
    slices = []
    for arch in ("arm64", "x86_64"):
        out = work / f"installer-{arch}"
        run("xcrun", "swiftc", "-parse-as-library", "-O", "-target", f"{arch}-apple-macos{MINIMUM_MACOS}",
            SOURCE, "-o", out)
        slices.append(out)
    run("lipo", "-create", *slices, "-output", target)


def build_icon(target: Path, work: Path) -> None:
    iconset = work / "AppIcon.iconset"
    iconset.mkdir()
    for size in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
            run("sips", "-z", size * scale, size * scale, ICON, "--out", iconset / name)
    run("iconutil", "-c", "icns", iconset, "-o", target)


def build_app(version: str, payload: dict[str, tuple[bytes, int]], identity: str, work: Path) -> Path:
    app = work / f"{APP_NAME}.app"
    contents = app / "Contents"
    (contents / "MacOS").mkdir(parents=True)
    build_executable(contents / "MacOS" / APP_NAME, work)
    with (contents / "Info.plist").open("wb") as handle:
        plistlib.dump({
            "CFBundleName": APP_NAME, "CFBundleDisplayName": APP_NAME, "CFBundleExecutable": APP_NAME,
            "CFBundleIdentifier": BUNDLE_ID, "CFBundlePackageType": "APPL", "CFBundleInfoDictionaryVersion": "6.0",
            "CFBundleShortVersionString": version, "CFBundleVersion": version,
            "CFBundleIconFile": "AppIcon", "LSMinimumSystemVersion": MINIMUM_MACOS, "NSHighResolutionCapable": True,
            "NSHumanReadableCopyright": "Copyright © 2026 Rob Macrae. GNU GPL v3 or later. BepInEx, .NET and Il2CppInterop are under their own licences.",
        }, handle)
    resources = contents / "Resources"
    resources.mkdir()
    build_icon(resources / "AppIcon.icns", work)
    machos = []
    for name, (data, mode) in sorted(payload.items()):
        path = resources / "FM26-macOS" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        path.chmod(mode & 0o777 or 0o644)
        if is_macho(path):
            machos.append(path)
    # The FM24 companion as PyInstaller made it, symbolic links and all.
    companion = resources / "FM24-macOS" / FM24_COMPANION.name
    shutil.copytree(FM24_COMPANION, companion, symlinks=True)
    frameworks = sorted(path for path in companion.rglob("*.framework") if not path.is_symlink())
    machos += [path for path in sorted(companion.rglob("*"))
               if path.is_file() and not path.is_symlink() and is_macho(path)]
    for name in NOTICES:
        shutil.copy2(ROOT / name, resources / Path(name).name)
    # Inside out: the payload's libraries and tools (the companion's
    # executable last), its Python framework, then the app that seals them.
    executable = companion / FM24_COMPANION.name
    for path in sorted(machos, key=lambda path: path == executable):
        sign(path, identity)
    for path in frameworks:
        sign(path, identity)
    sign(app, identity)
    run("codesign", "--verify", "--deep", "--strict", app)
    print(f"Signed {len(machos)} payload files and {app.name} as {identity}")
    return app


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--identity", help='the signing identity (default: the Developer ID Application one; "-" ad hoc)')
    parser.add_argument("--notarize", metavar="PROFILE", help="notarise with this notarytool keychain profile")
    parser.add_argument("--bepinex-macos-zip", type=Path, help="a local copy of the pinned macOS BepInEx be.789 ZIP")
    args = parser.parse_args()
    if sys.platform != "darwin":
        raise SystemExit("The macOS installer is built on a Mac.")
    identity = args.identity or developer_id()
    if args.notarize and identity == "-":
        raise SystemExit("Apple notarises only Developer ID signed apps.")
    plugin = ROOT / make_release.PLUGIN_DLL
    missing = [str(path) for path in [plugin, FM24_COMPANION / FM24_COMPANION.name] +
               [make_release.MACOS_BUILD / built for built in make_release.MACOS_NATIVE.values()]
               if not path.is_file()]
    if missing:
        raise SystemExit("Build these first (dotnet build -c Release hotreload; python3 hotreload/macos/build.py; "
                         "fm24/build_companion_macos.py): " + ", ".join(missing))
    payload = make_release.macos_files(make_release.bepinex_zip(make_release.BEPINEX_MACOS, args.bepinex_macos_zip))
    payload["BepInEx/plugins/RegenerateFMHotReload/RegenerateFMHotReload.dll"] = (plugin.read_bytes(), 0o644)
    version = make_release.plugin_version()
    with tempfile.TemporaryDirectory() as temporary:
        work = Path(temporary)
        app = build_app(version, payload, identity, work)
        volume = work / "volume"
        volume.mkdir()
        run("ditto", app, volume / app.name)
        DMG.parent.mkdir(exist_ok=True)
        DMG.unlink(missing_ok=True)
        run("hdiutil", "create", "-quiet", "-volname", f"RegenerateFM {version}", "-srcfolder", volume,
            "-fs", "HFS+", "-format", "UDZO", "-ov", DMG)
    if identity != "-":
        run("codesign", "--timestamp", "--sign", identity, DMG)
    if args.notarize:
        notarize(DMG, args.notarize)
        run("xcrun", "stapler", "staple", DMG)
        run("spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose", DMG)
    state = "notarised" if args.notarize else "not notarised"
    print(f"Created {DMG} (RegenerateFM {version}, {state})")


if __name__ == "__main__":
    main()
