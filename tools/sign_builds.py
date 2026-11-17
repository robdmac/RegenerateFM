#!/usr/bin/env python3
"""The list of game builds RegenerateFM has been checked on, which the plugin
downloads at each start (src/RegenerateFM.Core/CheckedBuilds.cs), so a game
update can be marked as checked without a new release.

    python3 tools/sign_builds.py add --game-dir "<game folder>" --name "FM27 27.1.0 (Steam)"
    python3 tools/sign_builds.py add --os macos --game-assembly <sha256> --game-plugin <sha256> \\
        --user-data "Football Manager 27" --name "FM27 27.1.0 (Steam)"
    python3 tools/sign_builds.py sign     (after editing builds/builds.json by hand)
    python3 tools/sign_builds.py check    (the signed file matches builds.json and is signed)

builds/builds.json is the list to read and review. add and sign write it
with a new "issued" time and sign it into builds/regeneratefm-builds.json
with the release key (--signing-key, $REGENERATEFM_SIGNING_KEY or
~/.regeneratefm/update-signing-key.pem) by openssl, the same key the
self-update uses. Players get a new build once both files are merged to main
on GitHub. --game-dir reads a Windows game folder (GameAssembly.dll) or a Mac
one (fm.app), and the user data folder's name from Unity's app.info.
"""

from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_release  # noqa: E402

