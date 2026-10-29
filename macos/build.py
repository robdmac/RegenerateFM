#!/usr/bin/env python3
"""Build the native pieces BepInEx needs to run FM26 on macOS.

- libfunchook.dylib: funchook 1.1.3 for x86_64, with patches/funchook-rosetta.patch.
  BepInEx's default hooking library (Dobby) patches code in a way Rosetta 2
  doesn't see, so its hooks never run; funchook's mprotect-based patching works.
- Il2CppInterop.Runtime.dll, Il2CppInterop.Common.dll and
  Il2CppInterop.Generator.dll: Il2CppInterop 1.5.3 (the version in BepInEx
  6.0.0-be.789), with patches/il2cppinterop-macos.patch so that class injection
  finds GameAssembly.dylib and FM26's clang-built code, and the generator
  copes with FM27's methods calling no metadata init function.
- LibCpp2IL.dll: Cpp2IL at the commit BepInEx bundles, with
  patches/libcpp2il-macho-fixups.patch. FM27's GameAssembly.dylib uses chained
  fixups, which LibCpp2IL applies by writing to its copy of the binary, but it
  copies a universal binary's slice into a read-only stream, so BepInEx's
  first start failed with "Stream does not support writing".
- clear-exception-ports: clear_exception_ports.c, which run_regeneratefm.sh
  starts FM26 through when the Epic Games Launcher started it.

Each is cloned at a pinned commit, patched and built into --output. Needs git,
CMake, Xcode's command-line tools and the .NET 8 SDK (Il2CppInterop pins it).
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
FUNCHOOK = ("https://github.com/kubo/funchook.git", "88388db3c69e16c1560fee65c6857d75f5ce6fd5")  # v1.1.3
DISTORM_COMMIT = "ab59d6e193948cfa5d1482fb6c7e64870e9e93b9"
IL2CPPINTEROP = ("https://github.com/BepInEx/Il2CppInterop.git", "dbda1cb353b0f4253345dc45136d170b9e50a5a0")  # 1.5.3
# The LibCpp2IL in BepInEx be.789-791: 2022.1.0-development.1452+558ddd9.
CPP2IL = ("https://github.com/SamboyCoding/Cpp2IL.git", "558ddd98642010897d54316b51fbaa7889fda093")


def run(*command: str | Path, cwd: Path | None = None) -> None:
    subprocess.run([str(part) for part in command], cwd=cwd, check=True)


def checkout(url: str, commit: str, target: Path) -> None:
    run("git", "init", "-q", target)
    run("git", "fetch", "-q", "--depth", "1", url, commit, cwd=target)
    run("git", "checkout", "-q", "FETCH_HEAD", cwd=target)
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=target, check=True,
                          capture_output=True, text=True).stdout.strip()
    if head != commit:
        raise SystemExit(f"{url} checked out {head}, not the pinned {commit}.")


def build_funchook(work: Path, output: Path) -> None:
    source = work / "funchook"
    checkout(*FUNCHOOK, source)
    checkout("https://github.com/gdabah/distorm.git", DISTORM_COMMIT, source / "distorm")
    run("git", "apply", HERE / "patches" / "funchook-rosetta.patch", cwd=source)
    build = source / "build"
    run("cmake", "-S", source, "-B", build, "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_OSX_ARCHITECTURES=x86_64", "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0", "-DFUNCHOOK_CPU=x86",
        "-DFUNCHOOK_BUILD_TESTS=OFF", "-DFUNCHOOK_BUILD_STATIC=OFF", "-DFUNCHOOK_INSTALL=OFF",
        "-DCMAKE_C_FLAGS=-DFUNCHOOK_KEEP_PAGES_ON_DESTROY -DFUNCHOOK_ATOMIC_PATCH -DFUNCHOOK_RESERVE_PAGES=256")
    run("cmake", "--build", build, "-j", "8")
    # BepInEx loads it by name ([DllImport("funchook")]) from BepInEx/core.
    shutil.copyfile(build / "libfunchook.1.1.3.dylib", output / "libfunchook.dylib")


def build_il2cppinterop(work: Path, output: Path, dotnet: str) -> None:
    source = work / "Il2CppInterop"
    checkout(*IL2CPPINTEROP, source)
    run("git", "apply", HERE / "patches" / "il2cppinterop-macos.patch", cwd=source)
    run(dotnet, "build", "Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj", "-c", "Release", "-nologo",
        "-p:GeneratePackageOnBuild=false", cwd=source)
    shutil.copyfile(source / "bin" / "Il2CppInterop.Runtime" / "net6.0" / "Il2CppInterop.Runtime.dll",
                    output / "Il2CppInterop.Runtime.dll")
    shutil.copyfile(source / "bin" / "Il2CppInterop.Common" / "netstandard2.0" / "Il2CppInterop.Common.dll",
                    output / "Il2CppInterop.Common.dll")
    # The generator BepInEx runs on a first start: FM27's (Unity 6.3) methods
    # call no metadata init function, which it required.
    run(dotnet, "build", "Il2CppInterop.Generator/Il2CppInterop.Generator.csproj", "-c", "Release", "-nologo",
        "-f", "netstandard2.1", "-p:GeneratePackageOnBuild=false", cwd=source)
    shutil.copyfile(source / "bin" / "Il2CppInterop.Generator" / "netstandard2.1" / "Il2CppInterop.Generator.dll",
                    output / "Il2CppInterop.Generator.dll")


def build_clear_exception_ports(output: Path) -> None:
    run("xcrun", "clang", "-O2", "-Wall", "-arch", "arm64", "-arch", "x86_64", "-mmacosx-version-min=12.0",
        "-o", output / "clear-exception-ports", HERE / "clear_exception_ports.c")


def build_libcpp2il(work: Path, output: Path, dotnet: str) -> None:
    source = work / "Cpp2IL"
    checkout(*CPP2IL, source)
    run("git", "apply", HERE / "patches" / "libcpp2il-macho-fixups.patch", cwd=source)
    # Its global.json asks for the .NET 9 SDK; any SDK that builds net6.0 will do.
    (source / "global.json").unlink()
    run(dotnet, "build", "LibCpp2IL/LibCpp2IL.csproj", "-c", "Release", "-f", "net6.0", "-nologo",
        "-p:VersionSuffix=development.1452", "-p:CheckEolTargetFramework=false", cwd=source)
    shutil.copyfile(source / "LibCpp2IL" / "bin" / "Release" / "net6.0" / "LibCpp2IL.dll", output / "LibCpp2IL.dll")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output", type=Path, default=HERE / "build")
    parser.add_argument("--dotnet", help="the dotnet command (default: dotnet on PATH, then ~/.local/dotnet)")
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("Build these on macOS: libfunchook.dylib needs Apple's toolchain.")
    local = Path.home() / ".local" / "dotnet" / "dotnet"
    dotnet = args.dotnet or shutil.which("dotnet") or (str(local) if local.is_file() else None)
    for tool, found in (("git", shutil.which("git")), ("cmake", shutil.which("cmake")), ("dotnet", dotnet)):
        if not found:
            parser.error(f"{tool} is required.")
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="regeneratefm-macos-") as scratch:
        build_funchook(Path(scratch), args.output)
        build_il2cppinterop(Path(scratch), args.output, dotnet)
        build_libcpp2il(Path(scratch), args.output, dotnet)
    build_clear_exception_ports(args.output)
    for name in ("libfunchook.dylib", "Il2CppInterop.Runtime.dll", "Il2CppInterop.Common.dll",
                 "Il2CppInterop.Generator.dll", "LibCpp2IL.dll",
                 "clear-exception-ports"):
        print(f"Built {args.output / name}")


if __name__ == "__main__":
    main()
