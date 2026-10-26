using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;
using Xunit;

namespace RegenerateFM.Tests
{
    // The squad command: every row from the table's row model, and the batch.
    public class SquadTests
    {
        private const long Table = 0x10000, Model = 0x11000, RowsAt = 0x12000, People = 0x20000;
        private static readonly DateTime Today = new DateTime(2023, 1, 2);

        // ``players``: (person ID, handle), in table order after the header row.
        private static (FakeProcess, SquadState) Build(List<(uint Id, int Handle)> players, uint[] drawn, int[] rowIds = null,
                                                         HashSet<uint> coaches = null)
        {
            var game = new FakeProcess();
            game.Q(Table + Layout.Windows.TableModel, Model);
            var rows = Enumerable.Range(0, players.Count + 1).Select(index => RowsAt + 0x100L * index).ToList();
            game.Q(Model + SquadReader.ModelRows, 0x13000, 0x13000 + 8 * rows.Count).Q(0x13000, rows.ToArray());
            var ids = rowIds ?? new[] { SquadReader.HeaderRowId }.Concat(players.Select(player => 0x01000000 | player.Handle)).ToArray();
            for (int i = 0; i < rows.Count; i++)
                game.Q(rows[i], rows[i] + 0x40, rows[i] + 0x50).Q(rows[i] + 0x40, SquadReader.RowIdKey, rows[i] + 0x60).I(rows[i] + 0x68, (uint)ids[i]);
            var people = new Dictionary<uint, long>();
            game.Put(0x30000 - 0x100, new byte[0x100 * (players.Count + 2)]);  // the person objects, around their records
            for (int index = 0; index < players.Count; index++)
            {
                var (id, handle) = players[index];
                long person = 0x30000 + 0x100L * index;
                long vtable = coaches != null && coaches.Contains(id) ? Layout.Windows.PlayerCoachVtables.First() : MemoryTests.PlayerVtable;
                game.Q(person, MemoryTests.Image + vtable).I(person + 8, (uint)handle).I(person + 12, id, id)
                    .Put(person + 24, new byte[] { 0, 0, 0x58, 0x10, 4, 0, 0, 0 });
                game.Q(People + 8L * handle, person);
                people[id] = person;
            }
            var state = new SquadState
            {
                Rows = drawn.Select(id => new SquadRow(id.ToString(), people[id] + 12, "Shown " + id)).ToList(),
                Total = players.Count, Today = Today, Visible = (1, drawn.Length), Table = Table,
            };
            return (game, state);
        }

        private static SquadState Read(FakeProcess game, SquadState state)
        {
            var names = SquadReader.Names;
            SquadReader.ReadDrawn = _ => state;
            SquadReader.Names = (_, rows) => rows.ToDictionary(row => row.Id, row => "Record " + row.Id);
            try
            {
                return SquadReader.ReadRows(game);
            }
            finally
            {
                SquadReader.ReadDrawn = g => SquadReader.Read(g);
                SquadReader.Names = names;
            }
        }

        private static readonly List<(uint, int)> Six = new List<(uint, int)> { (101, 7), (102, 3), (103, 9), (104, 1), (105, 5), (106, 2) };

        [Fact]
        public void UndrawnRowsAreReadThroughThePersonTable()
        {
            var (game, state) = Build(Six, new uint[] { 103, 104, 105, 106, 102 });
            var result = Read(game, state);
            Assert.Equal(new[] { "101", "102", "103", "104", "105", "106" }, result.Rows.Select(row => row.PersonId));
            Assert.Equal("Record 101", result.Rows[0].Name);
            Assert.Equal("Shown 103", result.Rows[2].Name);
            Assert.True(result.Complete);
        }

        [Fact]
        public void APlayerCoachIsLeftOutAndCounted()
        {
            // FM24 keeps a player-coach's player fields outside the record, so the
            // list can't describe them; the rest of the squad still goes ahead.
            var (game, state) = Build(Six, new uint[] { 103, 104, 105, 106, 102 }, coaches: new HashSet<uint> { 101 });
            var result = Read(game, state);
            Assert.Equal(new[] { "102", "103", "104", "105", "106" }, result.Rows.Select(row => row.PersonId));
            Assert.Equal((1, 0), (result.Coaches, result.Others));
            Assert.True(result.Complete);
        }

