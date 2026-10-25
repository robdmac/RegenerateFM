using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Memory;
using Xunit;

namespace RegenerateFM.Tests
{
    // FM24's memory readers against fake game memory laid out as the Windows
    // fm.exe's (and the Mac build's) records are.
    public class MemoryTests
    {
        public const long Image = 0x140000000;
        public static readonly long PlayerVtable = Layout.Windows.PersonVtables.First(entry => entry.Value == "player").Key;
        public static readonly long StaffVtable = Layout.Windows.PersonVtables.First(entry => entry.Value == "staff").Key;

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        // A player record at ``record``: the person object's class, the ID
        // twice, the dates, a full name, a nation, and position ratings with 20
        // at ``position``.
        public static FakeProcess Player(FakeProcess game, long record, uint id, string name, int position = 7, int recordType = 4,
                                         ushort year = 2023, ushort day = 133, ushort born = 1995)
        {
            game.Put(record - 0x300, new byte[0x400]);  // the person object, around its record
            game.Q(record - 12, Image + PlayerVtable).I(record - 4, 0x0E).I(record, id, id).Put(record + 12, Hex("00025810")).I(record + 16, (uint)recordType);
            var dates = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(dates, year);
            BinaryPrimitives.WriteUInt16LittleEndian(dates.AsSpan(2), day);
            BinaryPrimitives.WriteUInt16LittleEndian(dates.AsSpan(4), born);
            game.Put(record + Layout.Windows.RecordYear, dates);
            game.I(record + Layout.Windows.RecordName, (uint)(record + 0x2000)).Text(record + 0x2000, name);
            var ratings = Enumerable.Repeat((byte)1, 15).ToArray();
            ratings[position] = 20;
            game.Put(record + Layout.Windows.PositionRatings, ratings);
            game.Put(record + Layout.Windows.Attributes, Enumerable.Repeat((byte)60, 54).ToArray());
            game.Put(record + Layout.Windows.Height, new byte[] { 180, 0 });
            // The nation: a db::NATION with its name.
            long nation = record + 0x3000;
            game.I(record + Layout.Windows.Nation, (uint)nation).Q(nation, Image + Layout.Windows.NationVtable)
                .I(nation + 0x30, (uint)(nation + 0x100)).Text(nation + 0x100, "Icelandic");
            game.I(record + Layout.Windows.Contract, 0).I(record + Layout.Windows.PlayingClub, 0);
            return game;
        }

        // A db::TEAM at ``team`` with its club details: (team ID, own name or
        // null, club details ID, club name).
        public static void Team(FakeProcess game, long team, uint id, string own, uint clubId, string club)
        {
            long details = team + 0x400;
            game.Q(team, Image + Layout.Windows.TeamVtable).I(team + 12, id).I(team + 0x30, (uint)details)
                .Q(details, Image + Layout.Windows.ClubVtable).I(details + 12, clubId).I(details + 0xC8, (uint)(details + 0x200))
                .Text(details + 0x200, club).I(team + 0x18, own != null ? (uint)(team + 0x800) : 0);
            if (own != null)
                game.Text(team + 0x800, own);
        }

        public static void Contract(FakeProcess game, long record, long team)
        {
            long contract = record + 0x5000;
            game.I(record + Layout.Windows.Contract, (uint)contract).I(contract + 8, (uint)(record - 12)).I(contract + 0x10, (uint)team);
        }

