# Third-party software in the FM Face release

FM Face's own code is under the MIT licence in `LICENSE`. The release ZIP's
`FM26` folder also contains, unmodified, the
[BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/)
build (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.760+a1afbfb.zip`, SHA-256
`9753b825578a3c3a31cc10067cd45a44a7bf56d3c34c4679e24d6adfd0fba8ea`), which
loads the FM Face plugin into FM26. Its parts and their licences:

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
