# FM24's native library

RegenerateFM works inside FM24 through a small library it loads: `winmm.dll`
on Windows and Proton ([regeneratefm24.c](regeneratefm24.c)), and
`libregeneratefm24.dylib` on the Mac ([regeneratefm24_macos.c](regeneratefm24_macos.c),
the same logic with pthreads and BSD sockets). It adds RegenerateFM's commands
to FM24's own menus and shows RegenerateFM's progress and messages in FM24's
own dialogs. The companion ([src/RegenerateFM.Desktop](../../src/RegenerateFM.Desktop/FM24/README.md))
does the rest: it reads the person from FM24's memory, makes the face and
saves it.

The library is pinned to FM24 24.4.2. The setup checks the executable's SHA-256
([supported_build.json](supported_build.json),
[supported_build_macos.json](supported_build_macos.json)), and the library
checks the image (or the Mac slice's LC_UUID) and the original vtable pointers
before hooking anything. Any other build is left unhooked.

## What it adds

- **Portrait popup:** in the RegenerateFM skin, a transparent `popup_button`
  over a player's or staff member's portrait, with **Generate new face** and
  **Update face**.
- **Overview tab menu:** the same two commands below **Follow** in a profile's
  **Overview** tab menu, with any skin.
- **Squad menu:** **Generate missing squad faces…** at the end of the Squad
  screen's **Overview** menu, and in the reserve and youth team tabs of the
  Development Center and another club's Players tab.
- **Staff menu:** **Generate missing staff faces…** at the end of the Staff
  screen's **Overview** menu and another club's **Staff** menu (any tab menu
  with the staff list's **All** row, `tcsp`).
- **Greyed Update face:** when a menu opens, the library asks the companion
  whether the person has a face, and greys out **Update face** if not. It
  waits at most 30 ms, so a menu opens about 2–3 ms later than without
  RegenerateFM.
- **In-game dialogs:** the squad confirmation, progress (with **Stop after
  this face**), results and errors use FM24's own dialog, built the way FM24
  builds its "Quit game?" question. When a batch can include the club's
  other people, the confirmation is a choice of three: its Yes and No are
  relabelled with the two answers (No posts its own event) and the dialog's
  hidden Cancel button is shown as No. FM24 closes the dialog on Cancel
  without an event, so a choice that disappears counts as No. Progress for a single face is shown on the
  portrait popup, or in a dialog with other skins.
- **Skin reload:** after a face is saved, the library runs FM24's own "reload
  current skin" command on the UI thread, so the new face appears without
  leaving the screen. If that fails, the face stays saved and the companion
  says to restart FM24 to see it.

## How it works

- **Hooks.** The library replaces a few validated vtable entries: the game
  screen's event handler and the Squad screen's menu provider. It adds the
  profile and team-tab rows with FM24's own item functions when a menu opens.
  No game code is patched or copied. Every event that isn't RegenerateFM's
  goes to FM24's original handler.
- **Who was clicked.** Inside FM24's menu callback, on its UI thread, the
  library reads the person on screen from the sidebar widget, which holds the
  screen's object (`0x01000000 | handle`), and looks the handle up in FM24's
  person table. All reads go through `ReadProcessMemory` (or guarded Mach
  reads), so a stale pointer can't crash FM24. The command carries that ID and
  its click time, and the companion refuses it if the open profile is no
  longer that person or the command is more than 0.75 s old.
- **Bridge.** Everything runs over loopback UDP, authenticated with the
  private token in `regeneratefm24.ini` beside the game:
  - port 8769, the companion: clicks and squad commands, and face checks
    (`REGENERATEFM24F`);
  - port 8770, the library: dialogs (`REGENERATEFM24D`), progress
    (`REGENERATEFM24P`), the Mac key prompt (`REGENERATEFM24K`) and skin
    reloads;
  - on the Mac, port 8771 (TCP): memory reads for the companion (below).

  The companion checks the token, protocol version, sequence, click time and
  sender, and refuses replays, late clicks and a second request while one is
  running.
- **Starting the companion.** The setup records the companion's path
  (`companion=`) in `regeneratefm24.ini`, which is UTF-16 with a byte-order
  mark on Windows so `GetPrivateProfileStringW` reads any path. The DLL starts
  it with no window, passing FM24's process ID; on the Mac,
  `run_regeneratefm24.sh` starts it. The companion stops when FM24 does.
- **Logging.** `regeneratefm24.log` beside the game records bridge events,
  each click's person and read time, and each face check.

Two things that look simpler but don't work:

- The screen's object browser also holds the open person, but FM24 replaces
  it on every skin reload, so only the sidebar is used.
- Under Wine, a connected UDP socket that once met the closed companion port
  stops waking `select` for every later reply, so face checks use an
  unconnected socket.

## The Mac

FM24 for Mac allows `DYLD_INSERT_LIBRARIES`, so `run_regeneratefm24.sh`
(FM24's Steam launch option) loads the library into the arm64 game without
changing `fm.app`. Its addresses were matched to fm.exe's through the RTTI both
builds keep, and its person layout differs (clang lays the classes out
differently); both are in the companion's
[Layout.cs](../../src/RegenerateFM.Desktop/FM24/Layout.cs).

The hardened runtime stops the companion from reading FM24's memory itself, so
the library serves read-only memory requests on loopback TCP (port 8771) for
the token's holder. When a face command needs a key, the library shows
[key_prompt_macos.m](key_prompt_macos.m)'s prompt as a sheet on FM24's window,
as the FM26 plugin does.

## Performance

RegenerateFM adds under 1 ms per minute to FM24's UI thread, and the companion
never pauses the game; it uses no noticeable CPU when idle. Reading a profile
from memory takes about 0.3 s and a 29-player squad about 0.9 s, so a face is
mostly the wait for the image request. `build.py --perf` builds a DLL that logs
RegenerateFM's own time on FM24's UI thread.

## Building

The release's companion carries the built library, so this is only for
building it yourself. On Linux, install a MinGW-w64 compiler and the build
script's one Python dependency, then build into `build/`, where the
companion's build picks it up:

```sh
python3 -m venv .venv-native
.venv-native/bin/pip install -r native/fm24/requirements.txt
.venv-native/bin/python native/fm24/build.py \
  --system-winmm '/path/to/Proton/prefix/drive_c/windows/system32/winmm.dll'
```

The builder reads the system `winmm.dll`'s exports and generates forwarding
stubs for them (`--cc` names another compiler). On a Mac,
`python3 native/fm24/build_macos.py` builds the library into `build-macos/`.

The companion's `setup` installs the library, its settings and the skin
panels; see [src/RegenerateFM.Desktop/FM24/README.md](../../src/RegenerateFM.Desktop/FM24/README.md).
Every write is journaled and rolled back if a step fails, and `setup --remove`
restores the panels and removes only the library whose checksum it recorded.
