using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RegenerateFM.Desktop.FM24.Memory
{
    public sealed class SquadRow
    {
        public SquadRow(string personId, long record, string name, bool staff = false, string job = null)
        {
            PersonId = personId;
            Record = record;
            Name = name;
            Staff = staff;
            Job = job;
        }

        public string PersonId { get; }
        public long Record { get; }
        public string Name { get; }
        public bool Staff { get; }
        // A staff member's job, or null (a player, or a job RegenerateFM can't name).
        public string Job { get; }
    }

    public sealed class SquadState
    {
        public List<SquadRow> Rows = new List<SquadRow>();   // drawn rows only; see Total
        public int Total;                                    // rows in the table, drawn or not
        public HashSet<string> Indexed = new HashSet<string>();
        public DateTime Today;
        public (int First, int Last) Visible;                // first and last visible row, 1-based
        public long Table;                                   // the table widget, for the row model
        public List<string> Unreadable = new List<string>(); // undrawn players whose names FM24 hasn't loaded
        public List<string> Brief = new List<string>();      // players FM24 keeps only a brief record for
        public int Coaches;                                  // player-coaches, made from their own profile instead
        public int Others;                                   // rows that aren't players RegenerateFM can read

        public bool Complete => Rows.Count + Unreadable.Count + Brief.Count + Coaches + Others == Total;
        public List<SquadRow> Missing => Rows.Where(row => !Indexed.Contains(row.PersonId)).ToList();
    }

    // A row's person isn't a player RegenerateFM can describe from the list: a
    // player-coach (Coach), staff, or no validated person at all.
    public sealed class NotAPlayerException : MemoryUnavailableException
    {
        public NotAPlayerException(bool coach = false) : base("A Squad row does not show a validated FM24 player.")
        {
            Coach = coach;
        }

        public bool Coach { get; }
    }

    // A row's person has only FM24's brief record (no age or attributes).
    public sealed class BriefRecordException : MemoryUnavailableException
    {
        public BriefRecordException(string personId) : base("FM24 keeps only a brief record for person " + personId + ".")
        {
            PersonId = personId;
        }

        public string PersonId { get; }
    }

    // The open FM24 Squad table and portrait index, from game memory. One
    // read-only pass over FM24's writable memory finds the player-name cells of
    // the Squad table attached to the live UI tree, and the person record each
    // row shows (the same rows FM24 draws, including loaned-in and reserve
    // players listed on the screen), and FM24's graphics resource index, which
    // lists every indexed graphics/pictures/person/<id>/portrait (built-in and
    // RegenerateFM pictures). Anything unexpected raises
    // MemoryUnavailableException; callers must not guess.
    public static class SquadReader
    {
        public static readonly int NameCellId = Widgets.SkinId("Pnms");
        // A team's Overview squad list (a reserve or youth team in the
        // Development Center) uses the same cell class named PnmP, two levels
        // inside a row widget whose parent is the list; the list keeps rows,
        // visible rows and its row model at the same offsets as the Squad table.
        public static readonly int ListCellId = Widgets.SkinId("PnmP");
        public const int RowPerson = 0x728;
        public const int RowName = 0x1C8;       // cell -> {u32 length, UTF-8} display name ("Ian Fray")
        public const int TableRows = 0x3A0;     // table widget: total rows, drawn or not
        public const int TableVisible = 0x394;  // table widget: first and last visible row, 1-based
        // Every row of the table, drawn or not: table -> row model (layout
        // TableModel), whose vector at +0x18 lists the row objects (the header
        // row first). A row object starts with a vector of {fourcc key, value
        // pointer} properties; its 'id  ' value holds a u32 at +8. A person row's
        // id is 0x01000000 | the person's handle, the u32 at person+8, and FM24
        // keeps person pointers in a heap table indexed by handle.
        public const int ModelRows = 0x18;
        public static readonly int RowIdKey = Widgets.SkinId("id  ");
        public static readonly int HeaderRowId = Widgets.SkinId("HROW");
        public const int PersonRow = 0x01000000;
        public static string NotASquad =>
            Text.T("Generate missing squad faces works on a team's player list, and the list on this screen has no players RegenerateFM can read. Open the team's Squad screen (for a reserve or youth team, its tab in the Development Center) and choose it from that menu.");
        private static readonly byte[] PersonPicture = Encoding.ASCII.GetBytes("graphics/pictures/person/");
        private static readonly Regex Portrait = new Regex(@"\G(?:r-)?([1-9][0-9]{0,9})/portrait", RegexOptions.Compiled);
        private static readonly byte[] NoPicture = Encoding.ASCII.GetBytes("graphics/pictures/person/none/male/player/portrait");
        private static readonly Regex DateText = new Regex(@"^([1-9]|[12][0-9]|3[01]) ([A-Z][a-z]{2}) ((?:19|20)[0-9]{2})\z", RegexOptions.Compiled);

        internal static string PlayerId(GameProcess game, long person)
        {
            long record = person + 12;
            var raw = game.Read(record, 20);
            uint number = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (!raw.AsSpan(0, 8).SequenceEqual(People.IdTwice(number)) || number < 1 || number > 2_147_483_647)
                throw new NotAPlayerException();
            // Rows FM24 lists as "Scouting Required" with no age or ability
            // (players of leagues it hasn't loaded) are brief objects
            // (db::VIRTUAL_PLAYER) with none of the fields a prompt needs.
            long kind = People.ObjectClass(game, record);
            string id = number.ToString(CultureInfo.InvariantCulture);
            if (game.Layout.BriefVtables.Contains(kind))
                throw new BriefRecordException(id);
            // A player-coach keeps their player fields outside the record.
            if (game.Layout.PlayerCoachVtables.Contains(kind))
                throw new NotAPlayerException(coach: true);
            if (!People.IsPersonRecord(game, record))
                throw new NotAPlayerException();
            if (People.PersonKind(game, record, raw[16]) != "player")
                throw new NotAPlayerException();
            return id;
        }

        private static string RowText(GameProcess game, long address)
        {
            uint size = game.U32(address);
            if (size < 1 || size > 80)
                throw new MemoryUnavailableException("Invalid FM24 row name.");
            string value;
            try
            {
                value = People.Utf8.GetString(game.Read(address + 4, (int)size));
            }
            catch (DecoderFallbackException)
            {
                throw new MemoryUnavailableException("Invalid FM24 row name.");
            }
            if (!People.IsPrintable(value) || value != value.Trim())
                throw new MemoryUnavailableException("Invalid FM24 row name.");
            return value;
        }

        private static readonly byte[] Space20 = Encoding.ASCII.GetBytes(" 20");

        // Counts "d Mon 20YY" FM string objects in a chunk (the top bar shows today's).
        public static void CountDates(ReadOnlySpan<byte> data, Dictionary<string, int> dates)
        {
            int offset = 0;
            while (true)
            {
                int at = data.Slice(offset).IndexOf(Space20);
                if (at < 0)
                    break;
                offset += at;
                if (offset + 6 <= data.Length && IsDigit(data[offset + 3]) && IsDigit(data[offset + 4]) && data[offset + 5] == 0)
                {
                    foreach (int start in new[] { offset - 5, offset - 6 })
                    {
                        if (start < 4)
                            continue;
                        string text = Encoding.Latin1.GetString(data.Slice(start, offset + 5 - start));
                        var match = DateText.Match(text);
                        if (match.Success && Array.IndexOf(ScreenReader.Months, match.Groups[2].Value) >= 0 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(start - 4)) == text.Length)
                            dates[text] = dates.GetValueOrDefault(text) + 1;
                    }
                }
                offset += 3;
            }
        }

        private static bool IsDigit(byte value) => value >= '0' && value <= '9';

        // One pass: name cells of this manager and, with ``catalogue``, the
        // indexed portraits and the dates shown in FM strings.
        private static (List<(long Cell, int Kind)>, HashSet<string>, Dictionary<string, int>) ScanCells(GameProcess game, long manager, bool catalogue)
        {
            long cellVtable = game.ImageBase + game.Layout.NameCellVtable;
            var needle = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(needle, manager);
            var noPictureKey = new byte[8 + NoPicture.Length + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(noPictureKey, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(noPictureKey.AsSpan(4), (uint)NoPicture.Length);
            NoPicture.CopyTo(noPictureKey, 8);
            var cells = new List<(long, int)>();
            var indexed = new HashSet<string>();
            var dates = new Dictionary<string, int>();
            bool noPicture = false;
            var scan = game.Scan(128);
            foreach (var chunk in scan)
            {
                var data = chunk.Span;
                int offset = 0;
                while (true)
                {
                    int at = data.Slice(offset).IndexOf(needle);
                    if (at < 0)
                        break;
                    offset += at;
                    int start = offset - Widgets.Manager;
                    if ((chunk.Address + start) % 8 == 0 && start >= 0 && start + Widgets.Id + 4 <= data.Length &&
                        BinaryPrimitives.ReadInt64LittleEndian(data.Slice(start)) == cellVtable)
                    {
                        int kind = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + Widgets.Id));
                        if (kind == NameCellId || kind == ListCellId)
                            cells.Add((chunk.Address + start, kind));
                    }
                    offset += 8;
                }
                if (!catalogue)
                    continue;
                offset = 0;
                while (true)
                {
                    int at = data.Slice(offset).IndexOf(PersonPicture);
                    if (at < 0)
                        break;
                    offset += at;
                    // An indexed key is a complete FM string object:
                    // {u32 1}{u32 length} "graphics/pictures/person/<id>/portrait" NUL.
                    int after = offset + PersonPicture.Length;
                    var window = data.Slice(after, Math.Min(24, data.Length - after));
                    var match = Portrait.Match(Encoding.Latin1.GetString(window));
                    int end = match.Success ? after + match.Length : 0;
                    if (match.Success && offset >= 8 && end < data.Length && data[end] == 0 &&
                        data.Slice(offset - 8, 4).SequenceEqual(new byte[] { 1, 0, 0, 0 }) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset - 4)) == end - offset)
                        indexed.Add(match.Groups[1].Value);
                    offset += PersonPicture.Length;
                }
                noPicture |= data.IndexOf(noPictureKey) >= 0;
                CountDates(data, dates);
            }
            if (!scan.Complete || (catalogue && !noPicture))
                throw new MemoryUnavailableException("Could not read FM24's complete portrait index.");
            return (cells, indexed, dates);
        }

        // The in-game date shown in FM24's top bar ("2 Jan 2023"), from the most
        // common date string.
        internal static DateTime Today(Dictionary<string, int> dates)
        {
            if (dates.Count == 0)
                throw new MemoryUnavailableException("Could not find FM24's current date.");
            var ordered = dates.OrderByDescending(entry => entry.Value).ToList();
            var (text, count) = (ordered[0].Key, ordered[0].Value);
            if (count < 2 || (ordered.Count > 1 && ordered[1].Value * 2 > count))
                throw new MemoryUnavailableException("Could not identify FM24's current date.");
            var parts = text.Split(' ');
            return new DateTime(int.Parse(parts[2], CultureInfo.InvariantCulture), Array.IndexOf(ScreenReader.Months, parts[1]) + 1,
                                int.Parse(parts[0], CultureInfo.InvariantCulture));
        }

        // The open Squad table's drawn players and FM24's indexed portraits.
        // Only drawn rows have cells; Complete says whether that is every row.
        public static SquadState Read(GameProcess game, int attempts = 3, bool catalogue = true)
        {
            var state = new SquadState();
            try
            {
                long manager = Widgets.UiManager(game);
                List<(long Cell, int Kind)> cells = null;
                Dictionary<string, int> dates = null;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        (cells, state.Indexed, dates) = ScanCells(game, manager, catalogue);
                        break;
                    }
                    catch (MemoryUnavailableException) when (attempt + 1 < attempts)
                    {
                        // FM24 can unmap memory during a scan; a later pass is complete.
                    }
                }
                // Table -> {its child holding a row: that row's name cell}, per cell kind.
                var tables = new Dictionary<int, Dictionary<long, Dictionary<long, long>>>
                {
                    [NameCellId] = new Dictionary<long, Dictionary<long, long>>(),
                    [ListCellId] = new Dictionary<long, Dictionary<long, long>>(),
                };
                foreach (var (cell, kind) in new HashSet<(long, int)>(cells))
                {
                    if (!Widgets.Attached(game, cell, manager))
                        continue;
                    long row = kind == NameCellId ? cell : game.U64(game.U64(cell + Widgets.Parent) + Widgets.Parent);
                    long table = game.U64(row + Widgets.Parent);
                    if (!tables[kind].TryGetValue(table, out var rows))
                        tables[kind][table] = rows = new Dictionary<long, long>();
                    rows[row] = cell;
                }
                // The Squad table when there is one, so a list elsewhere on the
                // screen can't make the first team's Squad command ambiguous.
                var found = tables[NameCellId].Count > 0 ? tables[NameCellId] : tables[ListCellId];
                if (found.Count == 0)
                    throw new MemoryUnavailableException(Text.T("No squad player list is open; open the team's Squad view and try again."));
                if (found.Count != 1)
                    throw new MemoryUnavailableException("Expected one open FM24 Squad table; found " + found.Count + ".");
                var (tableWidget, drawn) = found.First();
                state.Table = tableWidget;
                state.Total = game.I32(tableWidget + TableRows);
                state.Visible = (game.I32(tableWidget + TableVisible), game.I32(tableWidget + TableVisible + 4));
                if (!(drawn.Count <= state.Total && state.Total <= 500) ||
                    !(1 <= state.Visible.First && state.Visible.First <= state.Visible.Last && state.Visible.Last <= state.Total))
                    throw new MemoryUnavailableException("Invalid FM24 Squad table row count.");
                var order = new Dictionary<long, int>();
                var children = Widgets.ChildrenOf(game, tableWidget);
                for (int i = 0; i < children.Count; i++)
                    order[children[i]] = i;
                if (!drawn.Keys.All(order.ContainsKey))
                    throw new MemoryUnavailableException("The FM24 Squad table changed while it was read.");
                var seen = new HashSet<string>();
                foreach (long row in drawn.Keys.OrderBy(row => order[row]))
                {
                    long cell = drawn[row];
                    long person = game.U64(cell + RowPerson);
                    string personId;
                    try
                    {
                        personId = PlayerId(game, person);
                    }
                    catch (BriefRecordException brief)
                    {
                        if (!state.Brief.Contains(brief.PersonId))
                            state.Brief.Add(brief.PersonId);
                        continue;
                    }
                    catch (NotAPlayerException other)
                    {
                        if (other.Coach)
                            state.Coaches++;
                        else
                            state.Others++;
                        continue;
                    }
                    if (seen.Add(personId))
                        state.Rows.Add(new SquadRow(personId, person + 12, RowText(game, game.U64(cell + RowName))));
                }
                if (state.Rows.Count == 0)
                {
                    // The squad menu is also offered on screens with another kind of
                    // list (a Development Center team's overview can list staff).
                    throw new MemoryUnavailableException(
                        state.Brief.Count > 0 ? Text.T("FM24 keeps only brief records for the players shown (no age or ability), so RegenerateFM can't describe them.")
                        : state.Coaches > 0 || state.Others > 0 ? NotASquad
                        : Text.T("The open FM24 Squad table has no players."));
                }
                state.Today = catalogue ? Today(game, dates, state.Rows.Select(row => row.Record)) : DateTime.MinValue;
            }
            catch (MemoryUnavailableException)
            {
                throw;
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                throw new MemoryUnavailableException("Could not read the FM24 Squad table: " + error.Message);
            }
            return state;
        }

        // The in-game date: shown in the top bar, else the date strings in
        // memory for the current year. Person records carry the year they were
        // last updated in: the current one, or for some youth players the one
        // before.
        private static DateTime Today(GameProcess game, Dictionary<string, int> dates, IEnumerable<long> records)
        {
            var today = ScreenReader.ScreenDate(game);
            if (today != null)
                return today.Value;
            int year = records.Select(record => (int)game.U16(record + game.Layout.RecordYear)).DefaultIfEmpty(0).Max();
            if (year <= 1900 || year >= 2200)
                throw new MemoryUnavailableException("Could not read FM24's current year.");
            return Today(dates.Where(entry => entry.Key.EndsWith(" " + year, StringComparison.Ordinal))
                              .ToDictionary(entry => entry.Key, entry => entry.Value));
        }

        // FM24's indexed portraits and the in-game date, for people read without
        // a list (``records``, for the current year if the date isn't shown).
        public static (HashSet<string> Indexed, DateTime Today) Catalogue(GameProcess game, IList<long> records, int attempts = 3)
        {
            try
            {
                long manager = Widgets.UiManager(game);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        var (_, indexed, dates) = ScanCells(game, manager, catalogue: true);
                        return (indexed, Today(game, dates, records));
                    }
                    catch (MemoryUnavailableException) when (attempt + 1 < attempts)
                    {
                        // FM24 can unmap memory during a scan; a later pass is complete.
                    }
                }
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                throw new MemoryUnavailableException("Could not read FM24's portrait index: " + error.Message);
            }
        }

        // The 'id  ' of every data row in the table's row model, in table order.
        private static List<int> RowIds(GameProcess game, long table)
        {
            long model = game.U64(table + game.Layout.TableModel);
            long begin = game.U64(model + ModelRows), end = game.U64(model + ModelRows + 8);
            if (end <= begin || (end - begin) % 8 != 0 || end - begin > 8 * 501)
                throw new MemoryUnavailableException("Invalid FM24 Squad row model.");
            var rows = new List<long>();
            for (long start = 0; start < end - begin; start += GameProcess.MaxRead)
            {
                var data = game.Read(begin + start, (int)Math.Min(GameProcess.MaxRead, end - begin - start));
                for (int i = 0; i < data.Length; i += 8)
                    rows.Add(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(i)));
            }
            var ids = new List<int>();
            foreach (long row in rows)
            {
                long first = game.U64(row), last = game.U64(row + 8);
                if (last < first || (last - first) % 16 != 0 || last - first > 16 * 32)
                    throw new MemoryUnavailableException("Invalid FM24 Squad row.");
                var properties = last > first ? game.Read(first, (int)(last - first)) : Array.Empty<byte>();
                var values = new List<long>();
                for (int i = 0; i < properties.Length; i += 16)
                    if ((BinaryPrimitives.ReadInt64LittleEndian(properties.AsSpan(i)) & 0xFFFFFFFF) == (uint)RowIdKey)
                        values.Add(BinaryPrimitives.ReadInt64LittleEndian(properties.AsSpan(i + 8)));
                if (values.Count != 1)
                    throw new MemoryUnavailableException("An FM24 Squad row has no single row ID.");
                ids.Add((int)game.U32(values[0] + 8));
            }
            if (ids.Count == 0 || ids[0] != HeaderRowId || ids.Skip(1).Any(row => row >> 24 != PersonRow >> 24))
                throw new MemoryUnavailableException("The FM24 Squad row model does not list people.");
            return ids.Skip(1).ToList();
        }

        // Base of FM24's person table, found from drawn rows' person pointers.
        // One pass finds where the first person's pointer is stored; the base is
        // the one that also holds every other drawn person at their own handles.
        // With at least five people that cannot be a coincidence, so the pass
        // stops there.
        private static long HandleTable(GameProcess game, List<long> people)
        {
            if (people.Count < 2)
                throw new MemoryUnavailableException("Too few drawn FM24 Squad rows to locate the person table.");
            var handles = people.Select(person => (int)game.U32(person + PersonHandles.PersonHandle)).ToList();
            bool HoldsAll(long candidate)
            {
                for (int i = 0; i < people.Count; i++)
                    if (game.U64(candidate + 8L * handles[i]) != people[i])
                        return false;
                return true;
            }
            if (game.PersonTable is long cached)
            {
                // The table stays put while the game runs; check it still holds every drawn person.
                try
                {
                    if (HoldsAll(cached))
                        return cached;
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                }
            }
            // The database's people vector (PersonHandles), else a memory search.
            long? table = PersonHandles.PeopleVector(game, handles[0]);
            try
            {
                if (table == null || !HoldsAll(table.Value))
                    table = FindHandleTable(game, people, handles);
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                table = FindHandleTable(game, people, handles);
            }
            game.PersonTable = table;
            return table.Value;
        }

        private static long FindHandleTable(GameProcess game, List<long> people, List<int> handles)
        {
            var needle = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(needle, people[0]);
            var bases = new HashSet<long>();
            foreach (var chunk in game.Scan(8))
            {
                var data = chunk.Span;
                int offset = 0;
                while (true)
                {
                    int at = data.Slice(offset).IndexOf(needle);
                    if (at < 0)
                        break;
                    offset += at;
                    long candidate = chunk.Address + offset - 8L * handles[0];
                    if ((chunk.Address + offset) % 8 == 0 && candidate > 0)
                    {
                        try
                        {
                            bool all = true;
                            for (int i = 1; i < people.Count && all; i++)
                                all = game.U64(candidate + 8L * handles[i]) == people[i];
                            if (all)
                                bases.Add(candidate);
                        }
                        catch (Exception error) when (People.Unreadable(error))
                        {
                        }
                    }
                    offset += 8;
                }
                if (bases.Count == 1 && people.Count >= 5)
                    break;
            }
            if (bases.Count != 1)
                throw new MemoryUnavailableException("Expected one FM24 person table; found " + bases.Count + ".");
            return bases.First();
        }

        // Names of players without a drawn cell, from their person records.
        // ``rows`` maps person ID to record. Players whose record has no
        // full-name string use FM24's separate first/surname record, or its
        // "object: <id> - <name>" text, found in one pass. Players with neither
        // are left out.
        private static Dictionary<string, string> UndrawnNames(GameProcess game, List<(string Id, long Record)> rows)
        {
            var names = new Dictionary<string, string>();
            var alternate = new List<(byte[] Needle, string Id)>();
            foreach (var (personId, record) in rows)
            {
                var identity = People.ParseRecord(game, record, long.Parse(personId, CultureInfo.InvariantCulture));
                if (identity != null)
                    names[personId] = identity.Name;
                else
                {
                    var needle = new byte[9];
                    People.IdTwice(long.Parse(personId, CultureInfo.InvariantCulture)).CopyTo(needle, 0);
                    needle[8] = 0x09;
                    alternate.Add((needle, personId));
                }
            }
            var found = new Dictionary<string, HashSet<string>>();
            if (alternate.Count > 0)
            {
                var wanted = new HashSet<string>(alternate.Select(item => item.Id));
                foreach (var chunk in game.Scan(128))
                {
                    var data = chunk.Span;
                    foreach (var (needle, personId) in alternate)
                    {
                        int offset = 0;
                        while (true)
                        {
                            int at = data.Slice(offset).IndexOf(needle);
                            if (at < 0)
                                break;
                            offset += at;
                            string name = ProfileReader.AlternateName(data.Slice(offset, Math.Min(128, data.Length - offset)));
                            if (name != null)
                            {
                                if (!found.TryGetValue(personId, out var set))
                                    found[personId] = set = new HashSet<string>();
                                set.Add(name);
                            }
                            offset++;
                        }
                    }
                    ProfileReader.CollectObjectNames(data, wanted, found);
                }
            }
            foreach (var (_, personId) in alternate)
                if (found.TryGetValue(personId, out var set) && set.Count == 1)
                    names[personId] = set.First();
            return names;
        }

        // Every Squad row, drawn or not, without scrolling the table. Drawn rows
        // keep their displayed name; the others use the person record's name.
        // Each row resolves through the person table to a validated player, and
        // every drawn row must be among them.
        // Replaced by tests: the drawn rows, and the undrawn rows' names.
        internal static Func<GameProcess, SquadState> ReadDrawn = game => Read(game);
        internal static Func<GameProcess, List<(string Id, long Record)>, Dictionary<string, string>> Names = UndrawnNames;

        public static SquadState ReadRows(GameProcess game)
        {
            var state = ReadDrawn(game);
            if (state.Complete)
                return state;
            var records = new List<(string Id, long Record)>();
            var drawn = state.Rows.ToDictionary(row => row.PersonId);
            var result = new SquadState
            {
                Total = state.Total, Indexed = state.Indexed, Today = state.Today, Visible = state.Visible, Table = state.Table,
            };
            try
            {
                var ids = RowIds(game, state.Table);
                if (ids.Count != state.Total || new HashSet<int>(ids).Count != ids.Count)
                    throw new MemoryUnavailableException("The FM24 Squad row model does not match the table.");
                long table = HandleTable(game, state.Rows.Select(row => row.Record - 12).ToList());
                foreach (int rowId in ids)
                {
                    int handle = rowId & 0xFFFFFF;
                    long person = game.U64(table + 8L * handle);
                    if (game.U32(person + PersonHandles.PersonHandle) != handle)
                        throw new MemoryUnavailableException("An FM24 Squad row does not resolve to its person.");
                    try
                    {
                        records.Add((PlayerId(game, person), person + 12));
                    }
                    catch (BriefRecordException brief)
                    {
                        result.Brief.Add(brief.PersonId);
                    }
                    catch (NotAPlayerException other)
                    {
                        if (other.Coach)
                            result.Coaches++;
                        else
                            result.Others++;
                    }
                }
            }
            catch (MemoryUnavailableException)
            {
                throw;
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                throw new MemoryUnavailableException("Could not read the FM24 Squad rows: " + error.Message);
            }
            var listed = new HashSet<string>(records.Select(record => record.Id));
            if (!drawn.Keys.All(listed.Contains))
                throw new MemoryUnavailableException("The FM24 Squad row model does not contain the drawn rows.");
            var names = Names(game, records.Where(record => !drawn.ContainsKey(record.Id)).ToList());
            foreach (var (personId, record) in records)
            {
                if (drawn.TryGetValue(personId, out var row))
                    result.Rows.Add(row);
                else if (names.TryGetValue(personId, out string name))
                    result.Rows.Add(new SquadRow(personId, record, name));
                else
                    result.Unreadable.Add(personId);
            }
            return result;
        }
    }
}
