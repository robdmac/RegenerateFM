# FM Face to-do

## Requested (2026-10-15)

1. **Performance measurement.** Done (2026-10-15). FM Face's own work on
   FM24's UI thread is about 0.35 ms a minute when idle, and at most 4.4 ms
   for a single event (showing a dialog). The companion uses no CPU when idle
   and its memory reads don't pause the game. Each read takes about 4.3 s,
   mostly in Python's byte searches; since made faster (see "In progress").
   Details are in `fm24/native_menu/README.md`.
2. **Remove the remaining overlays.** Done (2026-10-15). FM24's progress,
   confirmations, results and errors are FM24 dialogs; squad rows come from
   the table's row model and the biography from the displayed widget. All X11
   code (overlays, `--direct-clicks`, screenshots/OCR, scrolling, the
   Preferences reload fallback) and zenity have been removed; the companion
   requires the native DLL.
3. **Compatibility check for macOS and Windows.** Done (2026-10-15); see
   "Platform support" in the README. The FM24 companion now has a Windows
   memory backend, checked with Windows Python under Wine against the live
   game; macOS is refused (FM24 for Mac is a native program the DLL cannot
   hook). Still to do: a run on real Windows, and FM26 on macOS if wanted
   (per-platform build pins and the community Il2CppInterop patch).

## In progress

- In-game squad generation: done (memory roster from the row model, portrait
  index, prompts by ID; a billed two-face batch with Stop passed). Memory
  reads were made faster on 2026-10-16 (Generate about 10 s to 3.4 s, squad
  8.5 s to 4.2 s). The "All Players" view now works: reserve and youth
  players are read, and the few undrawn players whose names FM24 hasn't
  loaded are left out with a note.

## Later

- **FM24 Update face greyed out:** done (2026-10-16). The DLL asks the
  companion as the menu opens and sets the row's `disa` property.
- **FM24 Overview tab menu items:** done (2026-10-16). Generate new face and
  Update face are appended to player and staff Overview tab menus.
- **FM24 reserve teams:** done (2026-10-16). FM's added reserve and youth
  teams have club IDs above 2,000,000,000, which the club check refused;
  Federico Higuaín (staff) and Cole Jensen (player) at Inter Miami II now read.
- **FM24 reserve and youth squad lists:** done (2026-10-16). Generate
  missing squad faces is in each team tab menu of the Development Center and
  reads the team Overview's squad list; Inter Miami II previewed 11 faces.
  Players FM24 keeps only brief records for (Scouting Required, no age) are
  left out with a count; supporting them would need their record layout,
  which lacks the birth date and attributes a prompt uses.
- **FM24 menu face check:** done (2026-10-16). The check costs 1-3 ms; without
  a companion the DLL stops waiting after one 30 ms timeout.
- **FM26 Actions menu timing:** done (2026-10-16). The items are added as soon
  as the profile header exists and the ID is read on click.
- **Release build:** done (2026-10-16). Plugin version 0.8.0 was rebuilt into
  `hotreload/bin` and installed in FM26, and `dist/fmface-0.8.0.zip` was made.
  The unpacked zip passes all tests and holds no key or token. Not yet
  published anywhere.
- **Remote repo:** pushed to github.com/robdmac/FMFace.
- **FM26 without the Python server:** done in code (2026-10-16). Plugin 0.9.0
  calls OpenAI itself (`hotreload/FaceGenerator.cs`). It asks for the key in
  the game and keeps it DPAPI-encrypted in `%APPDATA%\FM Face`; the prompts, saved files and requests
  match `fmface.py` in tests. Checked live on 2026-10-17: a billed face
  (Taylor Arnett) saved and reloaded, and a typed key no longer triggers
  FM26's keyboard shortcuts.
- **Packaged FM24 companion:** done (2026-10-16). `fm24/build_exe.py` builds
  `dist/fmface-fm24/fmface-fm24.exe` (PyInstaller, one folder). Under Wine in
  FM24's prefix it read the squad, showed FM24's dialog and answered face
  checks. Skin building and DLL install still need Python once. Since
  2026-10-17 it asks for the key in its window (shared, encrypted key file
  with FM26) and rechecks a saved key at start; checked live against FM24.

