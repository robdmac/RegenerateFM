# FM24 native load probe

The subsequent [native portrait menu prototype](../native_menu/README.md) now
provides a validated selection hook for the tested executable. This directory
records the earlier loader and compiled Overview-menu investigation; its probe
DLL remains unsuitable for normal play.

This **experimental, temporary** WINMM replacement only checks whether the
Windows version of FM24 can load our code under Proton. It logs
`FMFACE_FM24_WINMM_PROBE_LOADED` through `OutputDebugStringA` and forwards FM24's
two static WINMM imports to the system DLL. It does not add UI or forward every WINMM export, so
do not distribute or use it for normal play.

Build a 64-bit Windows DLL from `winmm_probe.c` and `winmm_probe.def` with
MinGW-w64. Copy it as `winmm.dll` beside `fm.exe`, run FM24 with
`WINEDLLOVERRIDES=\"winmm=n,b\"` and Wine's `+debugstr` logging, then remove
the DLL after the load check. No game archive or save needs modification.

A local test on FM24 24.4.2 under Proton 9 confirmed that the DLL loads and
`timeBeginPeriod` is called. The forwarding version reached the main menu. The
tested executable's SHA-256 is
`e1059eee82fa7832188831521a3fa633ec3260dd98d03da48b16661147e3ab48`.
This establishes an in-process extension route, but no native menu insertion yet.
The installed probe DLL was removed after testing. A production plugin must forward the full
WINMM API, identify the FM24 build, and establish validated hooks for the
compiled Overview menu and click action. The separate native portrait popup uses
the game-screen event hook described in the newer prototype.

FM24's visible Overview dropdown contains profile views (Profile, Attributes,
Happiness, Information) and contextual actions (for example, Set Nickname).
The game's `menus.fmf` explicitly says its menu XML is only an icon template:
the game generates the menu items in code. The action identifiers also appear
as compiled constants in `fm.exe`; the `ObLn` identifier occurs in a static
registration sequence. Merely adding XML did not insert a new item. The next
reverse-engineering milestone is to identify the menu construction and command
dispatch functions for this specific build. Until both are validated, the
earlier skin button beneath the portrait was the FM24 integration. The newer
portrait popup uses the standard skin menu widget instead of inserting into
this code-generated Overview menu.

Runtime notes for the tested executable: MSVC RTTI names `MENU_ITEM`,
`MENU_ITEM_BASIC`, and `MENU_ITEM_SOURCE` are present. The `MENU_ITEM_BASIC`
constructor at image VA `0x1410f12b0` (RVA `0x10f12b0`) receives a four-byte
item ID in `r8d`. A GDB trace while entering a player profile observed the
`nick` (Set Nickname) and `lcra` (leadership-course request) identifiers among
hundreds of constructed items. Opening an already-built Overview dropdown did
not call that constructor again; the items are prepared when the profile is
built. The `MENU_ITEM_SOURCE` constructor at RVA `0x10f1fe0` receives an array
of item pointers, but a trace did not yet establish which source owns the
Overview dropdown. These addresses are observations for this exact SHA-256,
not general hooks. They do not establish safe insertion or command dispatch.