        [Fact]
        public void AModelThatDoesNotMatchTheTableIsRefused()
        {
            var (game, state) = Build(Six, new uint[] { 103, 104, 105, 106, 102 });
            state.Total = 7;
            Assert.Throws<MemoryUnavailableException>(() => Read(game, state));
            (game, state) = Build(Six, new uint[] { 103, 104, 105, 106, 102 });
            state.Rows[4] = new SquadRow("999", state.Rows[4].Record, "Shown 999");  // a drawn row the model doesn't list
            Assert.Throws<MemoryUnavailableException>(() => Read(game, state));
            (game, state) = Build(Six, new uint[] { 103, 104, 105, 106, 102 },
                                  rowIds: new[] { SquadReader.HeaderRowId }.Concat(Enumerable.Repeat(0x02000007, 6)).ToArray());
            Assert.Contains("does not list people", Assert.Throws<MemoryUnavailableException>(() => Read(game, state)).Message);
        }

        // Stands in for FM24's squad progress dialog.
        private sealed class Progress : Dll.IBatchProgress
        {
            public readonly List<string> Shown = new List<string>();
            public string Result;
            public ManualResetEventSlim Stopped { get; } = new ManualResetEventSlim();
            public void Update(string message) => Shown.Add(message);
            public void Finish(string text, Action<string> fallback) => Result = text;
            public void Dispose() => Shown.Add("closed");
        }

        private sealed class Batch
        {
            public readonly List<string> Notices = new List<string>(), Logged = new List<string>();
            public readonly List<(string Id, string Name)> Calls = new List<(string, string)>();
            public readonly List<Progress> Made = new List<Progress>();
            public readonly SquadBatch Squad;
            private readonly FakeDll _dll = new FakeDll();

