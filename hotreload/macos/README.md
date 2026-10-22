# FM26 on macOS

The FM26 plugin is the same DLL on macOS. What differs is how BepInEx gets into the game, and four problems BepInEx 6 has with FM26 for Mac. This folder holds the fixes, and `make_release.py` packs them into the release's `FM26-macOS` folder.

Checked on FM26 26.3.2 (Steam, universal `fm.app`) on an Apple M2 with macOS 26: the plugin loads and its build check passes, in 12 cold starts in a row; a career loads and stays up; on a player Overview, **Actions** ends with **Generate face** and a greyed-out **Update face**, and clicking the portrait opens the same two items. The plugin reads the player's ID from the header's data binding (2000091776 for the player tested). **Generate face**, and **Squad > Generate faces…**, open the key dialog; Cancel closes it and saves nothing. No key has been saved and no billed face made on macOS yet.

## What's in FM26-macOS

| File | What it is |
|---|---|
| `BepInEx/`, `dotnet/`, `libdoorstop.dylib`, `.doorstop_version` | [BepInEx 6.0.0-be.789 Unity IL2CPP macOS x64](https://builds.bepinex.dev/projects/bepinex_be/), pinned by SHA-256, without its `run_bepinex.sh` |
| `BepInEx/core/libfunchook.dylib` | funchook 1.1.3 with `patches/funchook-rosetta.patch`, built by `build.py` |
| `BepInEx/core/Il2CppInterop.Runtime.dll`, `Il2CppInterop.Common.dll` | Il2CppInterop 1.5.3 (be.789's version) with `patches/il2cppinterop-macos.patch`, built by `build.py` |
| `BepInEx/config/BepInEx.cfg` | Only `[Detours] DetourProviderType = Funchook`; BepInEx adds the rest |
| `BepInEx/plugins/RegenerateFMHotReload/` | The plugin |
| `run_regeneratefm.sh` | The launcher, used as FM26's Steam launch option: `"<game folder>/run_regeneratefm.sh" %command%` |

Nothing inside `fm.app` changes, so SEGA's signature and notarisation stay intact. FM26's hardened runtime already allows what's needed: its entitlements include `allow-dyld-environment-variables` and `disable-library-validation`.

## The launcher

`run_regeneratefm.sh` starts the game with doorstop's environment, as BepInEx's own script does, but:

- **Rosetta 2.** BepInEx's macOS CoreCLR, Dobby and Cpp2IL are x86_64 only. BepInEx's script prefers arm64 on Apple Silicon, which starts FM26 natively and CoreCLR then can't load. The launcher runs `arch -x86_64`, and passes `DYLD_INSERT_LIBRARIES` and `DYLD_LIBRARY_PATH` with `-e` because `arch` is SIP-protected and drops `DYLD_*` variables.
- **Paths.** BepInEx looks for the game's data in `<game folder>/Data` and for `GameAssembly.dylib` in the game folder. The launcher makes a `Data` symlink to `fm.app/Contents/Resources/Data` and sets `BEPINEX_GAME_ASSEMBLY_PATH` to `fm.app/Contents/Frameworks/GameAssembly.dylib`. That must be the loaded file itself, not a copy: BepInEx resolves every Il2CppInterop `[DllImport("GameAssembly")]` to that path, and a second copy is an uninitialised il2cpp that crashes the game. Cpp2IL reads the universal binary directly.
- **Quarantine.** A ZIP downloaded with a browser leaves every file quarantined, and macOS won't load a quarantined dylib that isn't notarised: it shows "libdoorstop.dylib Not Opened" and FM26 waits behind the dialog. The launcher clears the quarantine flag from `libdoorstop.dylib`, `dotnet` and `BepInEx` (never `fm.app`) before starting the game.
- **Crash log.** `BEPINEX_PRELOADER_LOG` puts BepInEx's start-up crash log in `BepInEx/preloader-error.log`; by default it goes beside the executable inside `fm.app`, which macOS doesn't allow.

## Hooking under Rosetta: funchook instead of Dobby

BepInEx hooks `il2cpp_runtime_invoke` to start the chainloader. With Dobby, BepInEx's default, the hook is written to memory but never runs, and a second Dobby hook on the same page kills the game with SIGBUS. Dobby swaps a patched copy of the page into place rather than writing through `mprotect`, and Rosetta doesn't retranslate the code. A plain `mprotect` → write → `mprotect` patch works, on the original signed app too. funchook patches that way, and BepInEx already supports it through `DetourProviderType = Funchook`, but its macOS ZIP has no funchook library. `patches/funchook-rosetta.patch` adds three things, each behind a compile flag that `build.py` sets:

- **`FUNCHOOK_RESERVE_PAGES=256`.** BepInEx makes one funchook per hook, and each needs a trampoline page within ±2 GB of its target. By the time plugins load, nothing within 2 GB of `GameAssembly.dylib` is free, so later hooks (the plugin's Harmony patches, sometimes Il2CppInterop's own) failed with "Failed to allocate memory in unused regions" and the game aborted. The first allocation now reserves 256 pages near the game's code, and later hooks use them.
- **`FUNCHOOK_ATOMIC_PATCH`.** The 5-byte jump at a hooked function's start is written as one aligned 8-byte atomic store, so a thread running the function, or Rosetta translating it, never sees half a jump.
- **`FUNCHOOK_KEEP_PAGES_ON_DESTROY`.** BepInEx removes its `il2cpp_runtime_invoke` hook from inside that hook while other threads may still be in the trampoline, so trampoline pages are never unmapped.

## Il2CppInterop on macOS

`patches/il2cppinterop-macos.patch`, against Il2CppInterop 1.5.3 (`dbda1cb`):

- **Finding GameAssembly.** .NET 6's `Process.Modules` lists only the main executable on macOS, with a zero base address, so class injection (`ClassInjector`, which the plugin and BepInEx's Unity log hook use) failed with "Sequence contains no matching element". `GameAssemblyModule` asks dyld for `GameAssembly.dylib` and scans only its `__TEXT` segment for signatures; elsewhere it uses `Process.Modules` as before. `XrefScanMethodDb` uses it too.
- **`MetadataCache::GetTypeInfoFromTypeDefinitionIndex`.** Il2CppInterop finds this function by following calls from `il2cpp_image_get_class`. FM26's clang build pads functions with `nop`s, not `int3`, so the xref scanner ran past `Image::GetType`'s tail call into the next function, picked `MetadataCache::GetAssemblyTypeHandle` and hooked it with the wrong signature: the game crashed as soon as start-up finished. On macOS it now follows the tail calls (`Image::GetType` → `MetadataCache::GetTypeInfoFromHandle` → `GlobalMetadata::GetTypeInfoFromHandle` → `GetTypeInfoFromTypeDefinitionIndex`), with `XrefScannerLowLevel.TailCallTarget` and `FollowTailCalls`. In 26.3.2 that is offset `0x5ea0d0` of the x86_64 slice, which the disassembly confirms (the `index == -1` check and `s_TypeInfoTable[index]` lookup).

- **`Class::GetFieldDefaultValue`.** Il2CppInterop takes the last of `Field::StaticGetValueInternal`'s three calls. In this build that is `BlobReader::GetConstantValueFromBlob`, which the literal-field branch calls after `GetFieldDefaultValue(field, &type)`, so the hook took the wrong arguments and FM26 crashed as soon as a career's home screen opened, with or without RegenerateFM. On macOS it now takes the call made right after `lea rsi, [rbp-…]`, the out parameter (`XrefScannerLowLevel.CallWithStackAddressArgument`), and follows its thunk to the function (offset `0x5e9760`).

The other hooks found their targets: `GenericMethod::GetMethod`, `Class::FromIl2CppType` and `Class::FromName`. `Class::Init` uses the `mono_class_instance_size` substitute, with a warning in the log. Upstream Il2CppInterop already matches `GameAssembly.dylib` by name, but that doesn't help with .NET 6's module list.

## Building

On a Mac with git, CMake, Xcode's command-line tools and the .NET 8 SDK (Il2CppInterop's `global.json` asks for it):