        [Fact]
        public void ARecordDecodesNameDatesAndKind()
        {
            long record = 0x1000;
            var game = new FakeProcess();
            game.Put(record - 0x100, new byte[0x160]);
            game.Q(record - 12, Image + StaffVtable).I(record, 14008037, 14008037).Put(record + 12, Hex("00025810")).I(record + 16, 0)
                .Put(record + 0x34, new byte[] { 2, 26, 0xE7, 0x07 }).Put(record + 0x38, new byte[] { 133, 0, 0xAC, 0x07 })
                .I(record + 0x3C, 0x1234).Text(0x1234, "Jorge Walter Theiler").Put(0x1234 + 25, new byte[75])
                .Put(record + Layout.Windows.PositionRatings, new byte[15])
                .I(0x2000, 0x2100).Text(0x2100, "Toni").I(0x3000, 0x3100).Text(0x3100, "Micevski")
                .Put(record + 0x40, new byte[0x20]);
            var identity = People.ParseRecord(game, record, 14008037);
            Assert.Equal(("Jorge Walter Theiler", new DateTime(1964, 5, 12), (58, 59), "staff"),
                         (identity.Name, identity.BirthDate, identity.PossibleAges, identity.Kind));
            // Bytes 12-15 are flags that vary from person to person (all seen live).
            foreach (string marker in new[] { "00025010", "00027810", "04027810", "00000000", "00006000", "80000000", "0000000a" })
            {
                game.Put(record + 12, Hex(marker));
                Assert.Equal(identity, People.ParseRecord(game, record, 14008037));
            }
            // The same bytes outside a person object (a copy, a retired person, an official) are not a person.
            foreach (long other in new long[] { 0x5A6EC38, 0x5A6D858, 0x5C7D4F8, -Image })
            {
                game.Q(record - 12, Image + other);
                Assert.Null(People.ParseRecord(game, record, 14008037));
            }
            // Players, including reserve and youth players of types 0 and 1.
            game.Q(record - 12, Image + PlayerVtable).Put(record + Layout.Windows.PositionRatings, new byte[] { 1, 1, 15, 1, 1, 1, 1, 20, 1, 1, 1, 1, 1, 1, 1 });
            foreach (uint type in new uint[] { 0, 1, 4 })
            {
                game.I(record + 16, type);
                Assert.Equal("player", People.ParseRecord(game, record, 14008037).Kind);
            }
            game.Put(record + Layout.Windows.PositionRatings, new byte[15]).I(record + 16, 1);
            Assert.Null(People.ParseRecord(game, record, 14008037));  // a type 1 player without ratings
            // No full name (60% of players): "first surname" from the name objects.
            game.I(record + 16, 4).I(record + 0x3C, 0).I(record + 0x4C, 0x2000).I(record + 0x54, 0x3000);
            Assert.Equal("Toni Micevski", People.ParseRecord(game, record, 14008037).Name);
            game.I(record + 0x4C, 0);  // one name only (Nur'alim): the surname
            Assert.Equal("Micevski", People.ParseRecord(game, record, 14008037).Name);
            game.I(record + 0x54, 0);
            Assert.Null(People.ParseRecord(game, record, 14008037));
            // A full name with a non-breaking space (Oluwagbemileke Immanuel\u00a0Ajibode).
            game.I(record + 0x3C, 0x1234).Text(0x1234, "Immanuel\u00a0Ajibode");
            Assert.Equal("Immanuel Ajibode", People.ParseRecord(game, record, 14008037).Name);
            // An unusable full name (Hugo Videla's pointed at "v"): the name objects.
            game.I(0x1234, 1).I(record + 0x4C, 0x2000).I(record + 0x54, 0x3000);
            Assert.Equal("Toni Micevski", People.ParseRecord(game, record, 14008037).Name);
        }

        [Fact]
        public void AMacRecordsNameComesFromItsNameObjects()
        {
            var mac = Layout.MacOS;
            long image = 0x100000000, record = 0x10000;
            var game = new FakeProcess(mac, image, zeros: true);
            game.Q(record - 12, image + 0x51E6FD0).I(record - 4, 7).I(record, 29190272, 29190272).I(record + 16, 4)
                .Put(record + mac.RecordYear, new byte[] { 0xE7, 0x07, 174, 0, 0xCB, 0x07 })
                .Q(record + mac.RecordName, 0).Q(record + mac.FirstNamePart, 0x20000).Q(record + mac.SurnamePart, 0x30000)
                .Q(0x20000, 0x21000).Text(0x21000, "Lewis").Q(0x30000, 0x31000).Text(0x31000, "Wing");
            var identity = People.ParseRecord(game, record, 29190272);
            Assert.Equal(("Lewis Wing", "player", new DateTime(1995, 6, 23)), (identity.Name, identity.Kind, identity.BirthDate));
        }

