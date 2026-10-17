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
  `FMFACE24_PROGRESS_NOT_SET` and the popup shows no progress. Cosmetic.
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
- **FM24 on macOS:** not started. FM24 for Mac is a native clang build, so the
  DLL hook, memory layouts and memory reader would all have to be redone.