```sh
python3 hotreload/macos/build.py
```

It clones funchook and Il2CppInterop at their pinned commits, applies the patches and writes `libfunchook.dylib`, `Il2CppInterop.Runtime.dll` and `Il2CppInterop.Common.dll` to `hotreload/macos/build/`. `make_release.py` then adds the `FM26-macOS` folder; without that folder it leaves macOS out. The plugin itself builds as in [../README.md](../README.md), with the .NET 6 SDK that the repository's `global.json` pins.

The patched libraries stay under their own licences: funchook is GPL-2.0-or-later with a linking exception, Il2CppInterop is LGPL-3.0. The patches and this build script are their modified source, and the release's `source` folder includes them.

## The installer

`installer/RegenerateFMInstaller.swift` is RegenerateFM Installer.app, which the release ships as `RegenerateFM-macOS.dmg`. It carries the FM26-macOS folder in `Contents/Resources`, copies it beside `fm.app` (clearing quarantine from what it copies) and sets the Steam launch option the way `steam_launch.py` does, while Steam is closed. Uninstall takes the launch option out and removes the files; if other BepInEx plugins are installed it removes only RegenerateFM's. `tests/test_macos_installer.py` runs it with `--cli` against a stand-in Steam folder and checks its launch options against `steam_launch.py`.

