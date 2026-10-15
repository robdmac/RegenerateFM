# FM Face to-do

## Requested (2026-10-15)

1. **Performance measurement.** Check FM Face does not slow the game
   unnecessarily: the DLL's work on FM24's UI thread (event hook, IDLE
   reload check, Squad menu provider), and the companion's memory scans
   (about 1.5 s per 2 GB pass; the squad reader makes two or three).
2. **Remove the remaining overlays where possible.** Done in native mode
   (2026-10-15): portrait progress, the squad confirmation, squad progress
   with Stop, results and errors are FM24 dialogs. Left: the squad reader
   still scrolls the table with X11 input; the X11 progress window and zenity
   remain as fallbacks and for `--direct-clicks`.
3. **Compatibility check for macOS and Windows.** The FM24 path is
   Linux/Proton-specific (process memory via /proc, X11, the WINMM proxy
   DLL loaded through a Proton override); FM26 uses BepInEx. Work out what
   runs, what needs porting, and what must be refused on each platform.

## In progress

- In-game squad generation: done in preview (memory roster, portrait index,
  prompts by ID, no profile navigation). Remaining: drop the X11 scroll
  (needs the table's row model or an in-game scroll), speed up repeat
  memory passes, and a billed run.

## Later

- Billed end-to-end run of the native path.
- Billed check of **Update face** in FM24 and FM26: likeness kept, age and kit
  updated. In FM24, grey the row out when there is no face (the skin popup has
  no per-row enabled state; the DLL would have to change it when the popup opens).
- Repo name and git setup.
