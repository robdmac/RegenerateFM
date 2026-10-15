# FM Face to-do

## Requested (2026-10-15)

1. **Performance measurement.** Check FM Face does not slow the game
   unnecessarily: the DLL's work on FM24's UI thread (event hook, IDLE
   reload check, Squad menu provider), and the companion's memory scans
   (about 1.5 s per 2 GB pass; the squad reader makes two or three).
2. **Remove the remaining overlays where possible.** The X11 progress
   indicator over the portrait, the squad progress panel, and zenity
   dialogs. The squad reader also still scrolls the table with X11 input
   to draw rows it has not drawn yet.
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
- Repo name and git setup.
