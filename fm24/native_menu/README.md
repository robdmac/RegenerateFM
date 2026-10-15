# Experimental native FM24 portrait menu

FM24 can draw and handle the portrait menu itself. This integration adds a
transparent native `popup_button` over the player and staff portrait in the FM
Face skin, with **Generate new face** and **Update face** rows. FM24 posts the
popup's `select_event`, `FMFG`, for either row; the DLL reads which row was
chosen (`FMFG` or `FMFU`) from the event's `slct` property. The DLL also adds
**Generate missing squad faces…** as the last item of FM24's own Squad screen
**Overview** menu, below **Export Team for Versus Competition**; choosing it
emits `FMFS`. The DLL consumes these events and sends an authenticated message
to the local companion.
Opening or dismissing the menu does not request a portrait. Generation and
profile reading run in the Linux companion, which reads FM24's memory and has
no X11 code. Progress, the squad confirmation, results and errors appear
inside FM24 (see "In-game dialogs and progress" below), and the skin reload
runs inside FM24 (see below).

This is a prototype for **FM24 24.4.2 under Proton 9**, with the exact executable
listed in [supported_build.json](supported_build.json). The installer and the
DLL both verify its SHA-256. The DLL also checks the image metadata, original
handler bytes, and original vtable pointer before installing the hook. Other
executables remain unhooked, and the FM24 companion requires this DLL.

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

Start the companion after FM24 (`--native-menu` is still accepted, but the
native menu is the only mode). Keep screen IDs enabled in the title: the
companion checks the open screen's header ID. For the first test, include
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

- **Roster:** the Squad table attached to the live UI tree. Drawn rows have a
  player-name cell holding the person and display name. The table draws only
  visible rows, so the rest come from its row model: table+0x6e8 points to an
  object whose vector at +0x18 lists every row object (a header row with ID
  `HROW` first). Each row starts with a `{fourcc key, value}` property vector;
  its `id  ` value holds `0x01000000 | handle`, where the handle is the u32 at
  person+8. FM24 keeps person pointers in a heap table indexed by handle. Its
  base is found with one memory pass for a drawn player's pointer, and must
  hold every drawn player at their own handle. Each resolved row must be a
  validated player whose handle matches, the model must have FM24's row count,
  and every drawn row must be in it; otherwise it refuses. Undrawn rows use
  the person record's name (players whose record has no name string use
  FM24's separate first/surname record). If the row model cannot be read, the
  squad command refuses; nothing is scrolled.
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
confirmation, progress and result are FM24 dialogs (below).

## Profiles without screenshots

In native mode the companion reads the open profile from memory only
(`fm24/native_screen.py`). The UI manager's root widget (manager+0x58) is the
open screen; its widget tree is walked through each widget's children vector
(+0x100), keeping widgets of this manager and treating anything else as a leaf.
The header (`head`) must contain one subtitle (`subs`) whose text (+0x190)
ends in "(ID:n)", and n must be the person read from memory. The Biography
box's `person_bio_text` widget (`biot`, from `person/person history biography
no title.xml`) holds the rich-text biography at +0x188; its first paragraph,
with links reduced to their text, is the headline used in the prompt. The
memory profile and the screen are each read twice, and any difference refuses
the request. If memory cannot be read the request is refused with a message;
there is no screenshot or OCR reader.

## In-game dialogs and progress

FM24 opens its own "Quit game and return to start screen?" question by
building a property record (`titl`, `text`, `acte` = the event posted for Yes,
`noet` = the event ID posted for No, `widt`, `heig`) and passing it, with the
screen manager as owner and parent, to the dialog function at RVA `0x10bfcc0`
(a jump to `0xbdef100`). A gdb trace on 2026-10-15 confirmed the owner is the
same manager object the DLL's event hook receives, and that choosing No posts
`NOEX` to it. The DLL does the same on the UI thread's next IDLE event, with
its own `FMDY`/`FMDN` events, which the hook consumes.

- **Yes/No question:** the squad count confirmation ("Generate faces?").
- **OK message:** the same dialog, after which its `no  ` button is hidden
  (skin property `hidn`) and `yes ` is relabelled OK. FM24's OK/Cancel
  function (`0x10b6050`) showed an empty panel, and `dbtn` only chooses the
  default button, so neither gave an OK-only text dialog. Results, companion
  errors and the DLL's own notices (formerly Windows message boxes) use it.
- **Squad progress:** the same, with the one button labelled **Stop after this
  face**. Its text is updated in place by applying a `{text}` record to the
  dialog (`0x1906d70`, the call FM24 uses when creating it). The skin reload
  after each face destroys open dialogs without any event, so a finished
  reload marks the dialog closed and the companion opens a new one for the
  next face. At the end the dialog becomes the result message.
- **Portrait progress:** the text of FM Face's portrait popup (`fmnP`/`fmnS`)
  is set the same way: Generating…, Updating…, Loading…, Failed. The reload
  that loads the new face rebuilds the popup, which clears it.

Widgets are found afresh every time by a bounded search under the screen's
root widget (`manager + 0x58`), so the DLL never uses a widget pointer after
navigation or a reload. If FM24 cannot show a dialog or set progress, the
message goes to the companion terminal, and an unanswered squad confirmation
counts as Cancel. The companion's
commands use the authenticated loopback listener on bridge port + 1:
`FMFACE24D` (dialogs; replies queued, busy, shown, yes, no, closed, failed)
and `FMFACE24P` (progress text), with base64 UTF-8 text.

The event keeps its properties as a vector of `{fourcc key, value pointer}` at
offset 0; the `slct` value holds the chosen row's ID at +8 and must have the
same value type as the event's own `evid` (`FMFG`). A live capture on
2026-10-15 showed `slct`/`id  ` = `FMFG` for **Generate new face** and `FMFU`
for **Update face**. If the row cannot be read, nothing is sent and FM24 shows
a message. The bridge then sends `update` instead of `profile`.

When either row is chosen, the DLL reads the person ID from game
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
the open profile from game memory and
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
reports a failure, the face stays saved and the companion reports that a
manual Preferences > Reload Skin is needed.

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

## Performance

`build.py --perf` builds a DLL that times FM Face's own work on FM24's UI
thread (excluding the game's handler) and logs it to `fmface24.log` once a
minute as `FMFACE24_PERF`. A normal build contains none of this code.

Measured on 2026-10-15 with the tested save:

| Activity | Events/min | FM Face time/min | Worst single event |
|---|---|---|---|
| Idle on Home | 1,200–1,700 | about 0.35 ms | under 0.02 ms |
| Clicking through screens and popups | about 2,600 | about 0.6 ms | under 0.01 ms |
| A Generate or Update click | | | 1.1 ms (person ID and row read) |
| Squad command dialog | | | 4.4 ms (dialog shown once) |

FM24's own CPU use was about 13.5% of one core when idle and 33% while
clicking through screens. While the companion ran squad memory reads back to
back it was 16.8%, against 14.1% idle afterwards: the companion reads the
game's memory from outside and never pauses it. The companion uses no
measurable CPU when idle (29 MB of memory).

Each companion read scans FM24's writable memory once, about 4.3 s, of
which reading takes 0.8 s and Python's byte searches 2.9 s. A Generate reads
the profile twice (about 10 s before the image request) and the squad
command takes about 8.5 s.
