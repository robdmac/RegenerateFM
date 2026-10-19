# Experimental native FM24 portrait menu

FM24 can draw and handle the portrait menu itself. This integration adds a
transparent native `popup_button` over the player and staff portrait in the FM
Face skin, with **Generate new face** and **Update face** rows. FM24 posts the
popup's `select_event`, `FMFG`, for either row; the DLL reads which row was
chosen (`FMFG` or `FMFU`) from the event's `slct` property. The DLL also adds
**Generate missing squad faces…** as the last item of FM24's own Squad screen
**Overview** menu, below **Export Team for Versus Competition**; choosing it
emits `FMFS`. It adds **Generate new face** and **Update face** to a player
or staff profile's **Overview** tab menu, below **Follow**, and greys out
**Update face** in both profile menus when the person has no face (see
"Profile menus" below). The DLL consumes these events and sends an
authenticated message to the local companion.
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

Release ZIPs include a built DLL (`FM24/_internal/fm24-dll/winmm.dll`), so
this is only for building it yourself. Start with the normal FM24 companion
environment from [../README.md](../README.md). Install a MinGW-w64 compiler and the build-only
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

Close FM24, then install. `fm24/setup_fm24.py` (and the packaged companion) does this step for you; by hand:

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

**Starting the companion.** `--companion '<path>'` (the packaged
`fmface-fm24.exe`, as Windows sees it: `Z:\...` under Proton) adds a
`companion=` line; `fmface24.ini` is then written as UTF-16 with a byte-order
mark, which `GetPrivateProfileStringW` reads, so any path works. Once its
hooks are in, the DLL starts that program on a thread of its own with
`CreateProcessW` (`DETACHED_PROCESS`, so no window), passing
`--started-by-fm24 <FM24's process ID> --game-dir "<FM24's folder>"`, and logs
`FMFACE24_COMPANION_STARTED`, `_MISSING`, `_NOT_CONFIGURED` or
`_START_FAILED error=<n>`. The companion logs to `fmface-fm24.log` beside
itself, waits on FM24's process handle and exits with it, and exits at once
if another companion holds its port. With no key saved it runs itself again
with `--ask-key` in a new console window, so closing that window can't stop
it. Under Proton, Wine ends the companion with FM24's session anyway. The
skin is optional: with its panels missing the installer installs the DLL
alone, and panels that already carry the popup (built by the companion when
the setup had no `zstandard`) are adopted as created files. The macOS library
doesn't start a companion yet.

For Steam/Proton, use this game launch option:

```text
WINEDLLOVERRIDES="winmm=n,b" %command%
```

Start the companion after FM24 (`--native-menu` is still accepted, but the
native menu is the only mode). FM24's screen IDs setting isn't needed. For the
first test, include `--preview-only`:

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

The same item is added to a team's tab menu: a tab of FM24's tab-button class
(`profileMenu.tabButtonVtableRva` = `0x5679168`, the class of the profile's
Overview tab too) whose rows include the team's Squad view (`TeSq`) and no
`FMFS` row yet. These are the reserve and youth team tabs of the Development
Center (Inter Miami II, IMCF Academy) and another club's Players tab. When any
popup opens (`meop`, see "Profile menus"), the DLL appends an `FMFS` row to
each such tab under the same parent with FM24's `itemNew`; FM24 sends the
chosen row to the tab button and on to the screen handler, which queues the
squad command as for the Squad screen. Opening such a tab's menu also shows
the team's Overview, so the command reads the Overview's squad list there
(below). The rows' `tick` property is the ✓ of the view shown, and a tab keeps
the ID of the row last chosen from it at +0x500, but neither is needed.

The squad command then works from game memory (`fm24/native_squad.py`); it
opens no profiles and reads no screenshots:

- **List:** the Squad table's player-name cells are `Pnms`. A team Overview's
  squad list uses the same cell class named `PnmP`, two levels inside a row
  widget whose parent is the list; the list keeps its row count, visible rows
  and row model at the Squad table's offsets. A `Pnms` table is used when
  there is one, so another list on the screen can't make the first team's
  command ambiguous; otherwise the one `PnmP` list.
