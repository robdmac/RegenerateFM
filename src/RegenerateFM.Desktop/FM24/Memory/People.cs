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
    public sealed class PersonIdentity : IEquatable<PersonIdentity>
    {
        public PersonIdentity(long personId, string name, DateTime birthDate, int gameYear, string kind)
        {
            PersonId = personId;
            Name = name;
            BirthDate = birthDate;
            GameYear = gameYear;
            Kind = kind;
        }

        public long PersonId { get; }
        public string Name { get; }
        public DateTime BirthDate { get; }
        public int GameYear { get; }
        public string Kind { get; }  // "player" or "staff"

        // The ages the record's year allows. The year is the one the record was
        // last updated in, which can be the season before (738 of 31,032 people,
        // mostly youth players, in a 2026-10-21 save), so AgeOn is preferred.
        public (int, int) PossibleAges => (GameYear - BirthDate.Year - 1, GameYear - BirthDate.Year);

        // The person's age on the in-game date ``today``.
        public int AgeOn(DateTime today) =>
            today.Year - BirthDate.Year - ((today.Month, today.Day).CompareTo((BirthDate.Month, BirthDate.Day)) < 0 ? 1 : 0);

        public bool Equals(PersonIdentity other) =>
            other != null && PersonId == other.PersonId && Name == other.Name && BirthDate == other.BirthDate &&
            GameYear == other.GameYear && Kind == other.Kind;

        public override bool Equals(object other) => Equals(other as PersonIdentity);
        public override int GetHashCode() => HashCode.Combine(PersonId, Name, BirthDate, GameYear, Kind);
    }

    // Person records: an independently derived, read-only parser for the FM24
    // 24.4.2 builds (see Layout). Unknown records fail closed.
    public static class People
    {
        public const int RecordSize = 64;
        // Record type at +16: 4 and 5 are players with full detail; 0 and 1 are
        // seen both for staff and for reserve or youth players. Players' records
        // hold position ratings (1-20, 20 at their natural position), staff
        // records zeros, so the ratings say which a type 0 or 1 record is.
        public static readonly int[] RecordTypes = { 0, 1, 4, 5 };
        public const int PositionCount = 15;
        private static readonly Regex Printable = new Regex(@"^[^\x00-\x1f\x7f]+$", RegexOptions.Compiled);
        private static readonly Regex PrintableName = new Regex(@"^[^\x00-\x1f\x7f]{3,80}$", RegexOptions.Compiled);
        private static readonly Regex PersonIdPattern = new Regex(@"^[1-9][0-9]{0,9}$", RegexOptions.Compiled);
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

        // Read failures and invalid values, which a reader treats as "not this".
        public static bool Unreadable(Exception error) =>
            error is IOException || error is InvalidDataException || error is ArgumentException || error is OverflowException ||
            error is UnauthorizedAccessException;

        public static byte[] IdTwice(long personId)
        {
            if (personId < 0 || personId > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(personId), "FM24 person IDs are 32-bit.");
            var needle = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(needle, (uint)personId);
            BinaryPrimitives.WriteUInt32LittleEndian(needle.AsSpan(4), (uint)personId);
            return needle;
        }

        // Addresses where <id><id> appears in FM24's writable memory.
        public static IEnumerable<long> RecordAddresses(GameProcess game, long personId, bool aligned = true)
        {
            var needle = IdTwice(personId);
            foreach (var chunk in game.Scan(needle.Length - 1))
                foreach (long address in Find(chunk, needle, aligned ? 4 : 1))
                    yield return address;
        }

        // Where ``needle`` is in the chunk, at addresses that are multiples of ``alignment``.
        public static List<long> Find(ScanChunk chunk, byte[] needle, int alignment = 1)
        {
            var found = new List<long>();
            var data = chunk.Span;
            int offset = 0;
            while (true)
            {
                int at = data.Slice(offset).IndexOf(needle);
                if (at < 0)
                    break;
                offset += at;
                if ((chunk.Address + offset) % alignment == 0)
                    found.Add(chunk.Address + offset);
                offset++;
            }
            return found;
        }

        // The vtable (from the image header) of the object whose record is at ``address``.
        public static long ObjectClass(GameProcess game, long address) => game.U64(address - 12) - game.ImageBase;

        // A person's canonical record: the person object's class (copies of the
        // record elsewhere have no object around them).
        public static bool IsPersonRecord(GameProcess game, long address)
        {
            try
            {
                return game.Layout.PersonVtables.ContainsKey(ObjectClass(game, address));
            }
            catch (Exception error) when (Unreadable(error))
            {
                return false;
            }
        }

        // "player", "staff", or null when the record cannot be classified.
        public static string PersonKind(GameProcess game, long address, long recordType)
        {
            game.Layout.PersonVtables.TryGetValue(ObjectClass(game, address), out string kind);
            if (kind != "player" || recordType == 4 || recordType == 5)
                return kind;
            var ratings = game.Read(address + game.Layout.PositionRatings, PositionCount);
            return ratings.All(value => value >= 1 && value <= 20) && ratings.Max() == 20 ? "player" : null;
        }

        // The record's current game year and birth date (InvalidDataException if
        // impossible). ``raw`` is the record's first bytes, if already read.
        public static (int Year, DateTime Birth) RecordDates(GameProcess game, long address, byte[] raw = null)
        {
            int offset = game.Layout.RecordYear;
            ReadOnlySpan<byte> values = raw != null && offset >= 0 && offset + 6 <= raw.Length
                ? raw.AsSpan(offset, 6) : game.Read(address + offset, 6);
            int year = BinaryPrimitives.ReadUInt16LittleEndian(values);
            int day = BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(2));
            int birthYear = BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(4));
            if (birthYear < 1 || birthYear > 9999 || day < 1)
                throw new InvalidDataException("Invalid FM24 birth date.");
            var birth = new DateTime(birthYear, 1, 1).AddDays(day - 1);
            if (birth.Year != birthYear)
                throw new InvalidDataException("Invalid FM24 birth date.");
            return (year, birth);
        }

        // A pointer in a person, nation, club or contract object; from ``raw``
        // (memory read from ``rawAddress``) when it holds it.
        public static long ReadPointer(GameProcess game, long address, byte[] raw = null, long rawAddress = 0)
        {
            int size = game.Layout.Pointer;
            long start = address - rawAddress;
            if (raw != null && start >= 0 && start + size <= raw.Length)
                return size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)start))
                                 : BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan((int)start));
            return game.Pointer(address);
        }

        public static PersonIdentity ParseRecord(GameProcess game, long address, long personId)
        {
            try
            {
                var raw = game.Read(address, RecordSize);
                if (!raw.AsSpan(0, 8).SequenceEqual(IdTwice(personId)))
                    return null;
                if (!IsPersonRecord(game, address))
                    return null;
                string kind = PersonKind(game, address, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16)));
                if (kind == null)
                    return null;
                var (gameYear, birth) = RecordDates(game, address, raw);
                if (birth.Year < 1900 || birth.Year > gameYear - 14)
                    return null;
                long namePointer = ReadPointer(game, address + game.Layout.RecordName, raw, address);
                string name = namePointer != 0 ? FmString(game, namePointer) : null;
                if (name == null || name.Length < 3)
                {
                    // No full name, or an unusable one (one save's Hugo Videla: "v"):
                    // "first surname" from the record's name objects. A person with
                    // one name (Nur'alim) has only the surname.
                    string first = NamePart(game, address + game.Layout.FirstNamePart);
                    string surname = NamePart(game, address + game.Layout.SurnamePart);
                    name = surname != null ? (first != null ? first + " " + surname : surname) : null;
                }
                if (name == null || !PrintableName.IsMatch(name))
                    return null;
                return new PersonIdentity(personId, name, birth, gameYear, kind);
            }
            catch (Exception error) when (Unreadable(error))
            {
                return null;
            }
        }

        private static string NamePart(GameProcess game, long address)
        {
            long pointer = game.Pointer(address);
            return pointer != 0 ? FmString(game, game.Pointer(pointer)) : null;
        }

        // An FM {u32 length, UTF-8, 0} string of 1-80 bytes, or null.
        public static string FmString(GameProcess game, long address)
        {
            if (address == 0)
                return null;
            uint length = game.U32(address);
            if (length < 1 || length > 80)
                return null;
            var text = game.Read(address + 4, (int)length + 1);
            if (text[length] != 0)
                return null;
            // Names can hold non-breaking spaces ("Immanuel Ajibode"): plain spaces in the prompt.
            string value = Utf8.GetString(text, 0, (int)length).Replace(' ', ' ');
            return Printable.IsMatch(value) && value == value.Trim() ? value : null;
        }

        // The one running FM24; MemoryUnavailableException says in the player's
        // terms why not.
        public static GameProcess FindGame()
        {
            var candidates = new List<GameProcess>();
            var unreadable = new List<Exception>();
            foreach (var (pid, executable) in Processes.Running())
            {
                if (!Processes.IsFm24(executable))
                    continue;
                try
                {
                    var game = Processes.Open(pid);
                    _ = game.ImageBase;
                    candidates.Add(game);
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
                {
                    unreadable.Add(error);
                }
            }
            if (candidates.Count == 1)
                return candidates[0];
            if (candidates.Count > 0)
                throw new MemoryUnavailableException("More than one FM24 is running. Quit the others and try again.");
            if (unreadable.Count > 0 && OperatingSystem.IsMacOS())
                throw new MemoryUnavailableException("RegenerateFM can't read FM24: its library in the game isn't answering. " +
                                                     "Quit FM24 and start it again from Steam, then try again.");
            if (unreadable.Count > 0)
                throw new MemoryUnavailableException("RegenerateFM can't read the running FM24 (" + unreadable[0].Message + ").");
            throw new MemoryUnavailableException("FM24 isn't running. Start it, open the profile and try again.");
        }

        // The unique canonical record address and validated identity for an ID
        // shown on the FM24 screen. A caller decoding build-specific fields
        // must not pick an arbitrary copy of a person ID from UI buffers or
        // history tables.
        public static (long Address, PersonIdentity Identity) ReadPersonRecord(GameProcess game, string personId)
        {
            if (!PersonIdPattern.IsMatch(personId ?? ""))
                throw new ArgumentException("Invalid FM24 person ID.");
            long number = long.Parse(personId, CultureInfo.InvariantCulture);
            var records = new List<(long, PersonIdentity)>();
            try
            {
                foreach (long address in RecordAddresses(game, number))
                {
                    var identity = ParseRecord(game, address, number);
                    if (identity != null)
                        records.Add((address, identity));
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new MemoryUnavailableException("Could not read FM24 memory: " + error.Message);
            }
            if (records.Count != 1)
                throw new MemoryUnavailableException("Expected one canonical FM24 person record; found " + records.Count + ".");
            return records[0];
        }

        // No control, format, surrogate, private-use or unassigned characters,
        // and no separators other than the space.
        public static bool IsPrintable(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value == ' ')
                    continue;
                switch (Rune.GetUnicodeCategory(rune))
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                    case UnicodeCategory.SpaceSeparator:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                        return false;
                }
            }
            return true;
        }

        // Runs of white space (and the separators U+001C to U+001F) as one
        // space, without any at either end.
        public static string CollapseSpaces(string text) =>
            string.Join(" ", text.Split(SplitAt, StringSplitOptions.RemoveEmptyEntries));

        private static readonly char[] SplitAt = Enumerable.Range(0, 0x3001).Select(code => (char)code)
                                                          .Where(c => char.IsWhiteSpace(c) || (c >= '\u001c' && c <= '\u001f')).ToArray();
    }
}
