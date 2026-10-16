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
  match `fmface.py` in tests. Still to do: a live FM26 check, then one billed
  face.
- **Packaged FM24 companion:** done (2026-10-16). `fm24/build_exe.py` builds
  `dist/fmface-fm24/fmface-fm24.exe` (PyInstaller, one folder). Under Wine in
  FM24's prefix it read the squad, showed FM24's dialog and answered face
  checks. Skin building and DLL install still need Python once.
