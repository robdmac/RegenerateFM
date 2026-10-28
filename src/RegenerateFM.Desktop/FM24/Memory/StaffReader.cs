using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // A club's staff and players: everyone contracted to one of its teams,
    // from FM24's people table. The staff command's club is the open screen's
    // (a club's Staff screen); a squad's is the club most of its players are
    // contracted to.
    public static class StaffReader
    {
        public const string UnknownJob = "member of staff";
        public static string NotAClub =>
            Text.T("Generate missing staff faces works on a club's Staff screen, and RegenerateFM couldn't tell which club this screen shows. Open the club's Staff screen and choose it from its menu there.");
        // FM24 refers to a club (its db::CLUB details) as 3 << 24 | its index in
        // the database's vector of clubs; the object keeps its index at +8.
        private const int ClubType = 3;
        private const int IndexMask = 0x7FFFFF;

        // FM24's job codes (the low byte of a contract's job; the lowest bit set
        // marks a player-coach's version of the job, and the high byte can carry
        // more: 0xE26 for Orlando City's Chief Doctor), with the text FM24's Staff
        // table shows for them, read on 2026-10-26 from MK Dons' and Chelsea's
        // (Owner and President on 2026-10-27, from Inter Miami's on Windows/Proton).
        private static readonly Dictionary<int, string> Jobs = new Dictionary<int, string>
        {
            [0x02] = "Coach",
            [0x04] = "Chairperson",
            [0x06] = "Director",
            [0x0A] = "Director of Football",
            [0x0C] = "Physio",
            [0x0E] = "Scout",
            [0x10] = "Manager",
            [0x14] = "Assistant Manager",
            [0x16] = "Set Piece Coach",
            [0x1A] = "Fitness Coach",
            [0x22] = "Goalkeeping Coach",
            [0x26] = "Chief Doctor",
            [0x28] = "Head of Sports Science",
            [0x2C] = "Chief Scout",
            [0x2E] = "Doctor",
            [0x30] = "Sports Scientist",
            [0x32] = "Head Physio",
            [0x3A] = "Recruitment Analyst",
            [0x3C] = "Performance Analyst",
            [0x3E] = "Head Performance Analyst",
            [0x40] = "Head of Youth Development",
            [0x42] = "Owner",
            [0x46] = "President",
            [0x56] = "Loan Manager",
            [0x58] = "Technical Director",
        };

        public static string JobTitle(int code) => Jobs.TryGetValue(code & 0xFE, out string job) ? job : null;

        // The person's ID if they are staff RegenerateFM can describe (a
        // player-coach included); NotAPlayerException for anyone else.
        internal static string StaffId(GameProcess game, long person)
        {
            long record = person + 12;
            var raw = game.Read(record, 20);
            uint number = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (!raw.AsSpan(0, 8).SequenceEqual(People.IdTwice(number)) || number < 1 || number > 2_147_483_647 ||
                !People.IsPersonRecord(game, record) || People.PersonKind(game, record, raw[16]) != "staff")
                throw new NotAPlayerException();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // The club details the open screen shows (a club's Staff screen refers
        // to its club).
        public static long ScreenClub(GameProcess game)
        {
            try
            {
                long manager = Widgets.UiManager(game);
                var widgets = ScreenReader.Tree(game, manager, game.U64(manager + ScreenReader.ScreenRoot));
                uint reference = PersonHandles.ScreenReference(game, manager, widgets, person: false);
                if (reference >> 24 != ClubType)
                    throw new MemoryUnavailableException(NotAClub);
                return ClubAt(game, (int)(reference & IndexMask));
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                throw new MemoryUnavailableException("Could not read the open FM24 screen: " + error.Message);
            }
        }

        // The club details at ``index`` in the database's vector of clubs, which
        // is found like the people's: the one whose entry there is a club
        // carrying that index.
        public static long ClubAt(GameProcess game, int index)
        {
            try
            {
                long database = game.U64(game.ImageBase + game.Layout.PersonTableGlobal);
                var slots = new byte[PersonHandles.DatabaseScan + 8];
                int read = game.ReadInto(database, slots);
                var found = new HashSet<long>();
                for (int slot = 0; slot + 16 <= read; slot += 8)
                {
                    long begin = BinaryPrimitives.ReadInt64LittleEndian(slots.AsSpan(slot));
                    long end = BinaryPrimitives.ReadInt64LittleEndian(slots.AsSpan(slot + 8));
                    if (begin == 0 || end <= begin || (end - begin) % 8 != 0 || index >= (end - begin) / 8)
                        continue;
                    try
                    {
                        long club = game.U64(begin + 8L * index);
                        if (club != 0 && game.U64(club) == game.ImageBase + game.Layout.ClubVtable && game.U32(club + 8) == index)
                            found.Add(club);
                    }
                    catch (Exception error) when (People.Unreadable(error))
                    {
                    }
                }
                if (found.Count != 1)
                    throw new MemoryUnavailableException(NotAClub);
                return found.First();
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                throw new MemoryUnavailableException("Could not read FM24's clubs: " + error.Message);
            }
        }

        // The club most of these people are contracted to; 0 if none is.
        public static long MostCommonClub(GameProcess game, IEnumerable<long> records)
        {
            var clubs = new Dictionary<long, int>();
            int count = 0;
            foreach (long record in records)
            {
                count++;
                try
                {
                    long club = ProfileReader.ContractClub(game, record);
                    clubs[club] = clubs.GetValueOrDefault(club) + 1;
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                }
            }
            if (clubs.Count == 0)
                return 0;
            var best = clubs.OrderByDescending(entry => entry.Value).First();
            return best.Value * 2 > count ? best.Key : 0;
        }

        // A club's people: staff (with their contract's job, or null) and
        // players, and the IDs of those whose name couldn't be read.
        public sealed class ClubList
        {
            public List<SquadRow> Staff = new List<SquadRow>(), Players = new List<SquadRow>();
            public List<string> UnnamedStaff = new List<string>(), UnnamedPlayers = new List<string>();
        }

        // Replaced by tests: the names of people without a list row.
        internal static Func<GameProcess, List<(string Id, long Record)>, Dictionary<string, string>> Names =
            (game, rows) => SquadReader.Names(game, rows);

        // [address + from, address + to) for each address, reading nearby
        // addresses together: FM24 keeps people (and contracts) close together,
        // and on the Mac every read is a request to the game. A window that
        // doesn't read whole is read an address at a time; an address that
        // can't be read is left out.
        private const int Window = 1 << 20;

        private static Dictionary<long, byte[]> ReadAround(GameProcess game, IEnumerable<long> addresses, int from, int to)
        {
            var result = new Dictionary<long, byte[]>();
            var sorted = addresses.Distinct().OrderBy(address => address).ToList();
            int size = to - from;
            for (int first = 0; first < sorted.Count;)
            {
                long start = sorted[first] + from;
                int end = first + 1;
                while (end < sorted.Count && sorted[end] + to - start <= Window)
                    end++;
                var buffer = new byte[sorted[end - 1] + to - start];
                int read;
                try
                {
                    read = game.ReadInto(start, buffer);
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                    read = 0;
                }
                for (int i = first; i < end; i++)
                {
                    long offset = sorted[i] + from - start;
                    if (offset + size <= read)
                    {
                        result[sorted[i]] = buffer.AsSpan((int)offset, size).ToArray();
                        continue;
                    }
                    try
                    {
                        var one = new byte[size];
                        if (game.ReadInto(sorted[i] + from, one) == size)
                            result[sorted[i]] = one;
                    }
                    catch (Exception error) when (People.Unreadable(error))
                    {
                    }
                }
                first = end;
            }
            return result;
        }

        private static long PointerAt(GameProcess game, byte[] data, int offset) =>
            game.Layout.Pointer == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))
                                     : BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));

        // Everyone contracted to one of the club's teams (``club`` is its club
        // details), from FM24's people table: each person's class and contract,
        // then each contract's team, and each team's club once. Only those at
        // the club are checked in full.
        public static ClubList ClubPeople(GameProcess game, long club)
        {
            // Handle 0, the table's first person, finds the table.
            var range = PersonHandles.PeopleRange(game, 0);
            if (range == null || range.Value.Count < 1 || range.Value.Count > 1_000_000)
                throw new MemoryUnavailableException("Could not read FM24's person table.");
            var (begin, total) = range.Value;
            var vector = new byte[8L * total];
            for (int start = 0; start < vector.Length; start += GameProcess.MaxRead)
                if (game.ReadInto(begin + start, vector.AsSpan(start, Math.Min(GameProcess.MaxRead, vector.Length - start))) <= 0)
                    throw new MemoryUnavailableException("Could not read FM24's person table.");
            var kinds = new Dictionary<long, bool>();  // class -> staff
            foreach (var (vtable, kind) in game.Layout.PersonVtables)
                kinds[game.ImageBase + vtable] = kind == "staff";
            var people = new List<long>();
            for (int i = 0; i < total; i++)
            {
                long person = BinaryPrimitives.ReadInt64LittleEndian(vector.AsSpan(8 * i));
                if (person > 0)
                    people.Add(person);
            }
            // The person object's class (its start) and contract pointer (in its record, 12 bytes in).
            int contractAt = 12 + game.Layout.Contract;
            int from = Math.Min(0, contractAt), to = Math.Max(8, contractAt + game.Layout.Pointer);
            var contracts = new List<(long Person, bool Staff, long Contract)>();
            foreach (var (person, data) in ReadAround(game, people, from, to))
            {
                if (!kinds.TryGetValue(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(-from)), out bool staff))
                    continue;
                long contract = PointerAt(game, data, contractAt - from);
                if (contract > 0)
                    contracts.Add((person, staff, contract));
            }
            var teams = ReadAround(game, contracts.Select(entry => entry.Contract), 0x10, 0x10 + game.Layout.Pointer);
            var clubs = new Dictionary<long, long>();  // team -> its club details
            var found = new List<(string Id, long Record, bool Staff)>();
            foreach (var (person, staff, contract) in contracts)
            {
                if (!teams.TryGetValue(contract, out var data))
                    continue;
                long team = PointerAt(game, data, 0);
                if (team <= 0)
                    continue;
                try
                {
                    if (!clubs.TryGetValue(team, out long details))
                        clubs[team] = details = game.Pointer(team + 0x30);
                    long record = person + 12;
                    if (details != club || ProfileReader.ContractClub(game, record) != club)
                        continue;
                    found.Add((staff ? StaffId(game, person) : SquadReader.PlayerId(game, person), record, staff));
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                    // Not a person RegenerateFM can describe, or one that moved.
                    clubs.TryAdd(team, 0);
                }
            }
            found.Sort((a, b) => string.CompareOrdinal(a.Id.PadLeft(10, '0'), b.Id.PadLeft(10, '0')));
            var names = Names(game, found.Select(person => (person.Id, person.Record)).ToList());
            var list = new ClubList();
            foreach (var (personId, record, staff) in found)
            {
                if (!names.TryGetValue(personId, out string name))
                    (staff ? list.UnnamedStaff : list.UnnamedPlayers).Add(personId);
                else if (staff)
                    list.Staff.Add(new SquadRow(personId, record, name, staff: true, job: ProfileReader.ContractJob(game, record)));
                else
                    list.Players.Add(new SquadRow(personId, record, name));
            }
            return list;
        }

        // The staff command's list: the open screen's club's staff, FM24's
        // indexed portraits and the in-game date. ``people`` gets the club's
        // whole list, players included.
        public static SquadState ReadStaff(GameProcess game, out ClubList people)
        {
            long club = ScreenClub(game);
            people = ClubPeople(game, club);
            if (people.Staff.Count == 0)
                throw new MemoryUnavailableException(Text.T("RegenerateFM found no staff it can read at this club."));
            var (indexed, today) = SquadReader.Catalogue(game, people.Staff.Select(row => row.Record).ToList());
            return new SquadState
            {
                Rows = people.Staff, Total = people.Staff.Count + people.UnnamedStaff.Count, Unreadable = people.UnnamedStaff,
                Indexed = indexed, Today = today,
            };
        }
    }
}
