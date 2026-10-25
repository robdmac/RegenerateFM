using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;

namespace RegenerateFM.Desktop.FM24
{
    // FM24's squad command: reads the roster from game memory, confirms in
    // FM24, then generates the missing faces one at a time. Everything is shown
    // in FM24's own dialogs through the native DLL. If FM24 cannot show one, the
    // message goes to the companion's log instead, and an unanswerable
    // confirmation counts as Cancel.
    public sealed class SquadBatch
    {
        private readonly Companion _companion;

        public SquadBatch(Companion companion)
        {
            _companion = companion;
        }

        // Replaced by tests.
        public Func<GameProcess, SquadState> ReadRows = SquadReader.ReadRows;
        public Func<GameProcess, System.Collections.Generic.IList<long>, string> ShownTeam = ProfileReader.ShownTeam;
        public Func<GameProcess, System.Collections.Generic.IList<long>, string> SquadClub = ProfileReader.SquadClub;
        public Func<GameProcess, long, string, string, DateTime, ProfileSnapshot> PlayerProfile = ProfileReader.SquadPlayerProfile;
        public Func<string, string, string, string, double?, string> Confirm;
        public Func<string, Dll.IBatchProgress> StartProgress;
        public Func<string, ProfileSnapshot, FaceResult> Generate;
        public TimeSpan Gap = TimeSpan.FromMilliseconds(300);

        private void Terminal(string text) => _companion.Log("RegenerateFM: " + text);

        private void Notify(string text) =>
            Dll.Notify(_companion.ReloadPort != 0 ? _companion.ReloadPort : (int?)null, _companion.Token, text, Terminal);

        // Asks in FM24's own dialog before any request. Without an answer from
        // FM24, nothing is generated. ``skipped`` players could not be read (FM24
        // has not loaded their names); ``brief`` players have only FM24's brief
        // record (no age or ability). ``coaches`` are player-coaches and
        // ``others`` rows that aren't players RegenerateFM can read. ``team``
        // names the team most players shown play for.
        public bool ConfirmBatch(int missing, int total, int skipped = 0, int brief = 0, int coaches = 0, int others = 0, string team = "")
        {
            string note = skipped > 0
                ? " " + skipped + " " + (skipped == 1 ? "player was" : "players were") + " left out because FM24 hasn't loaded their names; " +
                  "scroll the list to them and run it again to include them."
                : "";
            if (brief > 0)
                note += " " + brief + " " + (brief == 1 ? "player" : "players") + " FM24 lists without an age or ability (Scouting Required) " +
                        (brief == 1 ? "was" : "were") + " left out; FM24 keeps too little about them to describe a face.";
            if (coaches > 0)
                note += " " + coaches + " " + (coaches == 1 ? "player-coach was" : "player-coaches were") +
                        " left out; make their faces from their own profiles.";
            if (others > 0)
                note += " " + others + " " + (others == 1 ? "row that isn" : "rows that aren") + "'t " + (others == 1 ? "a player" : "players") +
                        " RegenerateFM can read " + (others == 1 ? "was" : "were") + " left out.";
            int leftOut = skipped + brief + coaches + others;
            string players = !string.IsNullOrEmpty(team) ? team + " players" : "squad players";
            if (missing == 0)
            {
                Notify(leftOut > 0 ? "All " + (total - leftOut) + " " + players + " read already have portraits." + note
                                   : "All " + total + " " + players + " already have portraits.");
                return false;
            }
            string counted = leftOut == 0 ? missing + " of " + total + " " + players + " have no portrait."
                                          : missing + " of the " + (total - leftOut) + " " + players + " read have no portrait.";
            string action = _companion.PreviewOnly ? "Preview faces" : "Generate faces";
            string bill = _companion.PreviewOnly ? "No API requests will be made in preview mode." : "Each face makes one billed API request.";
            if (_companion.ReloadPort == 0)
            {
                Terminal("The squad command needs the FM24 native DLL; no image request was made.");
                return false;
            }
            try
            {
                var confirm = Confirm ?? ((kind, title, text, _, timeout) => Dll.ShowDialog(_companion.ReloadPort, _companion.Token, kind, title, text, timeout));
                return confirm("confirm", action + "?", counted + " " + action + " for the " + missing + " missing players? " + bill + note,
                               null, 600) == "yes";
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
                Terminal("FM24 could not show the confirmation (" + error.Message + "); no image request was made.");
                return false;
            }
        }

