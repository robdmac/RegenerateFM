"""FM24 squad command: read the roster from game memory, confirm in FM24,
then generate the missing faces one at a time.

Everything is shown in FM24's own dialogs through the native DLL. If FM24
cannot show one, the message goes to the companion terminal instead, and an
unanswerable confirmation counts as Cancel.
"""

from __future__ import annotations

import time

from live_memory import MemoryUnavailable, find_game_pid
from native_dialog import DialogUnavailable, NativeProgress, notify, show_dialog
from native_profile import shown_team, squad_club, squad_player_profile
from native_squad import NOT_A_SQUAD, read_squad_rows


def _terminal(text: str) -> None:
    print(f"RegenerateFM: {text}", flush=True)


def confirm_batch(missing: int, total: int, *, preview_only: bool = False,
                  native: tuple[int, str] | None = None, skipped: int = 0, brief: int = 0,
                  coaches: int = 0, others: int = 0, team: str = "") -> bool:
    """Ask in FM24's own dialog before any request; ``native`` is the DLL's
    port and token. Without an answer from FM24, nothing is generated.
    ``skipped`` players could not be read (FM24 has not loaded their names);
    ``brief`` players have only FM24's brief record (no age or ability).
    ``coaches`` are player-coaches and ``others`` rows that aren't players
    RegenerateFM can read. ``team`` names the team most players shown play for."""
    note = (f" {skipped} {'player was' if skipped == 1 else 'players were'} left out because FM24 "
            "hasn't loaded their names; scroll the list to them and run it again to include them."
            if skipped else "")
    if brief:
        note += (f" {brief} {'player' if brief == 1 else 'players'} FM24 lists without an age or "
                 f"ability (Scouting Required) {'was' if brief == 1 else 'were'} left out; FM24 keeps "
                 "too little about them to describe a face.")
    if coaches:
        note += (f" {coaches} {'player-coach was' if coaches == 1 else 'player-coaches were'} left out; "
                 "make their faces from their own profiles.")
    if others:
        note += (f" {others} {'row that isn' if others == 1 else 'rows that aren'}'t "
                 f"{'a player' if others == 1 else 'players'} RegenerateFM can read "
                 f"{'was' if others == 1 else 'were'} left out.")
    left_out = skipped + brief + coaches + others
    players = f"{team} players" if team else "squad players"
    if not missing:
        notify(*(native or (None, "")),
               f"All {total - left_out} {players} read already have portraits.{note}"
               if left_out else f"All {total} {players} already have portraits.", _terminal)
        return False
    counted = (f"{missing} of {total} {players} have no portrait." if not left_out
               else f"{missing} of the {total - left_out} {players} read have no portrait.")
    action = "Preview faces" if preview_only else "Generate faces"
    bill = "No API requests will be made in preview mode." if preview_only else \
        "Each face makes one billed API request."
    if native:
        try:
            return show_dialog(*native, "confirm", f"{action}?",
                               f"{counted} "
                               f"{action} for the {missing} missing players? {bill}{note}",
                               timeout=600) == "yes"
        except TimeoutError:
            return False
        except (DialogUnavailable, ValueError, OSError) as exc:
            _terminal(f"FM24 could not show the confirmation ({exc}); no image request was made.")
            return False
    _terminal("The squad command needs the FM24 native DLL; no image request was made.")
    return False


def _finish(progress, native, text: str, fallback) -> None:
    """The batch result: in the FM24 progress dialog if it is still open,
    else a new FM24 message, else the companion terminal."""
    if isinstance(progress, NativeProgress):
        progress.finish(text, fallback)
        return
    if progress:
        progress.close()
    notify(*(native or (None, "")), text, fallback)


def run_native_squad_batch(handler, generate, describe=str, *, pid: int | None = None, key_ready=None) -> None:
    """Native-menu squad command: read the roster, portrait state and every
    prompt from FM24's memory, then generate without opening any profile.

    ``generate(person_id, profile)`` returns the companion's result dict.
    ``key_ready()``, if given, asks for a missing OpenAI key after the
    confirmation and returns False when the player cancels. Undrawn rows
    come from the table's row model, so nothing is scrolled.
    """
    batch_dialog = None
    # FM24's own dialogs through the DLL's authenticated loopback listener.
    native = (handler.reload_port, handler.token) if getattr(handler, "reload_port", None) else None
    try:
        print("FM24 squad scan started (game memory; no API requests).", flush=True)
        pid = pid or find_game_pid()

        state = read_squad_rows(pid)
        missing = state.missing
        print(f"FM24 squad scan complete: {len(missing)} missing of {state.total} players.", flush=True)
        if not confirm_batch(len(missing), state.total, preview_only=handler.preview_only, native=native,
                             skipped=len(state.unreadable), brief=len(state.brief),
                             coaches=state.coaches, others=state.others,
                             team=shown_team(pid, [row.record for row in state.rows])):
            return
        club = squad_club(pid, [row.record for row in state.rows])
        # Every prompt is validated before the first billed request.
        for row in missing:
            squad_player_profile(pid, row.record, row.name, club, state.today)
        if not handler.preview_only and key_ready is not None and not key_ready():
            print("No OpenAI key was saved; no image request was made.", flush=True)
            return
        action = "Previewing" if handler.preview_only else "Generating"
        if not native:
            raise ValueError("the squad command needs the FM24 native DLL")
        batch_dialog = NativeProgress(*native, "RegenerateFM squad faces",
                                      f"{action} {missing[0].name} (1/{len(missing)})")
        completed = 0
        for index, row in enumerate(missing):
            if batch_dialog.stopped.is_set():
                print(f"FM24 squad batch stopped after {completed} faces.", flush=True)
                _finish(batch_dialog, native,
                        f"Stopped after {completed} of {len(missing)} squad faces.", _terminal)
                batch_dialog = None
                return
            batch_dialog.update(f"{action} {row.name} ({index + 1}/{len(missing)})",
                                round(index * 100 / len(missing)))
            # Read again just before the request: the record must still be this player.
            profile = squad_player_profile(pid, row.record, row.name, club, state.today)
            result = generate(row.person_id, profile)
            print(describe(result), flush=True)
            if not result.get("ok") or result.get("playerId") != row.person_id:
                raise ValueError(f"Generation stopped at {row.name}: {result.get('error', 'player mismatch')}")
            if not handler.preview_only and not result.get("refreshed"):
                raise ValueError(f"Generation stopped after saving {row.name}: " +
                                 result.get("warning", "FM24 reload failed"))
            completed += 1
            batch_dialog.update(f"Loaded {row.name} ({completed}/{len(missing)})",
                                round(completed * 100 / len(missing)))
            print(f"FM24 squad batch: {completed}/{len(missing)} {row.name}", flush=True)
            # A short gap to stop before the next billed request.
            for _ in range(10 if index + 1 < len(missing) and not handler.preview_only else 0):
                if batch_dialog.stopped.is_set():
                    break
                time.sleep(.3)
        _finish(batch_dialog, native,
                f"{'Previewed' if handler.preview_only else 'Generated'} {completed} squad faces.", _terminal)
        batch_dialog = None
    except (ValueError, OSError, RuntimeError, MemoryUnavailable) as exc:
        print(f"FM24 squad batch stopped: {exc}", flush=True)
        # Nothing was started on a screen without a squad list, so it says just that.
        _finish(batch_dialog, native, NOT_A_SQUAD if str(exc) == NOT_A_SQUAD else f"Squad faces stopped: {exc}",
                _terminal)
        batch_dialog = None
    finally:
        if batch_dialog:
            batch_dialog.close()
