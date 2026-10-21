# Third-party software in the RegenerateFM release

RegenerateFM's own code is under the MIT licence in `LICENSE`. The release ZIP's
`FM26` folder also contains, unmodified, the
[BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/)
build (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760+a1afbfb.zip`, SHA-256
`9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea`), which
loads the RegenerateFM plugin into FM26. Its parts and their licences:

| Component | Licence | Source |
|---|---|---|
| BepInEx | LGPL-2.1 (`BepInEx-LICENSE.txt`) | https://github.com/BepInEx/BepInEx |
| UnityDoorstop (`winhttp.dll`) | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop |
| Il2CppInterop | LGPL-3.0 (`Il2CppInterop-LICENSE.txt`) | https://github.com/BepInEx/Il2CppInterop |
| HarmonyX (`0Harmony.dll`) | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | MIT | https://github.com/jbevain/cecil |
| Cpp2IL, LibCpp2IL, WasmDisassembler, StableNameDotNet | MIT | https://github.com/SamboyCoding/Cpp2IL |
| Disarm | MIT | https://github.com/SamboyCoding/Disarm |
| AsmResolver | MIT | https://github.com/Washi1337/AsmResolver |
| AssetRipper.Primitives, AssetRipper.CIL | MIT | https://github.com/AssetRipper |
| Iced | MIT | https://github.com/icedland/iced |
| Gee.External.Capstone (Capstone) | MIT / BSD-3-Clause | https://github.com/9ee1/Capstone.NET, https://github.com/capstone-engine/capstone |
| Dobby (`dobby.dll`) | Apache-2.0 | https://github.com/jmpews/Dobby |
| SemanticVersioning | MIT | https://github.com/adamreeve/semver.net |
| .NET runtime (`dotnet` folder) | MIT | https://github.com/dotnet/runtime |

The FM24 companion (`FM24` folder) is packaged with PyInstaller and includes
Python 3.11 (PSF licence), Pillow (MIT-CMU) and zstandard (BSD-3-Clause).

The `FM26-macOS` folder contains the same components from the
[BepInEx 6.0.0-be.789 Unity IL2CPP macOS x64](https://builds.bepinex.dev/projects/bepinex_be/)
build (`BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.789+6cea286.zip`, SHA-256
`cc2341544d271f8bcea4e7415acad10766e968d236aaee30cdd79780ec7db00b`), where
UnityDoorstop is `libdoorstop.dylib` and Dobby is `libdobby.dylib`, with these
changes:

| Component | Licence | Source |
|---|---|---|
| Il2CppInterop 1.5.3 (`Il2CppInterop.Runtime.dll`, `Il2CppInterop.Common.dll`), modified | LGPL-3.0 (`Il2CppInterop-LICENSE.txt`) | https://github.com/BepInEx/Il2CppInterop at `dbda1cb`, with `source/hotreload/macos/patches/il2cppinterop-macos.patch` |
| funchook 1.1.3 (`libfunchook.dylib`), modified | GPL-2.0-or-later with a linking exception | https://github.com/kubo/funchook at `88388db`, with `source/hotreload/macos/patches/funchook-rosetta.patch` |
| diStorm3 (inside `libfunchook.dylib`) | BSD-3-Clause | https://github.com/gdabah/distorm at `ab59d6e` |

`source/hotreload/macos/build.py` rebuilds both modified components from
those commits and patches.