        // The batch result: in the FM24 progress dialog if it is still open,
        // else a new FM24 message, else the log.
        private void Finish(Dll.IBatchProgress progress, string text)
        {
            if (progress != null)
                progress.Finish(text, Terminal);
            else
                Notify(text);
        }

        // The squad command: reads the roster, portrait state and every prompt
        // from FM24's memory, then generates without opening any profile.
        // Undrawn rows come from the table's row model, so nothing is scrolled.
        public void Run(GameProcess game = null)
        {
            Dll.IBatchProgress dialog = null;
            try
            {
                _companion.Log("FM24 squad scan started (game memory; no API requests).");
                game ??= _companion.FindGame();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var state = ReadRows(game);
                var missing = state.Missing;
                _companion.Log("FM24 squad scan complete: " + missing.Count + " missing of " + state.Total + " players (" +
                               clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s).");
                var records = state.Rows.Select(row => row.Record).ToList();
                if (!ConfirmBatch(missing.Count, state.Total, state.Unreadable.Count, state.Brief.Count, state.Coaches, state.Others,
                                  ShownTeam(game, records)))
                    return;
                string club = SquadClub(game, records);
                // Every prompt is validated before the first billed request.
                foreach (var row in missing)
                    PlayerProfile(game, row.Record, row.Name, club, state.Today);
                if (!_companion.PreviewOnly && !_companion.EnsureKey())
                {
                    _companion.Log("No OpenAI key was saved; no image request was made.");
                    return;
                }
                string action = _companion.PreviewOnly ? "Previewing" : "Generating";
                if (_companion.ReloadPort == 0)
                    throw new InvalidOperationException("the squad command needs the FM24 native DLL");
                string first = action + " " + missing[0].Name + " (1/" + missing.Count + ")";
                dialog = StartProgress != null ? StartProgress(first)
                                               : new Dll.Progress(_companion.ReloadPort, _companion.Token, "RegenerateFM squad faces", first);
                int completed = 0;
                for (int index = 0; index < missing.Count; index++)
                {
                    var row = missing[index];
                    if (dialog.Stopped.IsSet)
                    {
                        _companion.Log("FM24 squad batch stopped after " + completed + " faces.");
                        Finish(dialog, "Stopped after " + completed + " of " + missing.Count + " squad faces.");
                        dialog = null;
                        return;
                    }
                    dialog.Update(action + " " + row.Name + " (" + (index + 1) + "/" + missing.Count + ")");
                    // Read again just before the request: the record must still be this player.
                    var profile = PlayerProfile(game, row.Record, row.Name, club, state.Today);
                    var result = (Generate ?? _companion.GenerateForProfile)(row.PersonId, profile);
                    _companion.Log(result.Describe());
                    if (!result.Ok || result.PlayerId != row.PersonId)
                        throw new InvalidOperationException("Generation stopped at " + row.Name + ": " + (result.Error ?? "player mismatch"));
                    if (!_companion.PreviewOnly && !result.Refreshed)
                        throw new InvalidOperationException("Generation stopped after saving " + row.Name + ": " + (result.Warning ?? "FM24 reload failed"));
                    completed++;
                    dialog.Update("Loaded " + row.Name + " (" + completed + "/" + missing.Count + ")");
                    _companion.Log("FM24 squad batch: " + completed + "/" + missing.Count + " " + row.Name);
                    // A short gap to stop before the next billed request.
                    if (index + 1 < missing.Count && !_companion.PreviewOnly)
                        for (int i = 0; i < 10 && !dialog.Stopped.IsSet; i++)
                            Thread.Sleep(Gap);
                }
                Finish(dialog, (_companion.PreviewOnly ? "Previewed " : "Generated ") + completed + " squad faces.");
                dialog = null;
            }
            catch (Exception error) when (error is InvalidOperationException || error is MemoryUnavailableException ||
                                          People.Unreadable(error) || error is DialogUnavailableException || error is SocketException ||
                                          error is TimeoutException)
            {
                _companion.Log("FM24 squad batch stopped: " + error.Message);
                // Nothing was started on a screen without a squad list, so it says just that.
                Finish(dialog, error.Message == SquadReader.NotASquad ? SquadReader.NotASquad : "Squad faces stopped: " + error.Message);
                dialog = null;
            }
            catch (Exception error) when (dialog != null)
            {
                // Anything else still ends the progress dialog (so Stop isn't left
                // doing nothing), which then says all the player needs.
                _companion.Log("FM24 squad batch failed: " + error);
                Finish(dialog, "Squad faces stopped: " + error.Message);
                dialog = null;
            }
            finally
            {
                dialog?.Dispose();
            }
        }
    }
}
