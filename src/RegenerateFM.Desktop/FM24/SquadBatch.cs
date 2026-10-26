using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;

namespace RegenerateFM.Desktop.FM24
{
    // FM24's squad and staff commands: reads the list from game memory,
    // confirms in FM24, then generates the missing faces one at a time. The
    // confirmation also offers the club's other people (its staff from a
    // squad, its players from the staff) when some of them have no face.
    // Everything is shown in FM24's own dialogs through the native DLL. If FM24
    // cannot show one, the message goes to the companion's log instead, and an
    // unanswerable confirmation counts as Cancel.
    public sealed class SquadBatch
    {
        private readonly Companion _companion;

        public SquadBatch(Companion companion)
        {
            _companion = companion;
        }

        // Replaced by tests.
        public Func<GameProcess, SquadState> ReadRows = SquadReader.ReadRows;
        // The open screen's club's staff, and its whole list.
        public Func<GameProcess, (SquadState, StaffReader.ClubList)> ReadStaff = game => (StaffReader.ReadStaff(game, out var people), people);
        public Func<GameProcess, IList<long>, string> ShownTeam = ProfileReader.ShownTeam;
        public Func<GameProcess, IList<long>, string> SquadClub = ProfileReader.SquadClub;
        // The staff of the club most of a squad's players are contracted to.
        public Func<GameProcess, IList<long>, StaffReader.ClubList> SquadClubPeople = (game, records) =>
        {
            long club = StaffReader.MostCommonClub(game, records);
            if (club == 0)
                throw new MemoryUnavailableException("Could not tell which club the squad is.");
            return StaffReader.ClubPeople(game, club);
        };
        public Func<GameProcess, long, string, string, DateTime, ProfileSnapshot> PlayerProfile = ProfileReader.SquadPlayerProfile;
        public Func<GameProcess, long, string, string, DateTime, ProfileSnapshot> StaffProfile = ProfileReader.StaffListProfile;
        public Func<string, string, string, string, double?, string> Confirm;
        public Func<string, string, string, string, double?, string> Choose;
        public Func<string, Dll.IBatchProgress> StartProgress;
        public Func<string, ProfileSnapshot, FaceResult> Generate;
        public TimeSpan Gap = TimeSpan.FromMilliseconds(300);

        private void Terminal(string text) => _companion.Log("RegenerateFM: " + text);

        private void Notify(string text) =>
            Dll.Notify(_companion.ReloadPort != 0 ? _companion.ReloadPort : (int?)null, _companion.Token, text, Terminal);

        private static string Count(int count, string one, string many) => count + " " + (count == 1 ? one : many);

