using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;
using Xunit;

namespace RegenerateFM.Tests
{
    // The staff command, the club's people and the choice that offers them.
    public class StaffTests
    {
        private const long Image = MemoryTests.Image;
        private static readonly DateTime Today = new DateTime(2023, 9, 16);

        private static void Job(FakeProcess game, long record, ushort code)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, code);
            game.Put(record + 0x5000 + Layout.Windows.ContractJob, bytes);
        }

        // A staff member: a person record of the staff class, with handle ``handle``.
        private static void Staff(FakeProcess game, long record, uint id, int handle, string name)
        {
            MemoryTests.Player(game, record, id, name);
            game.Q(record - 12, Image + MemoryTests.StaffVtable).I(record - 4, (uint)handle);
        }

        [Fact]
        public void JobsAreNamedAsFm24ListsThem()
        {
            Assert.Equal("Goalkeeping Coach", StaffReader.JobTitle(0x22));
            // A player-coach's version of a job sets the lowest bit.
            Assert.Equal("Coach", StaffReader.JobTitle(0x03));
            Assert.Null(StaffReader.JobTitle(0x42));
        }

        [Fact]
        public void TheClubsPeopleAreEveryoneContractedToItsTeams()
        {
            var game = new FakeProcess(zeros: true);
            long first = 0x100000, youth = 0x110000, elsewhere = 0x120000, vector = 0x200000;
            MemoryTests.Team(game, first, 1, null, 10, "MK Dons");
            MemoryTests.Team(game, elsewhere, 3, null, 30, "Sutton");
            // The Under 18s: another team with the same club details.
            game.Q(youth, Image + Layout.Windows.TeamVtable).I(youth + 12, 2).I(youth + 0x30, (uint)(first + 0x400));
            var people = new (long Record, uint Id, bool IsStaff, long Team, ushort Job)[]
            {
                (0x320000, 103, true, elsewhere, 0x02), // another club's coach, the table's first person
                (0x300000, 101, true, first, 0x22),     // Goalkeeping Coach
                (0x310000, 102, true, youth, 0x0C),     // the Under 18s' Physio
                (0x330000, 104, false, first, 0),       // a player
                (0x340000, 105, true, first, 0x42),     // a job RegenerateFM can't name, and no readable name
                (0x350000, 106, true, 0, 0),            // no contract
            };
            for (int i = 0; i < people.Length; i++)
            {
                var (record, id, isStaff, team, job) = people[i];
                if (isStaff)
                    Staff(game, record, id, i, "Person " + id);
                else
                {
                    MemoryTests.Player(game, record, id, "Person " + id);
                    game.I(record - 4, (uint)i);
                }
                if (team != 0)
                {
                    MemoryTests.Contract(game, record, team);
                    Job(game, record, job);
                }
                game.Q(vector + 8L * i, record - 12);
            }
            long database = 0x400000;
            game.Q(Image + Layout.Windows.PersonTableGlobal, database)
                .Q(database + Layout.Windows.PersonTableSlot, vector, vector + 8L * people.Length);
            var names = StaffReader.Names;
            StaffReader.Names = (_, rows) => rows.Where(row => row.Id != "105").ToDictionary(row => row.Id, row => "Named " + row.Id);
            try
            {
                long club = StaffReader.MostCommonClub(game, new long[] { 0x300000, 0x310000, 0x330000 });
                Assert.Equal(first + 0x400, club);
                var list = StaffReader.ClubPeople(game, club);
                Assert.Equal(new[] { ("101", "Named 101", "Goalkeeping Coach"), ("102", "Named 102", "Physio") },
                             list.Staff.Select(row => (row.PersonId, row.Name, row.Job)));
                Assert.True(list.Staff.All(row => row.Staff));
                Assert.Equal(new[] { "105" }, list.UnnamedStaff);
                Assert.Equal(new[] { "104" }, list.Players.Select(row => row.PersonId));
                Assert.False(list.Players[0].Staff);
            }
            finally
            {
                StaffReader.Names = names;
            }
        }

        [Fact]
        public void AClubReferenceFindsTheClubInTheDatabasesVectorOfClubs()
        {
            var game = new FakeProcess(zeros: true);
            long database = 0x400000, clubs = 0x500000, decoys = 0x600000, club = 0x700000, other = 0x710000;
            game.Q(Image + Layout.Windows.PersonTableGlobal, database)
                .Q(database + 0x40, decoys, decoys + 8 * 20).Q(decoys + 8 * 7, other)  // another vector: not a club there
                .Q(other, Image + Layout.Windows.TeamVtable).I(other + 8, 7)
                .Q(database + 0x1F8, clubs, clubs + 8 * 20).Q(clubs + 8 * 7, club)
                .Q(club, Image + Layout.Windows.ClubVtable).I(club + 8, 7, 151027, 151027);
            Assert.Equal(club, StaffReader.ClubAt(game, 7));
            // A club that doesn't carry the index is a different object.
            game.I(club + 8, 8);
            Assert.Equal(StaffReader.NotAClub, Assert.Throws<MemoryUnavailableException>(() => StaffReader.ClubAt(game, 7)).Message);
        }

        [Fact]
        public void AStaffMembersPromptHasTheirJobAndContractClub()
        {
            var game = new FakeProcess();
            long record = 0x300000, team = 0x100000;
            Staff(game, record, 101, 1, "Erbil Bozkurt");
            MemoryTests.Team(game, team, 1, null, 10, "MK Dons");
            MemoryTests.Contract(game, record, team);
            var profile = ProfileReader.StaffListProfile(game, record, "Erbil Bozkurt", "Goalkeeping Coach", Today);
            Assert.Equal(("staff", "Erbil Bozkurt", 28, "Goalkeeping Coach", "MK Dons", "Icelandic"),
                         (profile.Type, profile.Name, profile.Age, profile.JobTitle, profile.Club, profile.Nationality));
            Assert.Equal(StaffReader.UnknownJob, ProfileReader.StaffListProfile(game, record, "Erbil Bozkurt", null, Today).JobTitle);
            Assert.Equal("male", profile.Gender);
            // A woman's record flags her (Sue Dawson, a director): her prompt mustn't call her a man.
            game.Put(record + Layout.Windows.PersonFlags, new byte[] { 0x11 });
            Assert.Equal("female", ProfileReader.StaffListProfile(game, record, "Sue Dawson", "Director", Today).Gender);
            // A player isn't described as staff.
            game.Q(record - 12, Image + MemoryTests.PlayerVtable);
            Assert.Throws<MemoryUnavailableException>(() => ProfileReader.StaffListProfile(game, record, "Erbil Bozkurt", "Coach", Today));
        }

        [Fact]
        public void AChoiceSendsBothAnswersLabelsAndReturnsTheOneChosen()
        {
            using var dll = new FakeDll("queued", "shown", "extra");
            Assert.Equal("extra", Dll.ShowChoice(dll.Port, new string('k', 43), "Generate faces?", "2 of 5 players have no portrait.",
                                                 "Generate 2 faces", "Include staff: 5 faces"));
            var first = dll.Received[0];
            Assert.Equal(("REGENERATEFM24D", "2", "choice"), (first[0], first[1], first[4]));
            Assert.Equal(("Generate 2 faces", "Include staff: 5 faces"),
                         (Encoding.UTF8.GetString(Convert.FromBase64String(first[7])), Encoding.UTF8.GetString(Convert.FromBase64String(first[8]))));
            Assert.Throws<ArgumentException>(() => Dll.ShowChoice(1, new string('k', 43), "T", "Text", new string('x', 100), "Other"));
        }

        // Stands in for FM24's progress dialog.
        private sealed class Progress : Dll.IBatchProgress
        {
            public readonly List<string> Shown = new List<string>();
            public string Result;
            public ManualResetEventSlim Stopped { get; } = new ManualResetEventSlim();
            public void Update(string message) => Shown.Add(message);
            public void Finish(string text, Action<string> fallback) => Result = text;
            public void Dispose() { }
        }

        private sealed class Batch : IDisposable
        {
            public readonly List<string> Notices = new List<string>(), Logged = new List<string>();
            public readonly List<(string Id, ProfileSnapshot Profile)> Calls = new List<(string, ProfileSnapshot)>();
            public readonly List<(string Title, string Text, string Yes, string Extra)> Choices = new List<(string, string, string, string)>();
            public readonly List<string> Confirmations = new List<string>();
            public Progress Made;
            public readonly SquadBatch Squad;
            private readonly FakeDll _dll = new FakeDll();

            // Players 1-4 (1 and 2 with faces) and staff 11-13 (11 with a face).
            public Batch(string answer)
            {
                var indexed = new HashSet<string> { "1", "2", "11" };
                var players = Enumerable.Range(1, 4).Select(i => new SquadRow(i.ToString(), 0x1000 + i, "Player " + i)).ToList();
                var staff = Enumerable.Range(11, 3).Select(i => new SquadRow(i.ToString(), 0x2000 + i, "Staff " + i, staff: true, job: i == 13 ? null : "Physio")).ToList();
                _dll.Answer = fields =>
                {
                    Notices.Add(Encoding.UTF8.GetString(Convert.FromBase64String(fields[6])));
                    return "shown";
                };
                var companion = new Companion { ReloadPort = _dll.Port, Token = new string('t', 43), Log = Logged.Add };
                Squad = new SquadBatch(companion)
                {
                    ReadRows = _ => new SquadState { Rows = players, Total = 4, Indexed = indexed, Today = Today },
                    ReadStaff = _ => (new SquadState { Rows = staff, Total = 3, Indexed = indexed, Today = Today },
                                      new StaffReader.ClubList { Staff = staff, Players = players }),
                    SquadClubPeople = (_, _) => new StaffReader.ClubList { Staff = staff, Players = players },
                    SquadClub = (_, _) => "MK Dons", ShownTeam = (_, _) => "MK Dons", Gap = TimeSpan.Zero,
                    Confirm = (_, _, text, _, _) => { Confirmations.Add(text); return answer == "no" ? "no" : "yes"; },
                    Choose = (title, text, yes, extra, _) => { Choices.Add((title, text, yes, extra)); return answer; },
                    PlayerProfile = (_, _, name, _, _) => new ProfileSnapshot { Type = "player", Name = name },
                    StaffProfile = (_, _, name, job, _) => new ProfileSnapshot { Type = "staff", Name = name, JobTitle = job ?? StaffReader.UnknownJob },
                    StartProgress = text => Made = new Progress(),
                    Generate = (person, profile) =>
                    {
                        Calls.Add((person, profile));
                        return new FaceResult { Ok = true, PlayerId = person, Refreshed = true };
                    },
                };
            }

            public void Dispose() => _dll.Dispose();
        }

        [Fact]
        public void TheSquadConfirmationOffersTheClubsStaff()
        {
            using var batch = new Batch("extra");
            batch.Squad.Run(new FakeProcess());
            var choice = batch.Choices.Single();
            Assert.Equal(("Generate faces?", "Generate 2 faces", "Include staff: 4 faces"), (choice.Title, choice.Yes, choice.Extra));
            Assert.StartsWith("2 of 4 MK Dons players have no portrait. 2 of the club's 3 staff have none either.", choice.Text);
            Assert.Equal(new[] { "3", "4", "12", "13" }, batch.Calls.Select(call => call.Id));
            Assert.Equal(("Physio", StaffReader.UnknownJob), (batch.Calls[2].Profile.JobTitle, batch.Calls[3].Profile.JobTitle));
            Assert.Equal("Generated 4 faces.", batch.Made.Result);
        }

        [Fact]
        public void TheFirstAnswerMakesOnlyTheListsFaces()
        {
            using var batch = new Batch("yes");
            batch.Squad.Run(new FakeProcess());
            Assert.Equal(new[] { "3", "4" }, batch.Calls.Select(call => call.Id));
            Assert.Equal("Generated 2 squad faces.", batch.Made.Result);
            using var none = new Batch("no");
            none.Squad.Run(new FakeProcess());
            Assert.Empty(none.Calls);
        }

        [Fact]
        public void TheStaffCommandOffersTheClubsPlayers()
        {
            using var batch = new Batch("yes");
            batch.Squad.RunStaff(new FakeProcess());
            var choice = batch.Choices.Single();
            Assert.Equal(("Generate 2 faces", "Include players: 4 faces"), (choice.Yes, choice.Extra));
            Assert.StartsWith("2 of 3 MK Dons staff have no portrait. 2 of the club's 4 players have none either.", choice.Text);
            Assert.Equal(new[] { "12", "13" }, batch.Calls.Select(call => call.Id));
            Assert.Equal("Generated 2 staff faces.", batch.Made.Result);
        }

        [Fact]
        public void WhenOnlyTheOtherGroupLacksFacesItAsksAboutThem()
        {
            using var batch = new Batch("yes");
            var only = new List<SquadRow> { new SquadRow("11", 0x2011, "Staff 11", staff: true, job: "Physio") };
            var players = Enumerable.Range(1, 4).Select(i => new SquadRow(i.ToString(), 0x1000 + i, "Player " + i)).ToList();
            batch.Squad.ReadStaff = _ => (new SquadState { Rows = only, Total = 1, Indexed = new HashSet<string> { "1", "2", "11" }, Today = Today },
                                          new StaffReader.ClubList { Staff = only, Players = players });
            batch.Squad.RunStaff(new FakeProcess());
            Assert.Empty(batch.Choices);
            Assert.Contains("All 1 MK Dons staff already have portraits, but 2 of the club's 4 players have none. Generate faces for the 2 missing players?",
                            batch.Confirmations.Single());
            Assert.Equal(new[] { "3", "4" }, batch.Calls.Select(call => call.Id));
        }

        [Fact]
        public void WithoutTheClubsPeopleTheListAloneIsOffered()
        {
            using var batch = new Batch("yes");
            batch.Squad.SquadClubPeople = (_, _) => throw new MemoryUnavailableException("Could not read FM24's person table.");
            batch.Squad.Run(new FakeProcess());
            Assert.Empty(batch.Choices);
            Assert.Contains("2 of 4 MK Dons players have no portrait. Generate faces for the 2 missing players?", batch.Confirmations.Single());
            Assert.Contains(batch.Logged, line => line.Contains("could not read the club's staff"));
            Assert.Equal(new[] { "3", "4" }, batch.Calls.Select(call => call.Id));
        }

        [Fact]
        public void AScreenWithoutAClubSaysWhereToRunIt()
        {
            using var batch = new Batch("yes");
            batch.Squad.ReadStaff = _ => throw new MemoryUnavailableException(StaffReader.NotAClub);
            batch.Squad.RunStaff(new FakeProcess());
            Assert.Empty(batch.Calls);
            Assert.Equal(StaffReader.NotAClub, batch.Notices.Last());
        }
    }
}