ROOT = make_release.ROOT
BUILDS = ROOT / "builds" / "builds.json"
SIGNED = ROOT / "builds" / "regeneratefm-builds.json"
USER_DATA = re.compile(r"^Football Manager \d{2}( Demo)?$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def game_folder(folder: Path) -> dict:
    """The build in a game folder: Windows' layout, or the Mac's fm.app."""
    app = folder / "fm.app" / "Contents"
    if app.is_dir():
        os_name = "macos"
        assembly = app / "Frameworks" / "GameAssembly.dylib"
        plugin = app / "PlugIns" / "game_plugin.bundle" / "Contents" / "MacOS" / "game_plugin"
        app_info = app / "Resources" / "Data" / "app.info"
    else:
        os_name = "windows"
        assembly = folder / "GameAssembly.dll"
        plugin = folder / "fm_Data" / "Plugins" / "x86_64" / "game_plugin.dll"
        app_info = folder / "fm_Data" / "app.info"
    for path in (assembly, plugin, app_info):
        if not path.is_file():
            raise SystemExit(f"{folder} isn't a Football Manager game folder: {path} is missing.")
    lines = app_info.read_text(encoding="utf-8").splitlines()
    user_data = lines[1].strip() if len(lines) > 1 else ""
    return {"os": os_name, "game_assembly": file_hash(assembly), "game_plugin": file_hash(plugin), "user_data": user_data}


def check_build(build: dict) -> None:
    if build.get("os") not in ("windows", "macos"):
        raise SystemExit(f"{build.get('name')}: os must be windows or macos.")
    for field in ("game_assembly", "game_plugin"):
        if not SHA256.match(build.get(field) or ""):
            raise SystemExit(f"{build.get('name')}: {field} must be a lower-case SHA-256.")
    if not USER_DATA.match(build.get("user_data") or ""):
        raise SystemExit(f"{build.get('name')}: user_data must be the game's user data folder, like \"Football Manager 27\".")
    if not (build.get("name") or "").strip():
        raise SystemExit("Each build needs a name.")


def load() -> dict:
    return json.loads(BUILDS.read_text(encoding="utf-8"))


def payload_bytes(listing: dict) -> bytes:
    return (json.dumps(listing, indent=1, ensure_ascii=False) + "\n").encode("utf-8")


def sign(listing: dict, key: Path) -> None:
    """Writes builds.json with a new issued time and the signed file."""
    if not key.is_file():
        raise SystemExit(f"The release signing key {key} is missing.")
    for build in listing["builds"]:
        check_build(build)
    public = subprocess.run([make_release.openssl(), "ec", "-in", str(key), "-pubout", "-outform", "DER"],
                            check=True, capture_output=True).stdout
    if public != release_key():
        raise SystemExit(f"{key} isn't the key SelfUpdate.cs's ReleaseKey checks for.")
    listing["issued"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    payload = payload_bytes(listing)
    with tempfile.TemporaryDirectory() as folder:
        payload_file = Path(folder) / "payload.json"
        payload_file.write_bytes(payload)
        signature = subprocess.run([make_release.openssl(), "dgst", "-sha256", "-sign", str(key), str(payload_file)],
                                   check=True, capture_output=True).stdout
    BUILDS.write_bytes(payload)
    SIGNED.write_text(json.dumps({"payload": base64.b64encode(payload).decode("ascii"),
                                  "signature": base64.b64encode(signature).decode("ascii")}, indent=1) + "\n",
                      encoding="utf-8")
    print(f"Signed {len(listing['builds'])} builds, issued {listing['issued']}: merge builds/ to main to publish them.")


def release_key() -> bytes:
    built_in = re.search(r'ReleaseKey =\s*"([A-Za-z0-9+/=]+)"', make_release.SELF_UPDATE.read_text(encoding="utf-8"))
    if not built_in:
        raise SystemExit("SelfUpdate.cs's ReleaseKey wasn't found.")
    return base64.b64decode(built_in.group(1))


def check() -> None:
    """The signed file holds builds.json exactly, signed with the release key."""
    signed = json.loads(SIGNED.read_text(encoding="utf-8"))
    payload = base64.b64decode(signed["payload"])
    if payload != BUILDS.read_bytes():
        raise SystemExit(f"{SIGNED.name} doesn't match {BUILDS.name}: run sign.")
    with tempfile.TemporaryDirectory() as folder:
        public_file = Path(folder) / "public.der"
        public_file.write_bytes(release_key())
        payload_file = Path(folder) / "payload.json"
        payload_file.write_bytes(payload)
        signature_file = Path(folder) / "signature.der"
        signature_file.write_bytes(base64.b64decode(signed["signature"]))
        result = subprocess.run([make_release.openssl(), "dgst", "-sha256", "-keyform", "DER", "-verify", str(public_file),
                                 "-signature", str(signature_file), str(payload_file)], capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"{SIGNED.name} isn't signed with the release key.")
    listing = json.loads(payload)
    for build in listing["builds"]:
        check_build(build)
    print(f"{SIGNED.name}: {len(listing['builds'])} builds, issued {listing['issued']}, signed with the release key.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--signing-key", type=Path,
                        default=Path(os.environ.get("REGENERATEFM_SIGNING_KEY") or make_release.DEFAULT_SIGNING_KEY))
    commands = parser.add_subparsers(dest="command", required=True)
    add = commands.add_parser("add", help="add a build and sign the list")
    add.add_argument("--name", required=True, help='how the log names it, like "FM27 27.1.0 (Steam)"')
    add.add_argument("--game-dir", type=Path, help="the game folder to read the build from")
    add.add_argument("--os", choices=("windows", "macos"))
    add.add_argument("--game-assembly", help="SHA-256 of GameAssembly.dll or GameAssembly.dylib")
    add.add_argument("--game-plugin", help="SHA-256 of game_plugin.dll or the Mac's game_plugin")
    add.add_argument("--user-data", help='the game\'s user data folder, like "Football Manager 27"')
    commands.add_parser("sign", help="sign builds.json as it is")
    commands.add_parser("check", help="check the signed file against builds.json")
    args = parser.parse_args()
    if args.command == "check":
        check()
        return
    listing = load()
    if args.command == "add":
        if args.game_dir:
            build = game_folder(args.game_dir)
        else:
            if not (args.os and args.game_assembly and args.game_plugin and args.user_data):
                raise SystemExit("Give --game-dir, or --os, --game-assembly, --game-plugin and --user-data.")
            build = {"os": args.os, "game_assembly": args.game_assembly.lower(), "game_plugin": args.game_plugin.lower(),
                     "user_data": args.user_data}
        build = {"name": args.name, **build}
        check_build(build)
        same = [known for known in listing["builds"] if known["os"] == build["os"] and
                known["game_assembly"] == build["game_assembly"] and known["game_plugin"] == build["game_plugin"]]
        if same:
            raise SystemExit(f"That build is already listed, as {same[0]['name']}.")
        listing["builds"].append(build)
        print(f"Added {build['name']} ({build['os']}, {build['user_data']}).")
    sign(listing, args.signing_key)


if __name__ == "__main__":
    main()
