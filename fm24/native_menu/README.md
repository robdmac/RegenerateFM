# Experimental native FM24 portrait menu

FM24 can draw and handle the portrait menu itself. This integration adds a
transparent native `popup_button` over the player and staff portrait in the FM
Face skin. Selecting **Generate new face** emits `FMFG`. The DLL also adds
**Generate missing squad faces…** as the last item of FM24's own Squad screen
**Overview** menu, below **Export Team for Versus Competition**; choosing it
emits `FMFS`. The DLL consumes these events and sends an authenticated message
to the local companion.
Opening or dismissing the menu does not request a portrait. No browser or X11
menu window is involved. Generation, profile reading and progress still use
the existing Linux companion; its progress window and the squad scan still
depend on X11. The automatic skin reload runs inside FM24 (see below).

This is a prototype for **FM24 24.4.2 under Proton 9**, with the exact executable
listed in [supported_build.json](supported_build.json). The installer and the
DLL both verify its SHA-256. The DLL also checks the image metadata, original
handler bytes, and original vtable pointer before installing the hook. Other
executables remain unhooked. This is an optional integration and does not replace
the default companion overlay installation.

## Build and install

Start with the normal FM24 companion environment and FM Face skin from
[../README.md](../README.md). Install a MinGW-w64 compiler and the build-only
Python dependency:

```sh
.venv-fm24/bin/pip install -r fm24/native_menu/requirements.txt
.venv-fm24/bin/python fm24/native_menu/build.py \
  --system-winmm '/path/to/Proton/prefix/drive_c/windows/system32/winmm.dll'
```

Use `--cc` if the MinGW-w64 compiler has a different path or name. The builder
reads the user's system DLL export names and ordinals, then generates original
forwarding stubs. It does not copy system or game code into the plugin. In the
tested prefix, all 189 WINMM exports were forwarded. DLL resolution and hook
initialization happen on the first exported API call, outside `DllMain`.

Close FM24, then install:

```sh
.venv-fm24/bin/python fm24/native_menu/install.py \
  --game-dir '/path/to/Football Manager 2024' \
  --skin-dir '/path/to/user-data/Football Manager 2024/skins/fmface' \
  --auth-file .fm24-auth
```

This installs `winmm.dll`, a private `fmface24.ini`, and an installation manifest
beside `fm.exe`. It backs up both FM Face panels before adding the native popup.
The Squad menu item needs no skin change. Running the installer again over an
existing installation upgrades it in place; an earlier installation's Squad
screen **Faces** button is removed (its panel copy deleted, or your own squad
panel restored from its backup). `--squad-panel-source` is still accepted and
ignored. It refuses to
overwrite an unknown DLL or panels edited since installation. Every write is
journaled: if any step fails, all files are restored to their previous state.
No installation paths are compiled into the DLL. Configuration and logging
paths are derived from the DLL's own location; the network destination is always
loopback. `--port` changes the native bridge port, default 8769.

For Steam/Proton, use this game launch option:

```text
WINEDLLOVERRIDES="winmm=n,b" %command%
```

Start the companion after FM24; the native menu is its default mode
(`--native-menu` is still accepted, and `--direct-clicks` selects the older X11
menus). It finds the FM24 game window itself; pass `--window-id` only if it
reports none or several. Use the tested 1854×1011 window and keep screen IDs
enabled for the profile/reload checks. For the first test, include
`--preview-only`:

```sh
.venv-fm24/bin/python fm24/companion.py \
  --auth-file .fm24-auth \
  --graphics-dir '/path/to/Football Manager 2024/graphics/fmface' \
  --preview-only
```

Click a portrait, then **Generate new face**. Preview mode prints the identified
person and prompt without an API request or graphics write. Remove
`--preview-only` to enable billed generation. A custom bridge port also needs
`--native-port` on the companion.

On the Squad screen, open the **Overview** menu and choose **Generate missing
squad faces…**. FM24 builds that menu from provider objects; the DLL replaces
two validated vtable entries of the provider that adds **Export Team for Versus
Competition**. Its item-adding method calls the original and then appends the
FM Face item through the same game functions, and its command handler queues
`FMFS` and passes every other command to the original.

The squad command then works from game memory (`fm24/native_squad.py`); it
opens no profiles and reads no screenshots:

- **Roster:** the rows of the Squad table attached to the live UI tree. Each
  player-name cell holds its person record and display name. The table only
  draws visible rows, so the companion scrolls it (X11 wheel input) until
  FM24's own row count says every row has been read; otherwise it refuses.
- **Portrait state:** FM24's graphics index, read in the same pass. A player
  is missing when no `graphics/pictures/person/<id>/portrait` key is indexed.
  The index keeps a key after its FM Face mapping is deleted, so such a
  player is treated as having a portrait (no request is made).
- **Prompts:** each missing player's record (birth date, nationality,
  position ratings, height and attributes), the row's display name, the
  Squad's club, and the in-game date. Every prompt is built and validated
  before the first request, and read again just before each request.