Build it, after the plugin and `build.py`:

```sh
python3 hotreload/macos/make_installer.py                          # signed with the Developer ID certificate
python3 hotreload/macos/make_installer.py --notarize regeneratefm  # and notarised
python3 hotreload/macos/make_installer.py --identity -             # ad hoc, for a local test
```

It signs every Mach-O file in the payload and the app with the hardened runtime and a timestamp, makes `dist/RegenerateFM-macOS.dmg` and signs it. `--notarize` needs a notarytool keychain profile, made once with `xcrun notarytool store-credentials regeneratefm --apple-id <email> --team-id <team>` and an app-specific password; it then submits the DMG, waits and staples the ticket. FM26's entitlements disable library validation, so the libraries load with RegenerateFM's signature in place of BepInEx's ad hoc ones.

## Graphics reload on macOS

FM26's `ResourcesSubsystem.EnumerateLocalAssetsComplete` takes an `AsyncOperationHandle` struct. Il2CppInterop's Harmony hook passes it with the Windows x64 calling convention, so on macOS (x64 System V, under Rosetta 2) a hook on it hands the game a broken handle: FM26 logs "Attempting to use an invalid operation handle" and never finishes the reload, and the new face appeared only after a restart. On macOS the plugin therefore doesn't hook it. FM26 calls `RegisterResourceLocator`, whose arguments are objects, for each file in the folder as the enumeration completes; when those calls have stopped for 250 ms, the plugin refreshes the panels. Windows and Proton keep the original hook.

Checked on 2026-10-18 through Steam's launch option, with a local stand-in for the image API (`ApiBaseUrl`, nothing billed): Generate new face and then Update face on a player, both shown in the game, with the reload taking 0.6 and 1.0 s.

## Not done yet

- The squad batch on macOS.
- The plugin DLL in a release is built once for both systems. The one tested here was built against be.789's macOS interop assemblies; the Windows release has been built against be.760's.
- Intel Macs (the launcher should work, since x86_64 is native there), other macOS versions and other FM26 builds.

The **FM26 Demo** for Mac (Steam app 3551360, 26.3.2 with its own binaries) works the same way: copy the `FM26-macOS` folder into `Football Manager 26 Demo` and set its launch option (`python3 steam_launch.py fm26demo`). The launcher starts whichever executable `fm.app` names, and the plugin pins the Demo's hashes and saves faces in its user-data folder, `Football Manager 26 Demo`. Checked on 2026-10-21 with the image stand-in: BepInEx's first start, a 21-face First Team batch, and Generate and Update for a staff member. On another build the plugin turns its buttons off, but BepInEx and the hooks still load.
