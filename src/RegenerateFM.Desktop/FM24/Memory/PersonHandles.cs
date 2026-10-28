using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // A person from FM24's object reference to them, without screen IDs.
    //
    // FM24 refers to an object by a 32-bit reference, type << 24 | index; a
    // person is 0x01000000 | their handle, the u32 just before their record
    // (person + 8; see SquadReader). The open screen's object browser holds the
    // reference of what it shows, so the library can report the person on a
    // profile when the header shows no "(ID:n)". FM24's person table (a vector
    // of person pointers indexed by handle) makes a lookup two reads. If the
    // table read fails, the companion maps handles to people with one pass over
    // the game's memory, and checks each mapping again before it is used.
    public static class PersonHandles
    {
        public const int PersonHandle = 8;
        private const int PersonType = 1;
        private const int IndexMask = 0x7FFFFF;
        private const double RebuildAfter = 10.0;
        public const int DatabaseScan = 0x2000;

        private static readonly object Lock = new object();
        private static readonly Dictionary<int, Dictionary<int, long>> Index = new Dictionary<int, Dictionary<int, long>>();
        private static readonly Dictionary<int, Stopwatch> Built = new Dictionary<int, Stopwatch>();
        private static readonly HashSet<int> Building = new HashSet<int>();

        // Replaced by tests: the pass that maps handles to records.
        internal static Func<GameProcess, Dictionary<int, long>> ScanPass = ScanObjects;

        // The handle in a person reference; ArgumentException for any other kind of object.
        public static int Handle(uint reference)
        {
            // Handle 0 is the people vector's first person, not a null reference.
            if (reference >> 24 != PersonType || (reference & IndexMask) == IndexMask)
                throw new ArgumentException("FM24 reference 0x" + reference.ToString("x8") + " is not a person.");
            return (int)(reference & IndexMask);
        }

        // Person objects found by their vtable pointer: {vtable}{handle}{record}.
        public static Dictionary<int, long> ScanObjects(GameProcess game)
        {
            long image = game.ImageBase;
            var needles = new List<byte[]>();
            foreach (long vtable in game.Layout.PersonVtables.Keys)
            {
                var needle = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(needle, image + vtable);
                needles.Add(needle);
            }
            var found = new Dictionary<int, long>();
            foreach (var chunk in game.Scan(24))
            {
                var data = chunk.Span;
                foreach (var needle in needles)
                {
                    int offset = 0;
                    while (true)
                    {
                        int at = data.Slice(offset).IndexOf(needle);
                        if (at < 0)
                            break;
                        offset += at;
                        if ((chunk.Address + offset) % 8 == 0 && offset + 24 <= data.Length)
                        {
                            uint handle = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8));
                            uint number = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12));
                            uint again = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 16));
                            if (number == again && number != 0 && handle < IndexMask)
                                found.TryAdd((int)handle, chunk.Address + offset + 12);
                        }
                        offset++;
                    }
                }
            }
            return found;
        }

        // The person ID if the record at this address still carries this handle.
        private static string Check(GameProcess game, int handle, long record)
        {
            byte[] raw;
            try
            {
                raw = game.Read(record - 4, 20);
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                return null;
            }
            uint number = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != handle || !raw.AsSpan(4, 4).SequenceEqual(raw.AsSpan(8, 4)) ||
                number < 1 || number > 2_147_483_647 || !People.IsPersonRecord(game, record))
                return null;
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // The person ID and record at this handle in the database's vector at
        // ``slot``, or null. ``slots`` holds the database's first bytes, if read.
        private static (string, long)? SlotPerson(GameProcess game, long database, int slot, int handle, ReadOnlySpan<byte> slots = default)
        {
            try
            {
                long begin, end;
                if (slot + 16 <= slots.Length)
                {
                    begin = BinaryPrimitives.ReadInt64LittleEndian(slots.Slice(slot));
                    end = BinaryPrimitives.ReadInt64LittleEndian(slots.Slice(slot + 8));
                }
                else
                {
                    begin = game.U64(database + slot);
                    end = game.U64(database + slot + 8);
                }
                if (begin == 0 || end <= begin || (end - begin) % 8 != 0 || handle >= (end - begin) / 8)
                    return null;
                long person = game.U64(begin + 8L * handle);
                long record = person + PersonHandle + 4;
                string number = person != 0 ? Check(game, handle, record) : null;
                return number != null ? (number, record) : ((string, long)?)null;
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                return null;
            }
        }

        // The person ID and record address at this handle in FM24's person
        // table, or null. The database object keeps one vector per kind of
        // object, each indexed by handle ({begin, end, capacity, allocator}),
        // and which slot holds people changes from one run of FM24 to the next.
        // The layout's slot is tried first, then every slot in the first
        // DatabaseScan bytes; the people's is the one whose entry is a person
        // record carrying this handle.
        public static (string PersonId, long Record)? TableRecord(GameProcess game, int handle)
        {
            long database;
            try
            {
                database = game.U64(game.ImageBase + game.Layout.PersonTableGlobal);
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                return null;
            }
            int first = game.PersonTableSlot ?? game.Layout.PersonTableSlot;
            var person = SlotPerson(game, database, first, handle);
            if (person != null)
                return person;
            // Every slot in one read: on the Mac each read is a request to the
            // game's library, and FM24's menu waits only 30 ms for a face check.
            var slots = new byte[DatabaseScan + 8];
            int read = 0;
            try
            {
                read = game.ReadInto(database, slots);
            }
            catch (Exception error) when (People.Unreadable(error))
            {
            }
            var found = new SortedDictionary<int, (string, long)>();
            for (int slot = 0; slot < DatabaseScan; slot += 8)
                if (slot != first && SlotPerson(game, database, slot, handle, slots.AsSpan(0, read)) is (string, long) other)
                    found[slot] = other;
            if (new HashSet<(string, long)>(found.Values).Count != 1)
                return null;
            foreach (var entry in found)
            {
                game.PersonTableSlot = entry.Key;
                return entry.Value;
            }
            return null;
        }

        // The start of FM24's vector of person pointers, indexed by handle (the
        // slot TableRecord finds with this handle's person), or null.
        public static long? PeopleVector(GameProcess game, int handle) => PeopleRange(game, handle)?.Begin;

        // That vector's start and its number of entries, or null.
        public static (long Begin, int Count)? PeopleRange(GameProcess game, int handle)
        {
            if (TableRecord(game, handle) == null)
                return null;
            try
            {
                long database = game.U64(game.ImageBase + game.Layout.PersonTableGlobal);
                long slot = database + (game.PersonTableSlot ?? game.Layout.PersonTableSlot);
                long begin = game.U64(slot), end = game.U64(slot + 8);
                return (begin, (int)((end - begin) / 8));
            }
            catch (Exception error) when (People.Unreadable(error))
            {
                return null;
            }
        }

        // Maps every person record's handle (one memory pass, about 10 s). A pass
        // already running for this game is waited for, not repeated.
        public static void Build(GameProcess game)
        {
            lock (Lock)
            {
                if (Building.Contains(game.Pid))
                {
                    while (Building.Contains(game.Pid))
                        Monitor.Wait(Lock);
                    return;
                }
                Building.Add(game.Pid);
            }
            try
            {
                var found = ScanPass(game);
                lock (Lock)
                {
                    Index[game.Pid] = found;
                    Built[game.Pid] = Stopwatch.StartNew();
                }
            }
            finally
            {
                lock (Lock)
                {
                    Building.Remove(game.Pid);
                    Monitor.PulseAll(Lock);
                }
            }
        }

        public static void BuildInBackground(GameProcess game)
        {
            lock (Lock)
                if (Building.Contains(game.Pid))
                    return;
            var thread = new Thread(() =>
            {
                try
                {
                    Build(game);
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                }
            }) { IsBackground = true, Name = "fm24-person-handles" };
            thread.Start();
        }

        // Thrown without ``wait`` while the index is still being built.
        public sealed class StillIndexingException : InvalidOperationException
        {
            public StillIndexingException() : base(Text.T("RegenerateFM is still indexing FM24's people.")) { }
        }

        // The person ID and record address for a person reference.
        public static (string PersonId, long Record) PersonRecordForReference(GameProcess game, uint reference, bool wait = true)
        {
            int handle = Handle(reference);
            var found = TableRecord(game, handle);
            if (found != null)
                return found.Value;
            long record = 0;
            Stopwatch last;
            lock (Lock)
            {
                if (Index.TryGetValue(game.Pid, out var index))
                    index.TryGetValue(handle, out record);
                Built.TryGetValue(game.Pid, out last);
            }
            string person = record != 0 ? Check(game, handle, record) : null;
            if (person != null)
                return (person, record);
            // A person the index doesn't know yet (a newgen, say), or a moved record.
            if (last == null || last.Elapsed.TotalSeconds > RebuildAfter)
            {
                if (!wait)
                {
                    BuildInBackground(game);
                    throw new StillIndexingException();
                }
                Build(game);
                lock (Lock)
                {
                    record = 0;
                    if (Index.TryGetValue(game.Pid, out var index))
                        index.TryGetValue(handle, out record);
                }
                person = record != 0 ? Check(game, handle, record) : null;
                if (person != null)
                    return (person, record);
            }
            throw new MemoryUnavailableException("No FM24 person has handle 0x" + handle.ToString("x") + ".");
        }

        public static string PersonForReference(GameProcess game, uint reference, bool wait = true) =>
            PersonRecordForReference(game, reference, wait).PersonId;

        // The open screen's object reference: the sidebar menu's, which FM24
        // keeps after a skin reload, else the object browser's (exactly one).
        // Without ``person``, the sidebar's reference to any kind of object.
        public static uint ScreenReference(GameProcess game, long manager, Dictionary<int, List<long>> widgets, bool person = true)
        {
            long image = game.ImageBase;
            var sidebars = new List<long>();
            if (widgets.TryGetValue(Widgets.SkinId("side"), out var sides))
                foreach (long widget in sides)
                    if (game.U64(widget) == image + game.Layout.SidebarVtable)
                        sidebars.Add(widget);
            if (sidebars.Count == 1)
            {
                uint reference = game.U32(sidebars[0] + game.Layout.SidebarReference);
                if (!person && reference != 0)
                    return reference;
                try
                {
                    Handle(reference);
                    return reference;
                }
                catch (ArgumentException)
                {
                }
            }
            long vtable = image + game.Layout.ObjectBrowserVtable;
            var browsers = new List<long>();
            foreach (var group in widgets.Values)
                foreach (long widget in group)
                    if (game.U64(widget) == vtable)
                        browsers.Add(widget);
            if (browsers.Count != 1)
                throw new MemoryUnavailableException("Expected one FM24 object browser; found " + browsers.Count + ".");
            return game.U32(browsers[0] + game.Layout.BrowserReference);
        }
    }
}