After the count confirmation, each face is generated, saved and reloaded in
game in turn. On the test save this took about 30 seconds to read all 29
rows, where the profile-by-profile X11 scan took 10 to 20 minutes. The
confirmation and progress dialogs are still separate windows.

When **Generate new face** is chosen, the DLL reads the person ID from game
memory inside FM24's menu callback, on the game's UI thread, before returning
to the game. FM24 cannot navigate while that callback runs. It starts from the
event's source widget, which must be FM Face's `fmnP` (player) or `fmnS`
(staff) menu button belonging to the UI manager the callback receives. It
walks up the widget parents to the screen's `head` header, finds the header's
`subs` subtitle widget, and reads its text. With screen IDs enabled, that text
ends in "(ID:n)". The layout constants are in `fmface24.c` and are pinned to
the validated executable. The source and exactly one header and subtitle must
be found, within bounded searches. All reads use `ReadProcessMemory` on the
game's own process, so a stale pointer cannot crash FM24. If anything does not
match, the command carries ID 0 and the companion refuses it. `fmface24.log`
records the ID and the read time for each click.

The request is bound to that ID. Before any image request, the companion reads
the open profile (from game memory, with the existing screen fallback) and
refuses unless it is still that person ("The open FM24 profile is now person
X, not the requested person Y"). No screenshot is taken to bind the click. The
command also carries its click time, and one that arrives more than 0.75 s
late is refused as an extra check.

This replaces protocol 3's first drawing-stack entry, which was a team for
Theiler and a nation for Tyler Hall. A Python model of the header reader
matched the on-screen ID on 7 players (including Tyler Hall and Franco Negri)
and 5 staff (including Theiler) in live FM24.

After saving a portrait, the companion asks the DLL to reload the skin on the
loopback port after the bridge port (8770 by default), using the same token.
On FM24's UI thread, at its next `IDLE` event, the DLL repeats the validated
build's own "reload current skin" command: a queued `GChS` event naming the
current skin. FM24 then runs its staged skin change; the DLL reports completion
when the last `chsk` stage returns the stage counter to its resting value. No
Preferences screen, mouse input, screenshot or X11 is involved, and the open
screen stays in place. The function addresses are pinned in
[supported_build.json](supported_build.json). If the DLL does not answer or
reports a failure, the companion falls back to the X11 Preferences reload.

If the companion is not running, the DLL retries for about two seconds and then
shows a message; the next click tries again, so the companion can be started or
restarted while FM24 is running. Failures also appear in a dialog, not only in
the companion terminal. `fmface24.log` beside `fm.exe` records timestamped
bridge events.

To uninstall, close FM24 and run the installer with the same game and skin paths
and `--remove`; then remove the Steam DLL override. The original FM Face panels
are restored. The installer only removes the DLL whose checksum it recorded.

## Validated route

The skin's native popup supports an XML `list` of menu items and a `select_event`.
An in-game test showed that `select_event="FMFG"` reaches the game-screen event
handler with `edx=0x464d4647`. The selected row is handled by the game; the native
hook neither records mouse clicks nor guesses screen coordinates.

For this exact executable, the game-screen vtable slot at RVA `0x5600760`
contains handler RVA `0x1f21c0`. That handler calls the toolkit base handler at
RVA `0x1145600`. Hooking only the base handler's vtable would miss this direct
call, so the plugin atomically replaces the validated game-screen pointer. All
events other than `FMFG` and the reserved `FMFS` squad event call the original
handler. No executable instructions are patched or moved. The UI callback reads
the header ID and signals a worker; the worker communicates with the companion. The bridge checks
the private token, protocol version, message type, sequence, click time,
clicked person, and loopback sender. It rejects replay, late, and busy
selections, and reuses the companion's nonce and generation lock. A DLL from
before protocol version 5 is ignored; reinstall after updating the companion.

Protocol 5 live previews matched all eight player and five staff selections
tried, including repeats and the previously refused Theiler/Tyler Hall profiles.
An independent retest on 2026-10-15 verified those two profiles and Theiler's
headline biography again. The original Farías-to-Ian Fray case was refused
after a 0.533-second pause with the normal 0.75-second cutoff, retaining the
clicked ID and naming the different current ID; no cutoff override was needed.

The independent retest also exercised the normal save/reload path using a
temporary image-provider fixture returning existing local PNGs. Visibly
different portraits appeared automatically for a player and staff member;
player regeneration replaced the first version and cleaned up its file. A
simulated provider error preserved the current portrait/config and later
requests still worked. All original graphics were restored, with restored
portrait pixels matching their original screenshots. No image API was called.
This validates visible reload and regeneration, not actual provider generation
or authentication. The native squad entry and Cancel were also checked; a
complete missing-face batch and stop-after-current-face remain unverified by
that retest. See [the release checklist](../../RELEASE_CHECKLIST.md) for evidence
and remaining checks. Other skins, resolutions, builds, and staff roles need
separate validation before making this the default release integration.