## Next (2026-10-17)

- **FM24 setup without Python:** done (2026-10-17). `fmface-fm24.exe` sets
  FM24 up itself: it finds the game through Steam, builds the skin from
  FM24's own `panels.fmf` (no Resource Archiver) and installs the bundled DLL.
  Checked under Wine and against the live game (preview mode).
- **FM26 install without the .NET SDK or Python:** done (2026-10-17). The
  plugin no longer needs FM ActionLink, and the release ZIP's `FM26` folder
  (pinned BepInEx be.760 plus the plugin) is copied into the game folder.
  Checked live from a vanilla game folder. `install_fm26.py` was removed.
- **Real Windows check of the bundled FM24 DLL:** it forwards the WINMM
  exports of Wine's DLL, which it was built against; check FM24 starts with
  it on Windows.
- **FM24 popup progress text:** after the first use in a session the DLL logs
  `FMFACE24_PROGRESS_NOT_SET` and the popup shows no progress. The companion
  now shows progress in an FM Face dialog instead, as it does with skins
  other than FM Face's (2026-10-19). Cosmetic.
- **FM24 companion starts with FM24:** done (2026-10-19). The DLL starts the
  packaged `fmface-fm24.exe` (Windows, and inside the Proton prefix on Linux)
  and it stops with FM24; Linux installs with `FM24/install-linux.sh` and
  plain `python3`, refusing while Steam runs. Checked under Proton through
  Steam's Play; real Windows is untested.
- **macOS companion start:** the Mac library and `run_fmface24.sh` don't
  start the companion, and it isn't packaged for macOS (it needs building on
  a Mac). `run_fmface24.sh` could start it before FM24.
