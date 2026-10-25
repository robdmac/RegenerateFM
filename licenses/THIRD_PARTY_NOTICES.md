# Third-party software in the RegenerateFM release

RegenerateFM's own code is under the GNU GPL version 3 or later (`LICENSE`), with
the additional permission and terms in `NOTICE.md`. The release also bundles the
software below, each under its own licence; the files named in the tables are in
this `licenses` folder (`third-party/` unless shown otherwise), and
`licenses/fetch_licenses.py` fetches them from each project.

## Source of the GPL and LGPL parts

Every GitHub release has `RegenerateFM-third-party-source.zip` beside the
release ZIP and `RegenerateFM-macOS.dmg`. It holds the source of BepInEx,
UnityDoorstop, Il2CppInterop, funchook and diStorm3 at the commits bundled
here, the macOS patches (`macos/patches`) and the script that applies
them and builds the patched parts (`macos/build.py`).
`tools/make_source_archive.py` makes it.

## FM26 (the release ZIP's `FM26` folder)

The [BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/)
build (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760+a1afbfb.zip`, SHA-256
`9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea`), unmodified,
which loads the RegenerateFM plugin into FM26:

| Component | Licence | Licence file | Source |
|---|---|---|---|
| BepInEx | LGPL-2.1 | `BepInEx-LICENSE.txt` | https://github.com/BepInEx/BepInEx |
| UnityDoorstop 4.5.0 (`winhttp.dll`) | LGPL-2.1 | `UnityDoorstop.txt` | https://github.com/NeighTools/UnityDoorstop |
| Il2CppInterop | LGPL-3.0 | `Il2CppInterop-LICENSE.txt`, with the GPL-3.0 in `LICENSE` | https://github.com/BepInEx/Il2CppInterop |
| HarmonyX (`0Harmony.dll`) | MIT | `HarmonyX.txt` | https://github.com/BepInEx/HarmonyX |
| MonoMod | MIT | `MonoMod.txt` | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | MIT | `Mono.Cecil.txt` | https://github.com/jbevain/cecil |
| Cpp2IL, LibCpp2IL, WasmDisassembler, StableNameDotNet | MIT | `Cpp2IL.txt` | https://github.com/SamboyCoding/Cpp2IL |
| Disarm | MIT | `Disarm.txt` | https://github.com/SamboyCoding/Disarm |
| AsmResolver | MIT | `AsmResolver.txt` | https://github.com/Washi1337/AsmResolver |
| AssetRipper.Primitives, AssetRipper.CIL | MIT | `AssetRipper.Primitives.txt`, `AssetRipper.CIL.txt` | https://github.com/AssetRipper |
| Iced | MIT | `Iced.txt` | https://github.com/icedland/iced |
| Gee.External.Capstone (Capstone.NET) | MIT | `Capstone.NET.txt` | https://github.com/9ee1/Capstone.NET |
| Capstone | BSD-3-Clause, with LLVM-derived parts | `Capstone.txt`, `Capstone-LLVM.txt` | https://github.com/capstone-engine/capstone |
| Dobby (`dobby.dll`) | Apache-2.0 | `Dobby.txt` | https://github.com/jmpews/Dobby |
| SemanticVersioning | MIT | `SemanticVersioning.txt` | https://github.com/adamreeve/semver.net |
| .NET runtime (`dotnet` folder) | MIT, with its own third-party notices | `dotnet-runtime.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |

## FM26 on macOS (the macOS installer's `FM26-macOS` folder)

The same components from the
[BepInEx 6.0.0-be.789 Unity IL2CPP macOS x64](https://builds.bepinex.dev/projects/bepinex_be/)
build (`BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.789+6cea286.zip`, SHA-256
`cc2341544d271f8bcea4e7415acad10766e968d236aaee30cdd79780ec7db00b`), where
UnityDoorstop is `libdoorstop.dylib` and Dobby is `libdobby.dylib`, with these
changes:

| Component | Licence | Licence file | Source |
|---|---|---|---|
| Il2CppInterop 1.5.3 (`Il2CppInterop.Runtime.dll`, `Il2CppInterop.Common.dll`), modified | LGPL-3.0 | `Il2CppInterop-LICENSE.txt`, with the GPL-3.0 in `LICENSE` | https://github.com/BepInEx/Il2CppInterop at `dbda1cb`, with `macos/patches/il2cppinterop-macos.patch` |
| funchook 1.1.3 (`libfunchook.dylib`), modified | GPL-2.0-or-later with a linking exception | `funchook.txt` | https://github.com/kubo/funchook at `88388db`, with `macos/patches/funchook-rosetta.patch` |
| diStorm3 (inside `libfunchook.dylib`) | BSD-3-Clause | `diStorm3.txt` | https://github.com/gdabah/distorm at `ab59d6e` |

## The FM24 companion and the Linux installer

The FM24 companion (`FM24/regeneratefm-fm24.exe`, and `FM24-macOS` in the macOS
installer) and the Linux installer (`linux/regeneratefm`) are each one
self-contained .NET program, built from `src/RegenerateFM.Desktop`:

| Component | Licence | Licence file | Source |
|---|---|---|---|
| .NET runtime | MIT, with its own third-party notices | `dotnet-runtime.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| ZstdSharp (reads FM24's archives) | MIT | `ZstdSharp.txt` | https://github.com/oleg-st/ZstdSharp |
| Zstandard (zstd, which ZstdSharp is a port of) | BSD-3-Clause | `zstd.txt` | https://github.com/facebook/zstd |
| StbImageSharp (reads FM24's photos), a port of stb_image | Public domain or MIT | `StbImageSharp.txt` (stb's licence) | https://github.com/StbSharp/StbImageSharp |
