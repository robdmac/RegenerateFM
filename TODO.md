# FM Face to-do

## Requested (2026-10-15)

1. **Performance measurement.** Check FM Face does not slow the game
   unnecessarily: the DLL's work on FM24's UI thread (event hook, IDLE
   reload check, Squad menu provider), and the companion's memory scans
   (about 1.5 s per 2 GB pass; the squad reader makes two or three).
2. **Remove the remaining overlays.** Done (2026-10-15). FM24's progress,
   confirmations, results and errors are FM24 dialogs; squad rows come from
   the table's row model and the biography from the displayed widget. All X11
   code (overlays, `--direct-clicks`, screenshots/OCR, scrolling, the
   Preferences reload fallback) and zenity have been removed; the companion
   requires the native DLL.
3. **Compatibility check for macOS and Windows.** The FM24 path is
   Linux/Proton-specific (process memory via /proc, the WINMM proxy
   DLL loaded through a Proton override); FM26 uses BepInEx. Work out what
   runs, what needs porting, and what must be refused on each platform.

## In progress

- In-game squad generation: done (memory roster from the row model, portrait
  index, prompts by ID; a billed two-face batch with Stop passed). Remaining:
  speed up the memory passes (the portrait index and date scan take about
  10 s), and the "All Players" view, which has a row the reader refuses.

## Later

- Billed check of **Update face** in FM24 and FM26: likeness kept, age and kit
  updated. In FM24, grey the row out when there is no face (the skin popup has
  no per-row enabled state; the DLL would have to change it when the popup opens).
- Repo name and git setup.
