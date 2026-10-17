#!/usr/bin/env python3
"""Build the FM24 FM Face library for FM24 24.4.2 on macOS (Apple Silicon)."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import uuid
from pathlib import Path

HERE = Path(__file__).parent


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cc", default="clang")
    parser.add_argument("--output", type=Path, default=HERE / "build-macos")
    args = parser.parse_args()
    compiler = shutil.which(args.cc)
    if not compiler:
        parser.error("Xcode's command-line tools (clang) are required; supply --cc if necessary.")
    supported = json.loads((HERE / "supported_build_macos.json").read_text())
    constants = ["#define FM24_UUID_BYTES {" +
                 ",".join(f"0x{byte:02x}" for byte in uuid.UUID(supported["arm64Uuid"]).bytes) + "}"]
    for name, key in (("HANDLER_OFFSET", "eventHandler"), ("VTABLE_SLOT_OFFSET", "eventVtableSlot")):
        constants.append(f"#define {name} {supported[key]}u")
    # The same groups as supported_build.json, as offsets from the image header.
    for group, prefix in (("skinReload", "RELOAD_"), ("squadMenu", "SQUAD_MENU_"), ("dialog", "DIALOG_"),
                          ("profileMenu", "PROFILE_MENU_"), ("objectBrowser", "BROWSER_")):
        for key, value in supported[group].items():
            name = prefix + "".join("_" + c if c.isupper() else c.upper() for c in key)
            constants.append(f"#define {name} {value}u")
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "supported_build_macos.h").write_text("\n".join(constants) + "\n")
    target = args.output / "libfmface24.dylib"
    subprocess.run([compiler, "-arch", "arm64", "-dynamiclib", "-O2", "-Wall", "-Wextra", "-Werror",
                    "-mmacosx-version-min=11.0", "-I", str(args.output),
                    str(HERE / "fmface24_macos.c"), "-o", str(target)], check=True)
    print(f"Built {target}.")


if __name__ == "__main__":
    main()