            public Batch(Func<string, ProfileSnapshot, FaceResult> generate, bool preview = false, bool dll = true)
            {
                var state = new SquadState
                {
                    Rows = Enumerable.Range(1, 5).Select(i => new SquadRow(i.ToString(), 0x1000 + i, "Player " + i)).ToList(),
                    Total = 5, Indexed = new HashSet<string> { "1", "2", "4" }, Today = Today, Visible = (1, 5),
                };
                _dll.Answer = fields =>
                {
                    Notices.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[6])));
                    return "shown";
                };
                var companion = new Companion { PreviewOnly = preview, ReloadPort = dll ? _dll.Port : 0, Token = new string('t', 43), Log = Logged.Add };
                companion.FindGame = () => new FakeProcess();
                Squad = new SquadBatch(companion)
                {
                    ReadRows = _ => state, SquadClub = (_, _) => "Club", ShownTeam = (_, _) => "Club II", Gap = TimeSpan.Zero,
                    Confirm = (_, _, _, _, _) => "yes",
                    PlayerProfile = (_, _, name, _, _) => new ProfileSnapshot { Name = name },
                    StartProgress = text =>
                    {
                        var progress = new Progress();
                        progress.Shown.Add(text);
                        Made.Add(progress);
                        return progress;
                    },
                    Generate = (person, profile) =>
                    {
                        Calls.Add((person, profile.Name));
                        return generate(person, profile);
                    },
                };
            }

            public void Run()
            {
                Squad.Run(new FakeProcess());
                _dll.Dispose();
            }
        }

        [Fact]
        public void OnlyMissingPlayersAreGeneratedByIdWithProgressInFm24()
        {
            var batch = new Batch((person, _) => new FaceResult { Ok = true, PlayerId = person, Refreshed = true });
            batch.Run();
            Assert.Equal(new[] { ("3", "Player 3"), ("5", "Player 5") }, batch.Calls);
            var progress = batch.Made.Single();
            Assert.Equal("Generating Player 3 (1/2)", progress.Shown[0]);
            Assert.Contains("Loaded Player 5 (2/2)", progress.Shown);
            // The progress dialog itself becomes the result message.
            Assert.Equal("Generated 2 squad faces.", progress.Result);
        }

        [Fact]
        public void EveryPromptIsValidatedBeforeAnyRequest()
        {
            var batch = new Batch((person, _) => new FaceResult { Ok = true, PlayerId = person, Refreshed = true });
            batch.Squad.PlayerProfile = (_, _, name, _, _) =>
                name == "Player 5" ? throw new MemoryUnavailableException("Invalid FM24 player age.") : new ProfileSnapshot { Name = name };
            batch.Run();
            Assert.Empty(batch.Calls);
            Assert.Contains("Invalid FM24 player age", batch.Notices.Last());
        }

        [Fact]
        public void StopReportsHowManyFacesWereMade()
        {
            Batch batch = null;
            batch = new Batch((person, _) =>
            {
                batch.Made[0].Stopped.Set();  // Stop chosen during the first face
                return new FaceResult { Ok = true, PlayerId = person, Refreshed = true };
            });
            batch.Run();
            Assert.Equal(new[] { "3" }, batch.Calls.Select(call => call.Id));
            Assert.Equal("Stopped after 1 of 2 squad faces.", batch.Made[0].Result);
        }

        [Fact]
        public void TheBatchStopsAtTheFirstFailedOrUnreloadedFace()
        {
            var batch = new Batch((person, _) => new FaceResult { Ok = true, PlayerId = person, Warning = "reload failed" });
            batch.Run();
            Assert.Equal(new[] { "3" }, batch.Calls.Select(call => call.Id));
            Assert.Contains("reload failed", batch.Made[0].Result);
        }

        [Fact]
        public void AnotherKindOfListIsNamedWithoutStoppingAnything()
        {
            // A Development Center team's overview: the menu item is offered there too.
            var batch = new Batch((person, _) => new FaceResult { Ok = true });
            batch.Squad.ReadRows = _ => throw new MemoryUnavailableException(SquadReader.NotASquad);
            batch.Run();
            Assert.Empty(batch.Calls);
            Assert.Equal(SquadReader.NotASquad, batch.Notices.Last());
        }

        [Fact]
        public void WithoutTheDllNothingIsGenerated()
        {
            var batch = new Batch((person, _) => new FaceResult { Ok = true }, dll: false);
            batch.Run();
            Assert.Empty(batch.Calls);
            Assert.Contains(batch.Logged, line => line.Contains("native DLL"));
        }

        [Fact]
        public void ThePreviewMakesNoRequest()
        {
            var batch = new Batch((person, profile) => new FaceResult { Ok = true, Preview = true, PlayerId = person, Prompt = "p" }, preview: true);
            batch.Run();
            Assert.Equal("Previewed 2 squad faces.", batch.Made[0].Result);
        }

        [Fact]
        public void TheConfirmationNamesTheTeamAndWhoWasLeftOut()
        {
            string asked = null;
            var notices = new List<string>();
            using var dll = new FakeDll();
            dll.Answer = fields => { notices.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[6]))); return "shown"; };
            var logged = new List<string>();
            var companion = new Companion { ReloadPort = dll.Port, Token = new string('t', 43), Log = logged.Add };
            var squad = new SquadBatch(companion) { Confirm = (_, _, text, _, _) => { asked = text; return "no"; } };
            Assert.False(squad.ConfirmBatch(2, 5));
            Assert.Contains("2 of 5 squad players have no portrait", asked);
            squad.ConfirmBatch(2, 5, skipped: 1);
            Assert.Contains("1 player was left out because FM24 hasn't loaded their names", asked);
            squad.ConfirmBatch(2, 23, brief: 11, team: "Inter Miami II");
            Assert.Contains("2 of the 12 Inter Miami II players read have no portrait", asked);
            Assert.Contains("11 players FM24 lists without an age or ability (Scouting Required) were left out", asked);
            squad.ConfirmBatch(2, 25, coaches: 1, others: 2, team: "Reading");
            Assert.Contains("2 of the 22 Reading players read have no portrait", asked);
            Assert.Contains("1 player-coach was left out; make their faces from their own profiles.", asked);
            Assert.Contains("2 rows that aren't players RegenerateFM can read were left out.", asked);
            Assert.False(squad.ConfirmBatch(0, 23, brief: 11, team: "Inter Miami II"));
            Assert.Contains("All 12 Inter Miami II players read already have portraits.", notices[0]);
            squad.Confirm = (_, _, _, _, _) => "yes";
            Assert.True(squad.ConfirmBatch(2, 5));
            // An unavailable dialog counts as Cancel.
            squad.Confirm = (_, _, _, _, _) => throw new DialogUnavailableException("busy");
            Assert.False(squad.ConfirmBatch(2, 5));
            Assert.Contains("no image request was made", logged.Last());
        }
    }
}