        [Fact]
        public void RatingOffsetsMatchTwoLiveProfiles()
        {
            // These bytes came from two FM24 24.4.2 game records. Expected values
            // were read independently from the corresponding in-game overviews.
            var luis = Hex("4b4b5a414b285550502d4b050a0f0f0a0a504b0f280a5a5041645a4b412d28050a0f2d50463232374b412d5a4b552d50233750645f41");
            var cj = Hex("0a0f05290a053233050f29424233333d383d3829334729292364320f33420a32373c381e4233334228283d32322d423c3232373c2e33");
            Assert.Equal(new Dictionary<string, object>
            {
                ["Acceleration"] = 9, ["Agility"] = 9, ["Balance"] = 9, ["Jumping Reach"] = 11, ["Natural Fitness"] = 16,
                ["Pace"] = 10, ["Stamina"] = 10, ["Strength"] = 14,
            }, ProfileReader.Attributes(luis, ProfileReader.Physical));
            Assert.Equal(16, ProfileReader.Attributes(luis, ProfileReader.Technical)["Penalty Taking"]);
            Assert.Equal(20, ProfileReader.Attributes(luis, ProfileReader.Mental)["Determination"]);
            Assert.Equal(14, ProfileReader.Attributes(cj, ProfileReader.Goalkeeping)["Reflexes"]);
            Assert.Equal(13, ProfileReader.Attributes(cj, ProfileReader.Goalkeeping)["Handling"]);
            Assert.Equal(13, ProfileReader.Attributes(cj, ProfileReader.Physical)["Strength"]);
            // Stored 1-100, shown 1-20; raw 1 and 2 show as 1.
            var raw = new byte[54];
            foreach (var (stored, shown) in new[] { (1, 1), (2, 1), (3, 1), (8, 2), (100, 20) })
            {
                raw[34] = (byte)stored;
                Array.Fill(raw, (byte)stored);
                Assert.Equal(shown, ProfileReader.Attributes(raw, ProfileReader.Physical)["Acceleration"]);
            }
            foreach (byte stored in new byte[] { 0, 101 })
            {
                Array.Fill(raw, stored);
                Assert.Throws<MemoryUnavailableException>(() => ProfileReader.Attributes(raw, ProfileReader.Physical));
            }
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
        private static byte[] U32(uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            return data;
        }

        [Fact]
        public void TheUiScanKeepsOnlyLiveTitleTexts()
        {
            var active = Encoding.ASCII.GetBytes("Goalkeeping Coach - Inter Miami (ID:3100693)");
            var stale = Encoding.ASCII.GetBytes("Defender (Center) - Inter Miami (ID:2000155624)");
            var data = Concat(new byte[20], U32(53), U32(0), U32(1), U32((uint)active.Length), active, new byte[1],
                              U32(53), U32(0), U32(0), U32((uint)stale.Length), stale,
                              Encoding.ASCII.GetBytes("\0 43 years old\0 24 years old\0"));
            var titles = new Dictionary<string, List<string>>();
            var ages = new HashSet<int>();
            ProfileReader.CollectUiChunk(data, titles, ages);
            Assert.Equal(new[] { "3100693" }, titles.Keys);
            Assert.Equal(new[] { "Goalkeeping Coach - Inter Miami" }, titles["3100693"]);
            Assert.Equal(new HashSet<int> { 43, 24 }, ages);
        }

        [Fact]
        public void AWidgetWithMoreChildrenThanOneReadHolds()
        {
            // Lionel Messi's FM24 profile has a widget with more than 512 children,
            // and the process readers refuse reads over 4 KB.
            var game = new FakeProcess();
            game.Q(0x1000 + Widgets.Children, 0x10000, 0x10000 + 8 * 600).Q(0x10000, Enumerable.Range(1, 600).Select(i => (long)i).ToArray());
            Assert.Equal(Enumerable.Range(1, 600).Select(i => (long)i), Widgets.ChildrenOf(game, 0x1000));
        }

        [Fact]
        public void TodayIsTheMostCommonDateStringAndOnlyFmStringsCount()
        {
            Assert.Equal(new DateTime(2023, 1, 2), SquadReader.Today(new Dictionary<string, int> { ["2 Jan 2023"] = 6, ["1 Jan 2023"] = 1 }));
            foreach (var dates in new[]
                     {
                         new Dictionary<string, int>(), new Dictionary<string, int> { ["2 Jan 2023"] = 1 },
                         new Dictionary<string, int> { ["2 Jan 2023"] = 3, ["9 Mar 2023"] = 2 },
                     })
                Assert.Throws<MemoryUnavailableException>(() => SquadReader.Today(dates));
            byte[] Fm(string text) => Concat(U32((uint)text.Length), Encoding.ASCII.GetBytes(text), new byte[1]);
            var data = Concat(Encoding.ASCII.GetBytes("xx"), Fm("2 Jan 2023"), Encoding.ASCII.GetBytes("yy"), Fm("12 Feb 2024"),
                              Encoding.ASCII.GetBytes("2 Jan 2023\0"), Fm("2 Foo 2023"), Encoding.ASCII.GetBytes(" 2023 tail"));
            var counted = new Dictionary<string, int>();
            SquadReader.CountDates(data, counted);
            Assert.Equal(new Dictionary<string, int> { ["2 Jan 2023"] = 1, ["12 Feb 2024"] = 1 }, counted);
        }

        [Fact]
        public void AlternateNameRecordsDecodeAndCorruptOnesAreRefused()
        {
            // Live bytes of Tyler Hall's name record (ID twice, 09, Tyler, Hall).
            var tyler = Concat(People.IdTwice(2000251984), new byte[] { 9 }, U32(5), Encoding.ASCII.GetBytes("Tyler"), U32(4),
                               Encoding.ASCII.GetBytes("Hall\0"), new byte[30]);
            Assert.Equal("Tyler Hall", ProfileReader.AlternateName(tyler));
            // Gabriel Pec's: first, surname, common name.
            var pec = Concat(People.IdTwice(19397595), new byte[] { 9 }, U32(7), Encoding.ASCII.GetBytes("Gabriel"), U32(13),
                             Encoding.ASCII.GetBytes("Fortes Chaves"), U32(11), Encoding.ASCII.GetBytes("Gabriel Pec"),
                             new byte[] { 1, 0, 2, (byte)'X', 0x10, 4 }, new byte[30]);
            Assert.Equal("Gabriel Fortes Chaves", ProfileReader.AlternateName(pec));
            var corrupt = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(pec).Replace("Gabriel Pec", "Gabriel\x11Pec"));
            Assert.Null(ProfileReader.AlternateName(corrupt));
            var start = Concat(People.IdTwice(7), new byte[] { 9 }, U32(6), Encoding.ASCII.GetBytes("Lawson"));
            foreach (var tail in new[]
                     {
                         Concat(U32(9), Encoding.ASCII.GetBytes("Sunde\0\0\0\x11\0")),   // control bytes
                         Concat(U32(10), Encoding.ASCII.GetBytes("Sunderland")),          // no terminator
                         Concat(U32(0), new byte[1]),                                     // empty surname
                     })
                Assert.Null(ProfileReader.AlternateName(Concat(start, tail, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
        }

        [Fact]
        public void ABiographysFirstParagraphKeepsOnlyLinkText()
        {
            var link = Concat(new byte[] { 1 }, Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.ASCII.GetBytes("Click to view Lionel Messi's profile"))), new byte[] { 2 });
            var raw = Concat(new byte[] { 1 }, link, Encoding.ASCII.GetBytes("Lionel Messi\x01 is a  legend.\n\nLater paragraph.\0junk"));
            Assert.Equal("Lionel Messi is a legend.", ScreenReader.FirstParagraph(raw));
            Assert.Equal("", ScreenReader.FirstParagraph(Array.Empty<byte>()));
            Assert.Throws<MemoryUnavailableException>(() => ScreenReader.FirstParagraph(Concat(Encoding.ASCII.GetBytes("bad "), new byte[] { 0xFF },
                                                                                               Encoding.ASCII.GetBytes(" text"))));
        }

        [Fact]
        public void BriefRecordsArePlayersFm24KeepsTooLittleAbout()
        {
            uint id = 2002070373;
            string PlayerIdOf(long vtable, string marker = "00025810", uint first = 2002070373, uint second = 2002070373)
            {
                var game = new FakeProcess(zeros: true);
                game.Q(0x1000, Image + vtable).I(0x100C, first, second).Put(0x100C + 12, Hex(marker)).I(0x100C + 16, 4);
                var ratings = Enumerable.Repeat((byte)1, 15).ToArray();
                ratings[7] = 20;
                game.Put(0x100C + Layout.Windows.PositionRatings, ratings);
                return SquadReader.PlayerId(game, 0x1000);
            }
            // "Scouting Required" rows (seen on Inter Miami II and its academy) are db::VIRTUAL_PLAYER.
            foreach (string marker in new[] { "00020010", "08024030", "00025810" })
                Assert.Equal(id.ToString(), Assert.Throws<BriefRecordException>(() => PlayerIdOf(Layout.Windows.BriefVtables.First(), marker)).PersonId);
            // A full player, whatever its flag bytes.
            foreach (string marker in new[] { "00007800", "00006000", "80000000" })
                Assert.Equal(id.ToString(), PlayerIdOf(PlayerVtable, marker));
            // Not a person record at all, or not a player's.
            Assert.False(Assert.Throws<NotAPlayerException>(() => PlayerIdOf(PlayerVtable, first: 5, second: 6)).Coach);
            Assert.False(Assert.Throws<NotAPlayerException>(() => PlayerIdOf(0x5A6D858, first: 7, second: 7)).Coach);
            // A player-coach: a person, but not one a squad list can describe.
            Assert.True(Assert.Throws<NotAPlayerException>(() => PlayerIdOf(Layout.Windows.PlayerCoachVtables.First())).Coach);
        }

        [Fact]
        public void AFreeAgentIsMadeInAPlainShirt()
        {
            long record = 0x10000;
            var game = Player(new FakeProcess(), record, 28004396, "Gylfi Thor Sigurdsson", position: 10, born: 1989);
            Assert.Throws<NoContractException>(() => ProfileReader.Club(game, record));
            var identity = new PersonIdentity(28004396, "Gylfi Thor Sigurdsson", new DateTime(1989, 9, 8), 2023, "player");
            // His header names his national team, not a club.
            var profile = ProfileReader.Profile(game, record, identity, new List<string> { "Attacking Midfielder (Center) - Iceland" }, 33, false);
            Assert.Equal(("Free agent", "Attacking Midfielder (Center)", "Icelandic", "5'11\""),
                         (profile.Club, profile.Position, profile.Nationality, profile.Height));
            string prompt = Prompts.ProfilePrompt(profile, null, null);
            Assert.Contains("who is a free agent", prompt);
            Assert.Contains("plain unbranded football shirt", prompt);
        }

        [Fact]
        public void ALoanedPlayerWearsTheLoanClubsKitAndOtherTeamsAreNamedAsFm24NamesThem()
        {
            long record = 0x10000, parent = 0x40000, loan = 0x50000;
            var game = Player(new FakeProcess(), record, 1, "Test Player", position: 3);
            Team(game, parent, 77, null, 77, "Inter Miami");
            Team(game, loan, 88, null, 88, "Las Vegas");
            Contract(game, record, parent);
            var identity = new PersonIdentity(1, "Test Player", new DateTime(1995, 5, 13), 2023, "player");
            ProfileSnapshot Read(string title) => ProfileReader.Profile(game, record, identity, new List<string> { title }, 28, false);
            Assert.Equal("Inter Miami", Read("Defender (Center) - Inter Miami").Club);
            game.I(record + Layout.Windows.PlayingClub, (uint)loan);
            Assert.Equal("Las Vegas", Read("Defender (Center) - Inter Miami").Club);
            // Reading Under 21s (2026-10-19): an FM-made ID and no name of its own: its club's name.
            Team(game, loan, 2_000_339_974, null, 702, "Reading");
            Team(game, parent, 702, null, 702, "Reading");
            Assert.Equal("Reading", Read("Defender (Center) - Reading").Club);
            // Jong Sparta Rotterdam: its own name, Sparta's club details.
            Team(game, loan, 37_060_577, "Jong Sparta Rotterdam", 702, "Reading");
            Assert.Equal("Jong Sparta Rotterdam", Read("Defender (Center) - Jong Sparta Rotterdam").Club);
            // A youth team FM names only on screen.
            game.I(record + Layout.Windows.PlayingClub, 0);
            Assert.Equal("Reading Under 21s", Read("Defender/Wing Back (Center) - Reading Under 21s").Club);
            Assert.Throws<MemoryUnavailableException>(() => Read("Defender (Center) - Readingham Under 21s"));
            // A club object of the wrong class.
            game.Q(parent, Image + Layout.Windows.ClubVtable);
            Assert.Throws<MemoryUnavailableException>(() => ProfileReader.Club(game, record));
        }

        [Fact]
        public void ARecordIsFoundOnceAndReusedUntilItsHeaderChanges()
        {
            long record = 0x10000;
            var game = Player(new FakeProcess(), record, 7458500, "Lionel Messi");
            var first = ProfileReader.PersonRecord(game, "7458500");
            Assert.Equal(record, first.Address);
            int scans = game.Scans;
            Assert.Equal(first, ProfileReader.PersonRecord(game, "7458500"));
            Assert.Equal(scans, game.Scans);  // no memory search
            game.Put(record + 0x20, new byte[] { 0xEE });  // moved or replaced: search again
            ProfileReader.PersonRecord(game, "7458500");
            Assert.True(game.Scans > scans);
        }

        [Fact]
        public void TheDateAndAgesAreTheScreensOwnTexts()
        {
            var game = new FakeProcess(zeros: true);
            var texts = new[] { (0x100L, 0x1000L, "16 Sep 2023"), (0x200, 0x2000, "16 Sep 2023"), (0x300, 0x3000, "31 Feb 2023"),
                                (0x400, 0x4000, "28 years old"), (0x500, 0x5000, "Overview"), (0x600, 0x6000, "5 years old ago") };
            foreach (var (widget, text, value) in texts)
                game.Q(widget + ScreenReader.WidgetText, text).I(text, (uint)value.Length).Put(text + 4, Encoding.ASCII.GetBytes(value));
            int dtam = Widgets.SkinId("dtam");
            Assert.Equal(new DateTime(2023, 9, 16), ScreenReader.Date(game, new Dictionary<int, List<long>> { [dtam] = new List<long> { 0x100, 0x200 } }));
            Assert.Null(ScreenReader.Date(game, new Dictionary<int, List<long>> { [dtam] = new List<long> { 0x300 } }));
            Assert.Null(ScreenReader.Date(game, new Dictionary<int, List<long>>()));
            Assert.Equal(new HashSet<int> { 28 }, ScreenReader.Ages(game, new Dictionary<int, List<long>>
            {
                [1] = new List<long> { 0x400, 0x500 }, [2] = new List<long> { 0x600, 0x700 },
            }));
        }

        [Fact]
        public void TheAgeIsCheckedAgainstTheDateShownNotTheRecordsYear()
        {
            // Scott McGowan, born 23 Jul 2007: his record's year is 2022, the season
            // before, so it allows 14 or 15, but on 16 Sep 2023 he is 16.
            var identity = new PersonIdentity(2002067938, "Scott McGowan", new DateTime(2007, 7, 23), 2022, "player");
            Assert.Equal((14, 15), identity.PossibleAges);
            Assert.Equal(16, identity.AgeOn(new DateTime(2023, 9, 16)));
            Assert.Equal(15, identity.AgeOn(new DateTime(2023, 7, 22)));
            long record = 0x10000;
            var game = Player(new FakeProcess(), record, 2002067938, "Scott McGowan", position: 8, year: 2022, day: 204, born: 2007);
            Team(game, 0x40000, 702, null, 702, "MK Dons");
            Contract(game, record, 0x40000);
            var screen = new OpenScreen
            {
                PersonId = "2002067938", Title = "Midfielder (Right) - MK Dons Under 18s", Record = record,
                Ages = new HashSet<int> { 16 }, Today = new DateTime(2023, 9, 16),
            };
            var (person, profile) = ProfileReader.ReadOpenProfile(game, screen: screen);
            Assert.Equal(("2002067938", 16, "MK Dons Under 18s"), (person, profile.Age, profile.Club));
            Assert.Equal(0, game.Scans);  // the screen gave the record and age: no memory search
            screen.Today = null;
            Assert.Contains("age", Assert.Throws<MemoryUnavailableException>(() => ProfileReader.ReadOpenProfile(game, screen: screen)).Message);
        }

        [Fact]
        public void OnlyPersonReferencesHaveAHandle()
        {
            Assert.Equal(0xC0E, PersonHandles.Handle(0x01000C0E));
            // The people vector's first entry is a person too, so handle 0 is valid.
            Assert.Equal(0, PersonHandles.Handle(0x01000000));
            foreach (uint reference in new uint[] { 0x18000CB8, 0x017FFFFF, 0 })
                Assert.Throws<ArgumentException>(() => PersonHandles.Handle(reference));
        }

        [Fact]
        public void ThePersonTableGivesThePersonAndRecordInTwoReads()
        {
            long image = 0x100000000, database = 0x7000, decoy = 0x8000, table = 0x9000, other = 0xB000, person = 0xA000;
            var layout = new Layout
            {
                PersonVtables = Layout.Windows.PersonVtables, PersonTableGlobal = 0x50, PersonTableSlot = 0x18, Pointer = 8,
                BrowserReference = 0x878, PositionRatings = Layout.Windows.PositionRatings,
            };
            var game = new FakeProcess(layout, image, zeros: true);
            game.Q(image + 0x50, database).Q(database + 0x18, table, table + 8 * 0x20).Q(table + 8 * 0x0E, person)
                .Q(person, image + PlayerVtable).I(person + 8, 0x0E, 4000829, 4000829);
            PersonHandles.ScanPass = _ => throw new InvalidOperationException("no memory pass");
            try
            {
                Assert.Equal("4000829", PersonHandles.PersonForReference(game, 0x0100000E, wait: false));
                Assert.Equal(("4000829", person + 12), PersonHandles.PersonRecordForReference(game, 0x0100000E));
                Assert.Equal(table, PersonHandles.PeopleVector(game, 0x0E));
                // Outside the table, or an empty slot: the lookup falls back to the index.
                Assert.Null(PersonHandles.TableRecord(game, 0x40));
                Assert.Null(PersonHandles.TableRecord(game, 0x0F));
                // The layout's slot now holds another kind of object, also indexed by handle.
                var moved = new FakeProcess(layout, image, zeros: true, pid: 2);
                moved.Q(image + 0x50, database).Q(database + 0x18, decoy, decoy + 8 * 0x20).Q(decoy + 8 * 0x0E, other)
                     .Q(other, image + 0x5A6D858).I(other + 8, 0x0E, 1234, 1234)  // an official
                     .Q(database + 0x98, table, table + 8 * 0x20).Q(table + 8 * 0x0E, person)
                     .Q(person, image + PlayerVtable).I(person + 8, 0x0E, 4000829, 4000829);
                Assert.Equal("4000829", PersonHandles.TableRecord(moved, 0x0E)?.PersonId);
                Assert.Equal(0x98, moved.PersonTableSlot);
            }
            finally
            {
                PersonHandles.ScanPass = PersonHandles.ScanObjects;
            }
        }

        [Fact]
        public void TheHandleIndexFindsPersonObjectsByClass()
        {
            long image = 0x140000000, heap = 0x30000000;
            var game = new FakeProcess(image: image);
            var objects = new (long Offset, long Vtable, uint Handle, uint Person)[]
            {
                (0x000, PlayerVtable, 0x0E, 4000829), (0x100, 0x5A6D858, 0x0F, 77),  // an official
                (0x200, PlayerVtable, 0x10, 0), (0x304, PlayerVtable, 0x11, 5),       // empty, misaligned
            };
            foreach (var (offset, vtable, handle, person) in objects)
                game.Q(heap + offset, image + vtable).I(heap + offset + 8, handle, person, person);
            game.Put(heap + 0x3FF, new byte[1]);
            Assert.Equal(new Dictionary<int, long> { [0x0E] = heap + 12 }, PersonHandles.ScanObjects(game));
        }

        [Fact]
        public void TheScreenReferenceIsTheSidebarsWhichSurvivesASkinReload()
        {
            long image = 0x100000000, sidebar = 0x5000, browser = 0x6000;
            var layout = new Layout { SidebarVtable = 0x40, SidebarReference = 0x278, ObjectBrowserVtable = 0x80, BrowserReference = 0x878 };
            var game = new FakeProcess(layout, image, zeros: true);
            game.Q(sidebar, image + 0x40).I(sidebar + 0x278, 0x01000ED8).Q(browser, image + 0x80).I(browser + 0x878, 0x78008020);
            var widgets = new Dictionary<int, List<long>> { [Widgets.SkinId("side")] = new List<long> { sidebar }, [Widgets.SkinId("NULL")] = new List<long> { browser } };
            // After a reload the browser holds something else; the sidebar still has the person.
            Assert.Equal(0x01000ED8u, PersonHandles.ScreenReference(game, 0, widgets));
            // A sidebar showing a club falls back to the browser.
            game.I(sidebar + 0x278, 0x18000CB8).I(browser + 0x878, 0x01001BAD);
            Assert.Equal(0x01001BADu, PersonHandles.ScreenReference(game, 0, widgets));
        }

        [Fact]
        public void AClickWaitsForTheRunningPassInsteadOfStartingAnother()
        {
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            int calls = 0;
            PersonHandles.ScanPass = _ =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                release.Wait(2000);
                return new Dictionary<int, long>();
            };
            try
            {
                var game = new FakeProcess(pid: 99);
                var first = new Thread(() => PersonHandles.Build(game));
                first.Start();
                Assert.True(started.Wait(2000));
                var second = new Thread(() => PersonHandles.Build(game));
                second.Start();
                Thread.Sleep(100);
                release.Set();
                first.Join(2000);
                second.Join(2000);
            }
            finally
            {
                PersonHandles.ScanPass = PersonHandles.ScanObjects;
            }
            Assert.Equal(1, calls);
        }

