#!/usr/bin/env python3
"""Build an experimental FM24 WINMM proxy against the user's system DLL."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from pathlib import Path

import pefile


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--system-winmm", type=Path, required=True)
    parser.add_argument("--cc", default="x86_64-w64-mingw32-gcc")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "build")
    parser.add_argument("--perf", action="store_true",
                        help="Log FM Face's own time on FM24's UI thread once a minute (fmface24.log)")
    args = parser.parse_args()
    compiler = shutil.which(args.cc)
    if not compiler:
        parser.error("A MinGW-w64 C compiler is required; supply --cc if necessary.")
    pe = pefile.PE(str(args.system_winmm), fast_load=True)
    pe.parse_data_directories(directories=[0])
    symbols = pe.DIRECTORY_ENTRY_EXPORT.symbols
    if pe.FILE_HEADER.Machine != 0x8664 or not symbols:
        parser.error("--system-winmm must be the 64-bit system WINMM DLL used by FM24.")
    args.output.mkdir(parents=True, exist_ok=True)
    supported = json.loads(Path(__file__).with_name("supported_build.json").read_text())
    constants = []
    for name, value in (("FM24_HASH_BYTES", bytes.fromhex(supported["sha256"])),
                        ("FM24_HANDLER_BYTES", bytes.fromhex(supported["eventHandlerPrologue"]))):
        constants.append(f"#define {name} {{" + ",".join(f"0x{byte:02x}" for byte in value) + "}")
    for name, key in (("HANDLER_RVA", "eventHandlerRva"), ("VTABLE_SLOT_RVA", "eventVtableSlotRva"),
                      ("FM24_TIMESTAMP", "timestamp"), ("FM24_IMAGE_SIZE", "imageSize")):
        constants.append(f"#define {name} {supported[key]}u")
    # Functions of the validated build: FM24's own "reload current skin"
    # sequence, the Squad "Players" menu provider that the DLL extends, and
    # the query/message dialog functions FM24 uses for its own questions.
    for group, prefix in (("skinReload", "RELOAD_"), ("squadMenu", "SQUAD_MENU_"), ("dialog", "DIALOG_")):
        for key, value in supported[group].items():
            name = prefix + "".join("_" + c if c.isupper() else c.upper() for c in key)
            constants.append(f"#define {name} {value}u")
    (args.output / "supported_build.h").write_text("\n".join(constants) + "\n")
    # API names and ordinals only: no game or system DLL code is copied.
    names = [symbol.name.decode("ascii") if symbol.name else None for symbol in symbols]
    source = ["#include <windows.h>", "extern void *fmface_resolve_export(unsigned int);",
              f"void *fmface_exports[{len(symbols)}];",
              "const char *fmface_export_names[] = {" +
              ",".join('"' + name + '"' if name else "0" for name in names) + "};",
              "const unsigned short fmface_export_ordinals[] = {" +
              ",".join(str(symbol.ordinal) for symbol in symbols) + "};",
              f"const unsigned int fmface_export_count = {len(symbols)};"]
    exports = ["LIBRARY winmm", "EXPORTS"]
    for index, (symbol, name) in enumerate(zip(symbols, names)):
        stub = f"fmface_export_{index}"
        exports.append(f"    {name or stub}={stub} @{symbol.ordinal}" + (" NONAME" if name is None else ""))
        # Preserve all Windows x64 argument registers while resolving lazily.
        # Entry RSP is 8 mod 16. Four pushes and 104 bytes align the helper call
        # and provide its 32-byte shadow space plus room for XMM0..3.
        asm = [f"cmpq $0, fmface_exports+{index * 8}(%rip)", "jne 1f",
               "pushq %rcx", "pushq %rdx", "pushq %r8", "pushq %r9", "subq $104, %rsp"]
        asm += [f"movdqu %xmm{i}, {32 + i * 16}(%rsp)" for i in range(4)]
        asm += [f"movl ${index}, %ecx", "call fmface_resolve_export"]
        asm += [f"movdqu {32 + i * 16}(%rsp), %xmm{i}" for i in range(4)]
        asm += ["addq $104, %rsp", "popq %r9", "popq %r8", "popq %rdx", "popq %rcx",
                "1:", f"jmp *fmface_exports+{index * 8}(%rip)"]
        source.append(f'__attribute__((naked)) void {stub}(void) {{ __asm__("' +
                      "\\n".join(asm) + '"); }')
    generated = args.output / "exports.c"
    generated.write_text("\n".join(source) + "\n")
    definition = args.output / "winmm.def"
    definition.write_text("\n".join(exports) + "\n")
    target = args.output / "winmm.dll"
    subprocess.run([compiler, "-shared", "-O2", "-Wall", "-Wextra", "-Werror",
                    *(["-DFMFACE_PERF"] if args.perf else []),
                    "-static-libgcc", "-I", str(args.output), str(Path(__file__).with_name("fmface24.c")),
                    str(generated), str(definition), "-lws2_32", "-lbcrypt", "-o", str(target)],
                   check=True)
    print(f"Built {target} with {len(symbols)} forwarded WINMM exports.")


if __name__ == "__main__":
    main()
