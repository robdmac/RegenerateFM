# FM Face to-do

## Requested (2026-10-15)

1. **Performance measurement.** Check FM Face does not slow the game
   unnecessarily: the DLL's work on FM24's UI thread (event hook, IDLE
   reload check, Squad menu provider), and the companion's memory scans
   (about 1.5 s per 2 GB pass; the squad reader makes two or three).
2. **Remove the remaining overlays where possible.** Done in native mode
   (2026-10-15): portrait progress, the squad confirmation, squad progress
   with Stop, results and errors are FM24 dialogs. Since the same day the
   native mode uses no X11 at all in its normal path: Squad rows come from the
   table's row model, the biography from the displayed widget, and no window
   is looked up or activated. The X11 progress window, table scrolling and
   zenity remain only as fallbacks, and for `--direct-clicks`.
3. **Compatibility check for macOS and Windows.** The FM24 path is
   Linux/Proton-specific (process memory via /proc, X11, the WINMM proxy
   DLL loaded through a Proton override); FM26 uses BepInEx. Work out what
   runs, what needs porting, and what must be refused on each platform.

## In progress

- In-game squad generation: done (memory roster from the row model, portrait
  index, prompts by ID; a billed two-face batch with Stop passed). Remaining:
  speed up the memory passes (the portrait index and date scan take about
  10 s), and the "All Players" view, which has a row the reader refuses.

## Later

- Billed end-to-end run of the native path.
- Billed check of **Update face** in FM24 and FM26: likeness kept, age and kit
  updated. In FM24, grey the row out when there is no face (the skin popup has
  no per-row enabled state; the DLL would have to change it when the popup opens).
- Repo name and git setup.
