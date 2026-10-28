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

        // "Preview faces for the 3 missing players?"
        private string ForMissing(int count, bool players) =>
            _companion.PreviewOnly
                ? players ? Text.T("Preview faces for the {0} missing players?", count) : Text.T("Preview faces for the {0} missing staff?", count)
                : players ? Text.T("Generate faces for the {0} missing players?", count) : Text.T("Generate faces for the {0} missing staff?", count);

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
            string note = "";
            if (skipped > 0)
                note += " " + (staff
                    ? skipped == 1 ? Text.T("{0} member of staff was left out because RegenerateFM couldn't read their names.", skipped)
                                   : Text.T("{0} members of staff were left out because RegenerateFM couldn't read their names.", skipped)
                    : skipped == 1 ? Text.T("{0} player was left out because FM24 hasn't loaded their names; scroll the list to them and run it again to include them.", skipped)
                                   : Text.T("{0} players were left out because FM24 hasn't loaded their names; scroll the list to them and run it again to include them.", skipped));
            if (brief > 0)
                note += " " + (brief == 1
                    ? Text.T("{0} player FM24 lists without an age or ability (Scouting Required) was left out; FM24 keeps too little about them to describe a face.", brief)
                    : Text.T("{0} players FM24 lists without an age or ability (Scouting Required) were left out; FM24 keeps too little about them to describe a face.", brief));
            if (coaches > 0)
                note += " " + (otherTotal > 0
                    ? coaches == 1 ? Text.T("{0} player-coach was left out here and counted with the staff.", coaches)
                                   : Text.T("{0} player-coaches were left out here and counted with the staff.", coaches)
                    : coaches == 1 ? Text.T("{0} player-coach was left out; make their faces from their own profiles.", coaches)
                                   : Text.T("{0} player-coaches were left out; make their faces from their own profiles.", coaches));
            if (others > 0)
                note += " " + (staff
                    ? others == 1 ? Text.T("{0} row that isn't staff RegenerateFM can read was left out.", others)
                                  : Text.T("{0} rows that aren't staff RegenerateFM can read were left out.", others)
                    : others == 1 ? Text.T("{0} row that isn't a player RegenerateFM can read was left out.", others)
                                  : Text.T("{0} rows that aren't players RegenerateFM can read were left out.", others));
            int leftOut = skipped + brief + coaches + others;
            string listed = string.IsNullOrEmpty(team) ? staff ? Text.T("staff") : Text.T("squad players")
                          : staff ? Text.T("{0} staff", team) : Text.T("{0} players", team);
            // The club's other people: its players for the staff, its staff for a squad.
            string otherText = otherMissing <= 0 ? ""
                : " " + (staff ? Text.T("{0} of the club's {1} players have none either.", otherMissing, otherTotal)
                               : Text.T("{0} of the club's {1} staff have none either.", otherMissing, otherTotal));
            if (missing == 0 && otherMissing == 0)
            {
                string all = leftOut > 0 ? Text.T("All {0} {1} read already have portraits.", total - leftOut, listed)
                                         : Text.T("All {0} {1} already have portraits.", total, listed);
                string also = otherTotal <= 0 ? ""
                    : " " + (staff ? Text.T("So do the club's {0} players.", otherTotal) : Text.T("So do the club's {0} staff.", otherTotal));
                Notify(all + also + note);
                return "no";
            }
            string title = _companion.PreviewOnly ? Text.T("Preview faces?") : Text.T("Generate faces?");
            string bill = _companion.PreviewOnly ? Text.T("No API requests will be made in preview mode.") : Text.T("Each face makes one billed API request.");
            if (_companion.ReloadPort == 0)
            {
                Terminal("The " + (staff ? "staff" : "squad") + " command needs the FM24 native DLL; no image request was made.");
                return "no";
            }
            try
            {
                var confirm = Confirm ?? ((kind, heading, text, _, timeout) => Dll.ShowDialog(_companion.ReloadPort, _companion.Token, kind, heading, text, timeout));
                var choose = Choose ?? ((heading, text, yesLabel, extraLabel, timeout) => Dll.ShowChoice(_companion.ReloadPort, _companion.Token, heading, text, yesLabel, extraLabel, timeout));
                if (missing == 0)
                {
                    // Only the club's other people are missing faces.
                    string none = leftOut > 0
                        ? staff ? Text.T("All {0} {1} read already have portraits, but {2} of the club's {3} players have none.", total - leftOut, listed, otherMissing, otherTotal)
                                : Text.T("All {0} {1} read already have portraits, but {2} of the club's {3} staff have none.", total - leftOut, listed, otherMissing, otherTotal)
                        : staff ? Text.T("All {0} {1} already have portraits, but {2} of the club's {3} players have none.", total - leftOut, listed, otherMissing, otherTotal)
                                : Text.T("All {0} {1} already have portraits, but {2} of the club's {3} staff have none.", total - leftOut, listed, otherMissing, otherTotal);
                    return confirm("confirm", title, none + " " + ForMissing(otherMissing, players: staff) + " " + bill + note,
                                   null, 600) == "yes" ? "extra" : "no";
                }
                string counted = leftOut == 0 ? Text.T("{0} of {1} {2} have no portrait.", missing, total, listed)
                                              : Text.T("{0} of the {1} {2} read have no portrait.", missing, total - leftOut, listed);
                if (otherMissing == 0)
                    return confirm("confirm", title, counted + " " + ForMissing(missing, players: !staff) + " " +
                                   bill + note, null, 600) == "yes" ? "yes" : "no";
                string yes = _companion.PreviewOnly
                    ? missing == 1 ? Text.T("Preview {0} face", missing) : Text.T("Preview {0} faces", missing)
                    : missing == 1 ? Text.T("Generate {0} face", missing) : Text.T("Generate {0} faces", missing);
                string extra = staff ? Text.T("Include players: {0} faces", missing + otherMissing) : Text.T("Include staff: {0} faces", missing + otherMissing);
                string answer = choose(title, counted + otherText + " " + bill + note, yes, extra, 600);
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

        private static string Stopped(bool staff, string reason) =>
            staff ? Text.T("Staff faces stopped: {0}", reason) : Text.T("Squad faces stopped: {0}", reason);

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
                string Working(string name, int at) =>
                    _companion.PreviewOnly ? Text.T("Previewing {0} ({1}/{2})", name, at, queue.Count) : Text.T("Generating {0} ({1}/{2})", name, at, queue.Count);
                if (_companion.ReloadPort == 0)
                    throw new InvalidOperationException("the " + list + " command needs the FM24 native DLL");
                // Staff faces only, players' only, or both.
                int kind = queue.All(row => row.Staff) ? 0 : queue.Any(row => row.Staff) ? 1 : 2;
                string first = Working(queue[0].Name, 1);
                dialog = StartProgress != null ? StartProgress(first)
                    : new Dll.Progress(_companion.ReloadPort, _companion.Token,
                                       kind == 0 ? Text.T("RegenerateFM staff faces") : kind == 1 ? Text.T("RegenerateFM faces") : Text.T("RegenerateFM squad faces"),
                                       first);
                int completed = 0;
                for (int index = 0; index < queue.Count; index++)
                {
                    var row = queue[index];
                    if (dialog.Stopped.IsSet)
                    {
                        _companion.Log("FM24 " + list + " batch stopped after " + completed + " faces.");
                        Finish(dialog, kind == 0 ? Text.T("Stopped after {0} of {1} staff faces.", completed, queue.Count)
                                       : kind == 1 ? Text.T("Stopped after {0} of {1} faces.", completed, queue.Count)
                                       : Text.T("Stopped after {0} of {1} squad faces.", completed, queue.Count));
                        dialog = null;
                        return;
                    }
                    dialog.Update(Working(row.Name, index + 1));
                    // Read again just before the request: the record must still be this person.
                    var profile = Profile(row);
                    var result = (Generate ?? _companion.GenerateForProfile)(row.PersonId, profile);
                    _companion.Log(result.Describe());
                    if (!result.Ok || result.PlayerId != row.PersonId)
                        throw new InvalidOperationException(Text.T("Generation stopped at {0}: {1}", row.Name, result.Error ?? Text.T("person mismatch")));
                    if (!_companion.PreviewOnly && !result.Refreshed)
                        throw new InvalidOperationException(Text.T("Generation stopped after saving {0}: {1}", row.Name, result.Warning ?? Text.T("FM24 reload failed")));
                    completed++;
                    dialog.Update(Text.T("Loaded {0} ({1}/{2})", row.Name, completed, queue.Count));
                    _companion.Log("FM24 " + list + " batch: " + completed + "/" + queue.Count + " " + row.Name);
                    // A short gap to stop before the next billed request.
                    if (index + 1 < queue.Count && !_companion.PreviewOnly)
                        for (int i = 0; i < 10 && !dialog.Stopped.IsSet; i++)
                            Thread.Sleep(Gap);
                }
                Finish(dialog, _companion.PreviewOnly
                    ? kind == 0 ? Text.T("Previewed {0} staff faces.", completed) : kind == 1 ? Text.T("Previewed {0} faces.", completed)
                                : Text.T("Previewed {0} squad faces.", completed)
                    : kind == 0 ? Text.T("Generated {0} staff faces.", completed) : kind == 1 ? Text.T("Generated {0} faces.", completed)
                                : Text.T("Generated {0} squad faces.", completed));
                dialog = null;
            }
            catch (Exception error) when (error is InvalidOperationException || error is MemoryUnavailableException ||
                                          People.Unreadable(error) || error is DialogUnavailableException || error is SocketException ||
                                          error is TimeoutException)
            {
                _companion.Log("FM24 " + list + " batch stopped: " + error.Message);
                // Nothing was started on a screen without the list, so it says just that.
                bool wrongScreen = error.Message == SquadReader.NotASquad || error.Message == StaffReader.NotAClub;
                Finish(dialog, wrongScreen ? error.Message : Stopped(staff, error.Message));
                dialog = null;
            }
            catch (Exception error) when (dialog != null)
            {
                // Anything else still ends the progress dialog (so Stop isn't left
                // doing nothing), which then says all the player needs.
                _companion.Log("FM24 " + list + " batch failed: " + error);
                Finish(dialog, Stopped(staff, error.Message));
                dialog = null;
            }
            finally
            {
                dialog?.Dispose();
            }
        }
    }
}