        [Fact]
        public unsafe void ThisSystemsReaderReadsAProcessWithoutChangingIt()
        {
            // macOS reads only FM24, through RegenerateFM's library inside it.
            if (OperatingSystem.IsMacOS())
                return;
            var sample = Encoding.ASCII.GetBytes("FM24 probe, read from outside");
            fixed (byte* address = sample)
            {
                using GameProcess self = OperatingSystem.IsWindows() ? new WindowsProcess(Environment.ProcessId)
                                                                     : new LinuxProcess(Environment.ProcessId);
                long at = (long)address;
                Assert.Equal(sample, self.Read(at, sample.Length));
                Assert.Contains(self.WritableRegions(), region => region.Start <= at && at < region.End);
                Assert.Throws<InvalidDataException>(() => self.ImageBase);  // this process isn't FM24
            }
            Assert.Contains(Processes.Running(), process => process.Pid == Environment.ProcessId);
        }

        [Fact]
        public void FindingTheGameSaysWhyItCant()
        {
            var processes = new List<(int, string)>();
            var unreadable = new HashSet<int>();
            Processes.RunningSource = () => processes;
            Processes.Opener = pid => unreadable.Contains(pid) ? throw new IOException("no answer") : new FakeProcess(pid: pid);
            try
            {
                // The game's executable on this system (fm.app's on the Mac).
                string executable = OperatingSystem.IsMacOS() ? "/fm.app/Contents/MacOS/fm" : "/fm.exe";
                string game = "C:/Games/Football Manager 2024" + executable;
                processes.AddRange(new[] { (7, "/bin/sh"), (9, game), (10, "C:/Games/Football Manager 26" + executable) });
                Assert.Equal(9, People.FindGame().Pid);
                processes.RemoveAll(entry => entry.Item1 == 9);
                Assert.Contains("isn't running. Start it", Assert.Throws<MemoryUnavailableException>(People.FindGame).Message);
                processes.AddRange(new[] { (8, game), (9, game) });
                Assert.Contains("More than one FM24", Assert.Throws<MemoryUnavailableException>(People.FindGame).Message);
                processes.RemoveAll(entry => entry.Item1 == 8);
                unreadable.Add(9);
                // On the Mac the reads go through the library in the game, so that's what isn't answering.
                Assert.Contains(OperatingSystem.IsMacOS() ? "its library in the game isn't answering" : "can't read the running FM24 (no answer)",
                                Assert.Throws<MemoryUnavailableException>(People.FindGame).Message);
            }
            finally
            {
                Processes.RunningSource = null;
                Processes.Opener = null;
            }
        }
    }
}