- **FM24 people found by class:** done (2026-10-20). Person records,
  teams, club details and nations are recognised by their object's class
  (MSVC RTTI), not by marker bytes or ID copies, which varied from person to
  person; every one of the 29,330 players and staff in a Reading save reads
  (see native_menu/README.md, "Person records"). Mac players-coaches are
  still refused (their class isn't mapped there).
- **FM24 squad scan speed:** measured 2026-10-20 on the Inter Miami test
  save under Proton: 8 s from the menu click to the confirmation on the first
  team, 6.6 s on Inter Miami II. Of the companion's 5.3 s, 5.0 s is the one
  full pass over FM24's memory (`_scan`); the person-table pass (1.4 s) runs
  only on the first scan of a session, and the rest is under 0.3 s. Natively
  on Linux the pass takes 3.3 s. The pass does three jobs; to remove it, all
  three need a direct route:
  - **Name cells:** done in a test script, not in FM Face. Walk the widget
    tree from the UI manager's root at `manager+0x58`, following only
    children whose parent link points back. It found the same cells as the
    pass in 0.01 s, after visiting 737 widgets.
  - **Today's date:** the top-bar widget `dtam` (vtable RVA 0x5824808)
    points at the "3 Jan 2023" string at +0x188, so the tree walk can read
    it. Check it against the year in the player records. The only value
    elsewhere that moved by exactly one game day is in `TRANSFER_MANAGER`
    (+0x7dc), whose meaning is unknown.
  - **Portrait index:** don't cache it. FM24 keeps it as 32-byte nodes
    ({key string, u32 hash, 'NULL', value, 0}) in 8192-node pools. A skin
    reload with no new files left every node in place, but a new portrait's
    node landed outside the pools seen before, so cached pools would miss
    it. The hash isn't CRC32, CRC32C, FNV, djb2, sdbm, Murmur3 or Jenkins,
    and no bucket array showed up. Next: find the map's owner from FM24's
    code that looks up `graphics/pictures/person/...` (the code that reads
    the record's portrait key).
- **FM24 squad scan counts a removed face as present:** after a test portrait
  was added, reloaded, removed and reloaded again (2026-10-20, Bryan Destin),
  FM24 drew a silhouette, but the pass still found the old key string in
  freed memory, so the squad command would skip him until FM24 restarts.
  Reading the live index (above) fixes it too. It only happens after a face
  is removed during a session.
- **FM24 squad dialog names the club, not the team:** a youth squad's
  confirmation says "20 of 21 Reading players"; FM doesn't store "Reading
  Under 21s", so it would come from the screen's header.
- **FM24 portrait popup in other skins:** FM Face works with any skin through
  the Overview tab menu (checked on the default skin, 2026-10-19). The setup
  could also add the portrait popup to the skin in use when its panels keep
  FM24's `ppdp`/`ppic` widgets; untested with third-party skins.
- **FM26 reserve and youth squads:** the Generate faces… item depends on the
  Squad screen's player table; check it on the Under 21s and Under 18s tabs.
- **FM26 biography:** needs a visit to the Career tab first; FM24 reads it
  from the Overview.
- **Real Windows test** of both games. `dist/fmface-0.9.0.zip` is built
  but not published.
- **Credits system:** deferred.

## macOS (2026-10-17)

- **FM26 on macOS:** loads (2026-10-17). BepInEx be.789 under Rosetta 2 with
  funchook and a patched Il2CppInterop, started by `run_fmface.sh`; the
  plugin loaded in 12 cold starts, including a quarantined install from the
  release ZIP. Details in `hotreload/macos/README.md`. The Actions, portrait
  and squad commands open the key dialog. Still to check: a saved key and a
  billed face in game; starting through Steam's launch option; whether one
  plugin DLL serves be.760 (Windows) and be.789 (macOS).
- **FM26 person ID from the data binding:** done (2026-10-17). The plugin
  reads `objectID` (`Person.UniqueId`) from the profile header's binding, so
  "Show screen IDs in Title Bar" isn't needed; `(ID:n)` text is the fallback.
  Check it on Linux and on a staff profile.
- **FM26 squad command closes with the dropdown:** FM26 closes the Squad
  dropdown once the pointer leaves it, and the plugin removes **Generate
  faces…** at its next 0.5 s scan, so a slow click misses it (seen on macOS;
  same code on every platform).
- **FM24 on macOS:** works with preview faces (2026-10-17), on FM24 24.4.2's
  arm64 build with a copy of a real save. `libfmface24.dylib`
  (`fm24/native_menu/fmface24_macos.c`) adds the portrait popup, the
  Overview tab rows and the Squad menu item, shows FM24's dialogs, reloads
  the skin and serves the companion's memory reads; `setup_fm24.py`
  installs it with `run_fmface24.sh`. Every Windows address was matched to
  the Mac build (`supported_build_macos.json`); the companion's record
  offsets are in `fm24/layout.py`. Checked: a player and a staff preview,
  the squad count and its No, Update face greyed for a person without a
  face, and the reload. Still to check: a billed face and a billed squad
  batch, Update face on a person with a face, Steam's launch option, the
  Development Center tabs, and `setup_fm24.py --remove`. Player-coaches
  (db::ACTUAL_PLAYER_AND_NON_PLAYER) are refused. There is no packaged Mac
  companion; it runs from source with Pillow and zstandard.
- **FM24 key prompt in the game:** done on macOS (2026-10-18). With no saved
  key, a face command shows a key prompt on FM24's window
  (`key_prompt_macos.m`), checked live with a fake key: paste, OpenAI's
  rejection and the second prompt, Cancel. A real key and a billed face are
  still to check. Windows and Linux ask in a Win32 dialog the companion
  opens above FM24 (`key_dialog.py`, takes a paste under Proton; 2026-10-19);
  `fmface24.c` could show it on FM24's window instead.
- **FM26 face not appearing on macOS:** fixed (2026-10-18). A billed face
  (Lucas Hoyos) was saved, but the graphics reload failed with FM26's
  "invalid operation handle" and the popup waited 30 s before giving up:
  Il2CppInterop's hook on EnumerateLocalAssetsComplete passes its struct
  argument the Windows way. On macOS the plugin now follows
  RegisterResourceLocator instead (see hotreload/macos/README.md). Checked
  through Steam's launch option with a stand-in image API: Generate and
  Update face both shown, reload under 1 s. The plugin also logs how long
  the image request took, to tell OpenAI's time from FM26's.
- **FM26 staff, squad batch and key paste on macOS:** fixed (2026-10-18),
  each checked in game through Steam's launch option with a stand-in image
  API. Staff names and jobs are read from the header boxes when screen IDs
  are off (Tyler Ruthven: "job title Scout"); an unreadable staff header is
  logged. Generate faces… sits inside the Squad dropdown, so the menu no
  longer closes on the way to it; batches of 7 and 8 faces finished. The
  batch waits up to 30 s for a profile and pauses while FM26 is in the
  background (macOS throttles it). Cmd+V doesn't reach FM26 on macOS, so
  the key prompt has a Paste button. Still to do: the dropdown stays open
  after choosing Generate faces…; the FM24 in-game checks with
  `OPENAI_BASE_URL` (a stand-in image API for testing, new in fmface.py).
- **Steam launch options:** `steam_launch.py fm24|fm26 [--remove]` sets
  FM Face's launch option in Steam's localconfig.vdf (Steam closed, backup
  kept); FM24's setup uses it on macOS and Linux. Applied on this Mac.
- **FM24 person after a skin reload:** fixed (2026-10-18). After the first
  face's reload the object browser stopped tracking the screen, and the
  next click said "FM24 did not report which person's menu was chosen". The
  person now comes from the sidebar menu's reference, which survives the
  reload. Checked with a scripted run in preview mode: Sly, Sly after a
  reload, Paul Molesworth and John Marshall (staff, after the reload) from
  the portrait popup, and John from the Overview tab menu.
- **FM24 click delay without screen IDs:** fixed (2026-10-18). Clicks took
  5-10 s before the key prompt or "Generating…" while the companion indexed
  people with a memory pass. It now reads FM24's person table by handle
  (0.3 ms), asks for the key before the lookup, and a click waits for a
  pass already running instead of starting a second. fm.exe's table is
  mapped too, so Windows and Proton read it the same way.
- **FM24 without screen IDs:** done on macOS and for fm.exe (2026-10-18).
  The person comes from the sidebar's object reference, looked up by handle
  in FM24's person table (`fm24/layout.py`). Checked live on Proton with the
  setting off and on; not yet on Windows itself.

## macOS review (2026-10-21)

- **FM24 person survey on the Mac:** done. With the Mac build's class
  vtables, name objects, and team, club and nation classes mapped (from
  Itanium RTTI), every one of 31,032 players and staff in an MK Dons save
  reads; before, 13,958 did (most people without a full-name string, and
  every player-coach, were refused). Details in
  `fm24/native_menu/README.md` ("Person records").
- **FM24 ages from the in-game date:** done. A record's year can be the
  season before (738 people in that save), which refused a 16-year-old's
  profile and stopped the Under 18s squad scan. Ages now come from the
  date in the top bar, on every platform. Not yet checked on Windows.
- **FM24 Steam launch on the Mac:** fixed. `run_fmface24.sh` checked
  `uname -m`, which says x86_64 under Steam's Rosetta, so FM24 started
  without FM Face. The launcher now also starts the companion.
- **FM24 Mac squad batch:** the row model offset differs (+0x6C8); the
  final dialog now ends on OK as on Windows; the profile read takes 0.6 s
  instead of about 6 s, and the squad scan uses the people vector instead
  of a memory search.
- **FM24 another club's Players tab:** fixed. Its rows are the club's teams,
  All Players (`Capl`) and Export, with no Squad view (`TeSq`) in this save,
  so the item wasn't added (on Windows too: the same rule). A tab with All
  Players now gets it; checked on the Mac with Huddersfield (6 of 35). The
  Windows DLL has the same change but hasn't been rebuilt or checked.