        // Asks in FM24's own dialog before any request. Without an answer from
        // FM24, nothing is generated: "yes" makes the list's ``missing`` faces,
        // "extra" those and the club's ``otherMissing`` other people (staff for
        // a squad, players for ``staff``), "no" nothing. ``skipped`` people could
        // not be read (FM24 has not loaded their names); ``brief`` players have
        // only FM24's brief record (no age or ability). ``coaches`` are
        // player-coaches and ``others`` rows that aren't people RegenerateFM can
        // read. ``team`` names the team most people shown are at.
        public string ConfirmBatch(int missing, int total, int skipped = 0, int brief = 0, int coaches = 0, int others = 0, string team = "",
                                   int otherMissing = 0, int otherTotal = 0, bool staff = false)
        {
            string one = staff ? "member of staff" : "player", many = staff ? "members of staff" : "players";
            string note = skipped == 0 ? ""
                : staff ? " " + Count(skipped, one + " was", many + " were") + " left out because RegenerateFM couldn't read their names."
                : " " + Count(skipped, one + " was", many + " were") + " left out because FM24 hasn't loaded their names; " +
                  "scroll the list to them and run it again to include them.";
            if (brief > 0)
                note += " " + Count(brief, "player", "players") + " FM24 lists without an age or ability (Scouting Required) " +
                        (brief == 1 ? "was" : "were") + " left out; FM24 keeps too little about them to describe a face.";
            if (coaches > 0)
                note += " " + Count(coaches, "player-coach was", "player-coaches were") +
                        (otherTotal > 0 ? " left out here and counted with the staff." : " left out; make their faces from their own profiles.");
            if (others > 0)
                note += " " + Count(others, "row that isn", "rows that aren") + "'t " + (staff ? "staff" : others == 1 ? "a player" : "players") +
                        " RegenerateFM can read " + (others == 1 ? "was" : "were") + " left out.";
            int leftOut = skipped + brief + coaches + others;
            string listed = (!string.IsNullOrEmpty(team) ? team + " " : staff ? "" : "squad ") + (staff ? "staff" : "players");
            string other = staff ? "players" : "staff";
            string otherText = otherMissing > 0 ? " " + otherMissing + " of the club's " + otherTotal + " " + other + " have none either." : "";
            if (missing == 0 && otherMissing == 0)
            {
                string all = leftOut > 0 ? "All " + (total - leftOut) + " " + listed + " read already have portraits."
                                         : "All " + total + " " + listed + " already have portraits.";
                Notify(all + (otherTotal > 0 ? " So do the club's " + otherTotal + " " + other + "." : "") + note);
                return "no";
            }
            string action = _companion.PreviewOnly ? "Preview faces" : "Generate faces";
            string verb = _companion.PreviewOnly ? "Preview " : "Generate ";
            string bill = _companion.PreviewOnly ? "No API requests will be made in preview mode." : "Each face makes one billed API request.";
            if (_companion.ReloadPort == 0)
            {
                Terminal("The " + (staff ? "staff" : "squad") + " command needs the FM24 native DLL; no image request was made.");
                return "no";
            }
            try
            {
                var confirm = Confirm ?? ((kind, title, text, _, timeout) => Dll.ShowDialog(_companion.ReloadPort, _companion.Token, kind, title, text, timeout));
                var choose = Choose ?? ((title, text, yes, extra, timeout) => Dll.ShowChoice(_companion.ReloadPort, _companion.Token, title, text, yes, extra, timeout));
                if (missing == 0)
                {
                    // Only the club's other people are missing faces.
                    string none = "All " + (total - leftOut) + " " + listed + (leftOut > 0 ? " read" : "") + " already have portraits, but " +
                                  otherMissing + " of the club's " + otherTotal + " " + other + " have none.";
                    return confirm("confirm", action + "?", none + " " + action + " for the " + otherMissing + " missing " + other + "? " + bill + note,
                                   null, 600) == "yes" ? "extra" : "no";
                }
                string counted = leftOut == 0 ? missing + " of " + total + " " + listed + " have no portrait."
                                              : missing + " of the " + (total - leftOut) + " " + listed + " read have no portrait.";
                if (otherMissing == 0)
                    return confirm("confirm", action + "?", counted + " " + action + " for the " + missing + " missing " + (staff ? "staff" : "players") + "? " +
                                   bill + note, null, 600) == "yes" ? "yes" : "no";
                string answer = choose(action + "?", counted + otherText + " " + bill + note, verb + missing + (missing == 1 ? " face" : " faces"),
                                       "Include " + other + ": " + (missing + otherMissing) + " faces", 600);
                return answer == "yes" || answer == "extra" ? answer : "no";
            }
            catch (TimeoutException)
            {
                return "no";
            }
            catch (Exception error) when (error is DialogUnavailableException || error is ArgumentException ||
                                          error is System.Net.Sockets.SocketException)
            {
                Terminal("FM24 could not show the confirmation (" + error.Message + "); no image request was made.");
                return "no";
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
        public void Run(GameProcess game = null) => Run(game, staff: false);

        // The staff command: the same for the open Staff table.
        public void RunStaff(GameProcess game = null) => Run(game, staff: true);

        private void Run(GameProcess game, bool staff)
        {
            string list = staff ? "staff" : "squad";
            Dll.IBatchProgress dialog = null;
            try
            {
                _companion.Log("FM24 " + list + " scan started (game memory; no API requests).");
                game ??= _companion.FindGame();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                SquadState state;
                StaffReader.ClubList people = null;
                if (staff)
                    (state, people) = ReadStaff(game);
                else
                    state = ReadRows(game);
                var missing = state.Missing;
                var records = state.Rows.Select(row => row.Record).ToList();
                string club = SquadClub(game, records);
                if (people == null)
                {
                    try
                    {
                        people = SquadClubPeople(game, records);
                    }
                    catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                    {
                        // The squad's own faces can still be made.
                        _companion.Log("FM24 squad scan: could not read the club's staff: " + error.Message);
                    }
                }
                var listed = new HashSet<string>(state.Rows.Select(row => row.PersonId));
                var others = (people == null ? new List<SquadRow>() : staff ? people.Players : people.Staff)
                    .Where(row => !listed.Contains(row.PersonId)).ToList();
                int unnamed = people == null ? 0 : staff ? people.UnnamedPlayers.Count : people.UnnamedStaff.Count;
                var otherMissing = others.Where(row => !state.Indexed.Contains(row.PersonId)).ToList();
                _companion.Log("FM24 " + list + " scan complete: " + missing.Count + " missing of " + state.Total + (staff ? " staff" : " players") +
                               ", and " + otherMissing.Count + " of the club's " + others.Count + (staff ? " players" : " staff") +
                               (unnamed > 0 ? " (" + unnamed + " without a readable name left out)" : "") + " (" +
                               clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s).");
                string answer = ConfirmBatch(missing.Count, state.Total, state.Unreadable.Count, state.Brief.Count, state.Coaches, state.Others,
                                             staff ? club : ShownTeam(game, records), otherMissing.Count, others.Count, staff);
                if (answer == "no")
                    return;
                var queue = answer == "yes" ? missing : missing.Concat(otherMissing).ToList();
                ProfileSnapshot Profile(SquadRow row) =>
                    row.Staff ? StaffProfile(game, row.Record, row.Name, row.Job, state.Today)
                              : PlayerProfile(game, row.Record, row.Name, club, state.Today);
                // Every prompt is validated before the first billed request.
                foreach (var row in queue)
                    Profile(row);
                if (!_companion.PreviewOnly && !_companion.EnsureKey())
                {
                    _companion.Log("No OpenAI key was saved; no image request was made.");
                    return;
                }
                string action = _companion.PreviewOnly ? "Previewing" : "Generating";
                if (_companion.ReloadPort == 0)
                    throw new InvalidOperationException("the " + list + " command needs the FM24 native DLL");
                string faces = queue.All(row => row.Staff) ? "staff faces" : queue.Any(row => row.Staff) ? "faces" : "squad faces";
                string first = action + " " + queue[0].Name + " (1/" + queue.Count + ")";
                dialog = StartProgress != null ? StartProgress(first)
                                               : new Dll.Progress(_companion.ReloadPort, _companion.Token, "RegenerateFM " + faces, first);
                int completed = 0;
                for (int index = 0; index < queue.Count; index++)
                {
                    var row = queue[index];
                    if (dialog.Stopped.IsSet)
                    {
                        _companion.Log("FM24 " + list + " batch stopped after " + completed + " faces.");
                        Finish(dialog, "Stopped after " + completed + " of " + queue.Count + " " + faces + ".");
                        dialog = null;
                        return;
                    }
                    dialog.Update(action + " " + row.Name + " (" + (index + 1) + "/" + queue.Count + ")");
                    // Read again just before the request: the record must still be this person.
                    var profile = Profile(row);
                    var result = (Generate ?? _companion.GenerateForProfile)(row.PersonId, profile);
                    _companion.Log(result.Describe());
                    if (!result.Ok || result.PlayerId != row.PersonId)
                        throw new InvalidOperationException("Generation stopped at " + row.Name + ": " + (result.Error ?? "person mismatch"));
                    if (!_companion.PreviewOnly && !result.Refreshed)
                        throw new InvalidOperationException("Generation stopped after saving " + row.Name + ": " + (result.Warning ?? "FM24 reload failed"));
                    completed++;
                    dialog.Update("Loaded " + row.Name + " (" + completed + "/" + queue.Count + ")");
                    _companion.Log("FM24 " + list + " batch: " + completed + "/" + queue.Count + " " + row.Name);
                    // A short gap to stop before the next billed request.
                    if (index + 1 < queue.Count && !_companion.PreviewOnly)
                        for (int i = 0; i < 10 && !dialog.Stopped.IsSet; i++)
                            Thread.Sleep(Gap);
                }
                Finish(dialog, (_companion.PreviewOnly ? "Previewed " : "Generated ") + completed + " " + faces + ".");
                dialog = null;
            }
            catch (Exception error) when (error is InvalidOperationException || error is MemoryUnavailableException ||
                                          People.Unreadable(error) || error is DialogUnavailableException || error is SocketException ||
                                          error is TimeoutException)
            {
                _companion.Log("FM24 " + list + " batch stopped: " + error.Message);
                // Nothing was started on a screen without the list, so it says just that.
                bool wrongScreen = error.Message == SquadReader.NotASquad || error.Message == StaffReader.NotAClub;
                Finish(dialog, wrongScreen ? error.Message : (staff ? "Staff" : "Squad") + " faces stopped: " + error.Message);
                dialog = null;
            }
            catch (Exception error) when (dialog != null)
            {
                // Anything else still ends the progress dialog (so Stop isn't left
                // doing nothing), which then says all the player needs.
                _companion.Log("FM24 " + list + " batch failed: " + error);
                Finish(dialog, (staff ? "Staff" : "Squad") + " faces stopped: " + error.Message);
                dialog = null;
            }
            finally
            {
                dialog?.Dispose();
            }
        }
    }
}
