# FM24 research probes

These are historical feasibility probes kept for reference. They are not part
of the release archive and are not needed to run FM Face.

- `build_probe_skin.py` and `probe_server.py`: the first skin-button probe,
  which opened a loopback URL from FM24 and identified the visible player by OCR.
- `native_probe/`: the WINMM proxy probe used to discover the native skin
  event route that `../native_menu/` now uses.

For a supported process-memory access check, use `../memory_probe.py`.