- **Brief records:** players FM24 lists as Scouting Required, without an age
  or ability, have person records whose ID matches but whose marker bytes
  12–15 aren't `00 xx 58 10/12/18` (`00 02 00 10` on Inter Miami II, `08 02 40
  30` on its academy). Such records have no birth date, name pointer or
  attributes at the usual offsets, so those rows are left out and counted in
  the confirmation; a list of only such players is refused.
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
The person is the object of the screen FM24 has navigated to, which the
sidebar (`side`, class vtable RVA 0x57E3B48) keeps at +0x280:
0x01000000 | the person's handle. FM24's database object (`*(fm.exe +
0x6426D90)`) keeps one vector per kind of object, each indexed by handle,
in slots of {begin, end, capacity, allocator}. Which slot holds people changes
between runs of FM24 (+0x920 in one, +0x9C0 in the next), so the DLL and the
companion try the last slot found and otherwise every slot in the object's
first 0x2000 bytes; the entry at the handle must be a person that keeps that
handle at +8, followed by its canonical record (the ID twice,
then a `00 xx 58` marker). The header (`head`) must contain one subtitle
(`subs`, text at +0x190), "Role - Club", which the profile checks against the
record; with screen IDs enabled it ends in "(ID:n)", and n must then be the
same person. The Biography
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
  that loads the new face rebuilds the popup, which clears it. With another
  skin there is no popup, so the companion opens an OK dialog ("Generating a
  new face…"), sets its text to "Loading the new face…", and on an error
  turns it into the error message (`finish`); the reload closes it. Progress
  and `finish` texts take up to 1,000 bytes, as dialog texts do.

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
walks up the widget parents until one has the sidebar (`side`, of the
sidebar's own class) as a child, reads the screen's object from it, and looks
the person up in FM24's person table, as the companion does. The layout
constants are in `fmface24.c` and are pinned to the validated executable. The
source and exactly one sidebar must be found, within bounded searches. All
reads use `ReadProcessMemory` on the
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
Theiler and a nation for Tyler Hall, and the header "(ID:n)" reader after it,
which needed screen IDs enabled. On 2026-10-18 the sidebar reader gave the
on-screen person for Tyler Hall, Ian Fray and Jorge Theiler in live FM24, with
screen IDs off and on, after a skin reload and after the Back button. The
sidebar's object is set when FM24 navigates and after a skin reload; another
widget's object field (the screen host's +0x880) was cleared by a reload, so
it is not used.

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

## Profile menus

FM24's popup buttons keep their rows as menu items. The popup's root item
(`popup + 0x4f8`, whose `id  ` is the popup's skin ID) holds the rows as a
vector of item pointers at `+0x38`; each item keeps its properties at `+8`,
like an event. When a menu opens, the popup posts `meop` to its parent
container, which reaches the screen handler the DLL hooks before FM24 draws
the rows. The DLL then:

- **Overview tab menu:** a person profile's **Overview** tab is a `Pprf`
  button of FM24's tab-menu class (a `popup_button` subclass; its vtable,
  `profileMenu.tabButtonVtableRva` = `0x5679168`, is checked). If its rows
  include FM24's person actions (`nick` Set Nickname, `SUBS` Follow or `USUB`
  Unfollow), the DLL appends two rows with FM24's own `itemNew`, once per tab
  widget: `FMFG` **Generate new face** and `FMFU` **Update face**, each with
  its ID as `id  ` and `evid`. FM24 sends a chosen row's properties as an
  event to the tab button; nothing handles it there, so it reaches the screen
  handler with the tab (`Pprf`) as its source and `slct` naming the row. The
  person ID is read from the header exactly as for the portrait popup,
  starting from the tab. The skin's `actions/person actions.xml` can't be
  used: FM24 loads it from `actions.fmf` and ignores a skin copy.
- **Update face greyed out:** for the portrait popup (`fmnP`/`fmnS`) and the
  Overview tab menu, the DLL reads the person ID and asks the companion
  `FMFACE24F 1 <serial> <person> <token>` on the bridge port; the reply is
  `FMFACE24F <serial> yes|no`. On `no` it sets the `FMFU` row's `disa`
  property, which FM24 draws greyed out and ignores clicks on; on `yes` it
  clears it. The UI thread waits at most 30 ms. Without an answer (no
  companion, an older companion, a failed check) the row stays enabled and
  the companion refuses an update without a face, as before. After an
  unanswered check the DLL stops waiting: Wine reports nothing for a closed
  loopback port, so without a companion every menu would otherwise pause
  30 ms. It still sends each check, and the first reply, even a late one,
  makes it wait again. The check uses an unconnected socket and accepts
  replies only from the bridge port: under Wine a connected UDP socket that
  once met the closed port (FM24 started before the companion) stopped
  waking `select` for every other reply. The companion
  answers from `has_existing_portrait`, which follows the same config.xml
  mappings as **Update face** without decoding any picture: parsed configs
  are cached until the file changes, and other graphics folders are walked
  at most every 30 seconds. FM24's bundled pictures take about half a second
  to index, so that runs in the background on the first check (or at
  companion start if FM24 is running), and until it finishes there is no
  answer. `fmface24.log` records each check
  (`FMFACE24_POPUP` or `FMFACE24_OVERVIEW_MENU`, the person, the answer, and
  the microseconds spent reading the ID and waiting for the answer). Measured
  on 2026-10-16: the ID read takes 1–2 ms and the answer about 0.5 ms (8 ms
  for the first check after FM24 starts), so a menu opens about 2–3 ms later
  than without FM Face. Without a companion the first menu pauses 30 ms and
  later ones about 0.2 ms; after the companion starts, one check goes
  unanswered and the rest are answered in about 0.3 ms.

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

Each full pass over FM24's writable memory takes 2–4 s, mostly Python's
byte searches. The companion now avoids repeat passes:

| Read | Before | Now |
|---|---|---|
| Generate (profile, biography and the check read) | about 10 s | 3.4 s, 2.0 s for the same person again |
| Update face | about 10 s | 2.1 s |
| Squad roster | about 8.5 s | about 4.2 s |

- The check read before a request uses the open screen's header and the
  person record found the first time, instead of two more memory passes.
- Person record addresses are remembered while the game runs. Each is used
  only while its 64-byte header is unchanged; otherwise it is searched again.
- The squad scan counts the top bar's date in its main pass instead of a
  second one.
- The Squad person table's address is remembered and checked against every
  drawn row before use.

## macOS

FM24 24.4.2 for Mac is a universal clang build with the hardened runtime. Its
entitlements allow `DYLD_INSERT_LIBRARIES` and libraries of other teams, so
`run_fmface24.sh` (FM24's Steam launch option) loads `libfmface24.dylib` into
the arm64 game without touching `fm.app`. `build_macos.py` builds it from
[fmface24_macos.c](fmface24_macos.c), the same logic as `fmface24.c` with
pthreads and BSD sockets. The library checks the arm64 slice's LC_UUID and
the original vtable pointers before hooking, and the installer checks the
executable's SHA-256, both pinned in
[supported_build_macos.json](supported_build_macos.json).

**Addresses.** Each address in `supported_build.json` was matched to the Mac
build. The vtable slots come from the RTTI both builds keep: the event handler
is `GAME_SCREEN_CLIENT` slot 6 on the Mac (slot 5 in fm.exe; Itanium vtables
have two destructors), the Squad provider `TEAM_EXPORT_ACTION_UI` slots 2 and
3, the tab button `MENUBAR_POPUP_BUTTON`. The other functions were found
through their callers: FM24's preferences-screen skin reload (`GChS` with
`SkNM`, `dlog`, `dela`, `chev`) gives the reload functions, its "Quit game"
query (`NOEX`, `noet`, `widt`, `heig`) the dialog functions, and the Squad
provider's own item the item and value functions. On the Mac, dialog
properties are a record with their own init, set and free functions; `acte`
is an event code, as the game itself passes it. The item constructor returns
the item through `x8`, and items are released through vtable slot 1. The
skin-change stage counter is at manager + 0x2aa.

**Widgets and menus.** Checked live: the widget part (+0x38), its manager,
parent and skin ID, the children vector (+0x100), the event source (+0x20),
the event's property vector and the popup root item (+0x4f8) are at the same
offsets as in fm.exe.

**Memory.** The companion can't read FM24's memory on macOS: the hardened
runtime without get-task-allow makes `task_for_pid` fail, even as root. The
library listens on loopback TCP at the bridge port + 2. After
`FMFACE24M 1 <token>` it answers three requests: the image header and UUID,
the read/write regions not backed by a file, and guarded reads
(`mach_vm_read_overwrite` on its own task), up to 16 MB each. It writes
nothing. `process_memory_macos.py` gives the companion the same functions as
on Linux; a full pass over 2.1 GB takes about 2 s.

**Person records.** The record that starts with the person's ID twice is the
same, 12 bytes into the person object, but clang lays the person classes out
differently from MSVC: the dates, name, attributes, positions, height,
nation, clubs and contract are at other offsets ([layout.py](../layout.py)),
and pointers are 8 bytes. The nation, club, club-details and contract objects
keep fm.exe's internal offsets. Byte 12 of the marker is 0 for most people
but 1, 2, 4, 5 or 6 for some, and byte 14 is 0x50 on some staff and reserve
records. Where fm.exe keeps zeros for staff in the position-rating bytes, the
Mac build has other data, so the person's kind comes from the object's
vtable: `db::ACTUAL_PLAYER` or `db::ACTUAL_NON_PLAYER`. Player-coaches
(`db::ACTUAL_PLAYER_AND_NON_PLAYER`) keep their player fields elsewhere and
are refused. The Squad table, its row model and the person handle table are
read at the same offsets as in fm.exe.

**Without screen IDs.** FM24 refers to the object a screen shows as
`type << 24 | index`, `0x01000000 | handle` for a person. The sidebar menu
(`side`, `SIDEBAR_MENU_TABLE`) keeps that reference at +0x278 for every
screen. The screen's `CLIENT_OBJECT_BROWSER` (an ancestor of every widget on
it) holds it at +0x878, but only until the first skin reload: after one,
FM24 builds a new browser that no longer tracks the screen, which broke
every face after the first in a session. So the library reads the sidebar
first and the browser only as a fallback, and sends `r<reference>` in the
person field of the click and the face check when the header shows no
"(ID:n)". It doesn't call FM24's resolver, which can load and register
objects. The companion looks the handle up in FM24's person table: the
global at image + 0x5304848 points to the game database, whose vector of
person pointers at +0x318 is indexed by handle (two reads, about 0.3 ms;
every record carries its handle 4 bytes before it, which is checked). If
that read fails it maps handles to people with one memory pass of about
10 s (`person_handles.py`) and checks the mapping again before each use; `native_screen.py` uses the same
reference for the open profile. Staff names then come from first/surname
records, which on the Mac can start at any byte. fm.exe (Windows and
Proton) works the same way at its own offsets: the sidebar's class is
`0x57e3b48` with the reference at +0x280, the browser (`0x56bae48`) holds it
at +0x880 until a reload, and the database global is at image + 0x6426D90
(its person vector's slot is searched, as above). There the DLL looks the person up itself and
sends the ID, so the companion needs no `r<reference>`.

**Key prompt.** On the Mac, FM24 asks for the OpenAI key itself, like the
FM26 plugin. When a face command finds no saved key (or one OpenAI rejected),
the companion sends `FMFACE24K 1 <serial> <token> <base64 text>` to the
bridge port + 1, and the library shows [key_prompt_macos.m](key_prompt_macos.m)'s
prompt as a sheet on FM24's window: a secure field (Cmd+V pastes, though
FM24 has no Edit menu), Save key, Cancel and Get a key…, which opens OpenAI's
key page. Polling the same serial answers `shown`, `key <base64>` or
`cancel`; the typed key stays in the library only until the next request.
The companion checks the key with OpenAI (free), asks again with the reason
if it fails, and saves it in `~/Library/Application Support/FM Face`.

Checked on 2026-10-17 on an Apple M2 (macOS 26) with a copy of a real save,
in preview mode: a player (Sly) and a staff member (Terry McPhillips) read
in full and matched their screens, the 25-row Squad table and its 8 players
without a face, the squad count dialog and No, the greyed Update face, and
an in-game skin reload. On 2026-10-18, with screen IDs turned off: player and
staff previews from the portrait popup and the Overview tab menu, and Update
face greyed for a person without a face. No billed face has been made on macOS
yet.
