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
    // The person has no contract: a free agent, or unemployed staff.
    public sealed class NoContractException : MemoryUnavailableException
    {
        public NoContractException() : base("This FM24 person has no contract.") { }
    }

    // The open FM24 profile, from validated game and UI memory records. Offsets
    // were derived by comparing live FM24 24.4.2 records with several
    // independent player and staff profiles. Every pointer and value is checked
    // before it can be used in a prompt. Nothing here writes to the game.
    public static class ProfileReader
    {
        // FM24's database has only men: women's football arrived in FM26. The
        // prompt gives facial hair only to a person said to be a man.
        public const string Gender = "male";
        // What the prompt calls a player with no club (a plain shirt).
        public const string FreeAgent = "Free agent";

        public static readonly (string Name, int Offset)[] Physical =
        {
            ("Acceleration", 34), ("Agility", 46), ("Balance", 42), ("Jumping Reach", 39),
            ("Natural Fitness", 50), ("Pace", 38), ("Stamina", 37), ("Strength", 36),
        };
        public static readonly (string Name, int Offset)[] Technical =
        {
            ("Corners", 27), ("Crossing", 0), ("Dribbling", 1), ("Finishing", 2), ("First Touch", 22),
            ("Free Kick Taking", 35), ("Heading", 3), ("Long Shots", 4), ("Long Throws", 30), ("Marking", 5),
            ("Passing", 7), ("Penalty Taking", 8), ("Tackling", 9), ("Technique", 23),
        };
        public static readonly (string Name, int Offset)[] Mental =
        {
            ("Aggression", 45), ("Anticipation", 17), ("Bravery", 43), ("Composure", 52), ("Concentration", 53),
            ("Decisions", 18), ("Determination", 51), ("Flair", 26), ("Leadership", 40), ("Off The Ball", 6),
            ("Positioning", 20), ("Teamwork", 28), ("Vision", 10), ("Work Rate", 29),
        };
        public static readonly (string Name, int Offset)[] Goalkeeping =
        {
            ("Aerial Reach", 12), ("Command Of Area", 13), ("Communication", 14), ("Eccentricity", 31),
            ("First Touch", 22), ("Handling", 11), ("Kicking", 15), ("One On Ones", 19), ("Passing", 7),
            ("Punching (Tendency)", 33), ("Reflexes", 21), ("Rushing Out (Tendency)", 32), ("Throwing", 16),
        };
        public static readonly string[] Positions =
        {
            "Goalkeeper", "Sweeper", "Defender (Left)", "Defender (Center)", "Defender (Right)",
            "Defensive Midfielder", "Midfielder (Left)", "Midfielder (Center)", "Midfielder (Right)",
            "Attacking Midfielder (Left)", "Attacking Midfielder (Center)", "Attacking Midfielder (Right)",
            "Striker (Center)", "Wing Back (Left)", "Wing Back (Right)",
        };
        private static readonly Regex Title = new Regex(@"([^\x00\r\n]{3,100}) - ([^\x00\r\n]{3,80}) \(ID:([1-9][0-9]{0,9})\)", RegexOptions.Compiled);
        private static readonly Regex ObjectName = new Regex(@"\Gobject: ([1-9][0-9]{0,9}) - ([^,\x00\r\n]{2,80})[,\x00]", RegexOptions.Compiled);
        private const int RecordHeader = 0x40;

        private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

        // "<id><id> 09 <u32 n><first> <u32 n><surname>" followed by 00, or by a
        // third string, the common name ("Gabriel" "Fortes Chaves" "Gabriel Pec").
        // Returns "first surname", or null.
        public static string AlternateName(ReadOnlySpan<byte> raw)
        {
            if (raw.Length < 20 || raw[8] != 0x09)
                return null;
            int firstSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(9));
            if (firstSize < 1 || firstSize > 40 || 17 + firstSize > raw.Length)
                return null;
            int lastSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(13 + firstSize));
            int end = 17 + firstSize + lastSize;
            if (lastSize < 1 || lastSize > 40 || end >= raw.Length)
                return null;
            var texts = new List<byte[]> { raw.Slice(13, firstSize).ToArray(), raw.Slice(17 + firstSize, lastSize).ToArray() };
            if (raw[end] != 0)
            {
                if (end + 4 > raw.Length)
                    return null;
                int commonSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(end));
                if (commonSize < 1 || commonSize > 40 || end + 4 + commonSize >= raw.Length)
                    return null;
                texts.Add(raw.Slice(end + 4, commonSize).ToArray());
            }
            var decoded = new List<string>();
            foreach (var text in texts)
            {
                try
                {
                    decoded.Add(People.Utf8.GetString(text));
                }
                catch (DecoderFallbackException)
                {
                    return null;
                }
            }
            if (!decoded.All(text => People.IsPrintable(text) && text == text.Trim()))
                return null;
            return decoded[0] + " " + decoded[1];
        }

        // A canonical record whose full-name pointer is empty. Some FM24 players
        // use a separate ID-indexed first/surname record instead of the
        // canonical record's full-name string pointer. Both must agree on ID.
        private static (long, PersonIdentity) RecordWithAlternateName(GameProcess game, string personId)
        {
            long number = long.Parse(personId, CultureInfo.InvariantCulture);
            var records = new List<(long Address, int Year, DateTime Birth, string Kind)>();
            var names = new HashSet<string>();
            foreach (long address in People.RecordAddresses(game, number, aligned: game.Layout.NameRecordsAligned))
            {
                try
                {
                    var raw = game.Read(address, 128);
                    if (address % 4 == 0 && raw.AsSpan(0, 8).SequenceEqual(People.IdTwice(number)) &&
                        People.IsPersonRecord(game, address) && People.RecordTypes.Contains(raw[16]) &&
                        raw[17] == 0 && raw[18] == 0 && raw[19] == 0)
                    {
                        if (game.Pointer(address + game.Layout.RecordName) == 0)
                        {
                            var (year, birth) = People.RecordDates(game, address, raw);
                            string kind = People.PersonKind(game, address, raw[16]);
                            if (birth.Year >= 1900 && birth.Year <= year - 14 && kind != null)
                                records.Add((address, year, birth, kind));
                        }
                    }
                    string name = AlternateName(raw);
                    if (name != null)
                        names.Add(name);
                }
                catch (Exception error) when (People.Unreadable(error))
                {
                }
            }
            if (records.Count == 1 && names.Count == 0)
            {
                // Some reserve and youth players have neither name string resident;
                // FM24 then holds "object: <id> - <name>" texts for people just viewed.
                if (ObjectNames(game, new HashSet<string> { personId }).TryGetValue(personId, out var found))
                    names = found;
            }
            if (records.Count != 1 || names.Count != 1)
                throw new MemoryUnavailableException("Could not validate the alternate FM24 name record.");
            var record = records[0];
            return (record.Address, new PersonIdentity(number, names.First(), record.Birth, record.Year, record.Kind));
        }

        // Names in FM24's "object: <id> - <name>" texts for these people, in one pass.
        public static Dictionary<string, HashSet<string>> ObjectNames(GameProcess game, HashSet<string> personIds)
        {
            var found = new Dictionary<string, HashSet<string>>();
            foreach (var chunk in game.Scan(128))
                CollectObjectNames(chunk.Span, personIds, found);
            return found;
        }

        private static readonly byte[] ObjectPrefix = Bytes("object: ");

        public static void CollectObjectNames(ReadOnlySpan<byte> data, HashSet<string> personIds, Dictionary<string, HashSet<string>> found)
        {
            int offset = 0;
            while (true)
            {
                int at = data.Slice(offset).IndexOf(ObjectPrefix);
                if (at < 0)
                    break;
                offset += at;
                // The longest match: "object: " + 10 digits + " - " + 80 + 1.
                var window = data.Slice(offset, Math.Min(data.Length - offset, 8 + 10 + 3 + 80 + 1));
                var match = ObjectName.Match(Encoding.Latin1.GetString(window));
                if (match.Success && personIds.Contains(match.Groups[1].Value))
                {
                    string name;
                    try
                    {
                        name = People.Utf8.GetString(Encoding.Latin1.GetBytes(match.Groups[2].Value)).Trim();
                    }
                    catch (DecoderFallbackException)
                    {
                        name = "";
                    }
                    if (name.Length > 0 && People.IsPrintable(name))
                    {
                        if (!found.TryGetValue(match.Groups[1].Value, out var names))
                            found[match.Groups[1].Value] = names = new HashSet<string>();
                        names.Add(name);
                    }
                }
                offset += 8;
            }
        }

        // The person's canonical record and identity. Records stay at one address
        // while the game runs, so where each was found is remembered with its
        // header; a changed header means searching again. ``known`` is the
        // record's address if already known (from FM24's person table), which is
        // checked instead of searching memory for the ID.
        public static (long Address, PersonIdentity Identity) PersonRecord(GameProcess game, string personId, long known = 0)
        {
            if (known != 0)
            {
                var identity = People.ParseRecord(game, known, long.Parse(personId, CultureInfo.InvariantCulture));
                if (identity != null)
                {
                    Remember(game, personId, known, identity);
                    return (known, identity);
                }
            }
            (long Address, PersonIdentity Identity, byte[] Header) cached;
            bool have;
            lock (game.CacheLock)
                have = game.Records.TryGetValue(personId, out cached);
            if (have)
            {
                try
                {
                    if (game.Read(cached.Address, RecordHeader).AsSpan().SequenceEqual(cached.Header))
                        return (cached.Address, cached.Identity);
                }
                catch (IOException)
                {
                }
                lock (game.CacheLock)
                    game.Records.Remove(personId);
            }
            (long, PersonIdentity) found;
            try
            {
                found = People.ReadPersonRecord(game, personId);
            }
            catch (MemoryUnavailableException)
            {
                found = RecordWithAlternateName(game, personId);
            }
            Remember(game, personId, found.Item1, found.Item2);
            return found;
        }

        private static void Remember(GameProcess game, string personId, long address, PersonIdentity identity)
        {
            var header = game.Read(address, RecordHeader);
            lock (game.CacheLock)
            {
                if (game.Records.Count > 512)
                    game.Records.Clear();
                game.Records[personId] = (address, identity, header);
            }
        }

        private static string Text(GameProcess game, long address, int limit = 80)
        {
            if (address == 0)
                throw new MemoryUnavailableException("Missing FM24 string pointer.");
            uint size = game.U32(address);
            if (size < 1 || size > limit)
                throw new MemoryUnavailableException("Invalid FM24 string length.");
            var raw = game.Read(address + 4, (int)size + 1);
            if (raw[size] != 0)
                throw new MemoryUnavailableException("Unterminated FM24 string.");
            string value;
            try
            {
                value = People.Utf8.GetString(raw, 0, (int)size);
            }
            catch (DecoderFallbackException)
            {
                throw new MemoryUnavailableException("Invalid FM24 string content.");
            }
            if (!People.IsPrintable(value))
                throw new MemoryUnavailableException("Invalid FM24 string content.");
            return value;
        }

        private static readonly byte[] IdMarker = Bytes("(ID:"), YearsOld = Bytes(" years old");

        // ID-bearing titles and ages in one chunk of FM24's memory. Only rendered
        // UI text objects count, not discarded formatted strings.
        public static void CollectUiChunk(ReadOnlySpan<byte> data, Dictionary<string, List<string>> titles, HashSet<int> ages)
        {
            int offset = 0;
            while (true)
            {
                int at = data.Slice(offset).IndexOf(IdMarker);
                if (at < 0)
                    break;
                offset += at;
                int limit = Math.Min(data.Length, offset + 16);
                int endId = offset + 4 < limit ? data.Slice(offset + 4, limit - offset - 4).IndexOf((byte)')') : -1;
                if (endId >= 0)
                    endId += offset + 4;
                var digits = endId >= 0 ? data.Slice(offset + 4, endId - offset - 4) : ReadOnlySpan<byte>.Empty;
                if (digits.Length >= 1 && digits.Length <= 10 && AllDigits(digits) && digits[0] != (byte)'0')
                {
                    string personId = Encoding.ASCII.GetString(digits);
                    int beginning = Math.Max(0, offset - 190);
                    string window = Encoding.Latin1.GetString(data.Slice(beginning, endId + 1 - beginning));
                    Match title = null;
                    foreach (Match candidate in Title.Matches(window))
                        if (candidate.Index + candidate.Length == window.Length && candidate.Groups[3].Value == personId)
                        {
                            title = candidate;
                            break;
                        }
                    int titleStart = title != null ? beginning + title.Index : -1;
                    bool live = titleStart >= 8 &&
                                data.Slice(titleStart - 8, 4).SequenceEqual(new byte[] { 1, 0, 0, 0 }) &&
                                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(titleStart - 4)) == title.Length;
                    if (title != null && live)
                    {
                        if (!titles.TryGetValue(personId, out var list))
                            titles[personId] = list = new List<string>();
                        list.Add(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(title.Groups[1].Value + " - " + title.Groups[2].Value)));
                    }
                }
                offset += 4;
            }
            offset = 0;
            while (true)
            {
                int at = data.Slice(offset).IndexOf(YearsOld);
                if (at < 0)
                    break;
                offset += at;
                if (offset >= 2 && AllDigits(data.Slice(offset - 2, 2)))
                    ages.Add((data[offset - 2] - '0') * 10 + data[offset - 1] - '0');
                offset += 10;
            }
        }

        private static bool AllDigits(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                if (b < '0' || b > '9')
                    return false;
            return data.Length > 0;
        }

        // ID-bearing titles and ages from current game UI buffers. Only a
        // selection hint: each candidate ID must also resolve to exactly one
        // canonical person record, and ambiguous screens fail closed.
        private static (Dictionary<string, List<string>>, HashSet<int>) ScanUi(GameProcess game)
        {
            var titles = new Dictionary<string, List<string>>();
            var ages = new HashSet<int>();
            foreach (var chunk in game.Scan(256))
                CollectUiChunk(chunk.Span, titles, ages);
            return (titles, ages);
        }

        // Stored 1-100; shown 1-20. Raw 1 and 2 are shown as 1 (seen on young
        // reserve players).
        private static int Rating(int raw)
        {
            int value = Math.Max(1, (raw + 2) / 5);
            if (raw < 1 || raw > 100 || value < 1 || value > 20)
                throw new MemoryUnavailableException("Invalid FM24 attribute value.");
            return value;
        }

        public static Dictionary<string, object> Attributes(byte[] raw, (string Name, int Offset)[] mapping)
        {
            var values = new Dictionary<string, object>();
            foreach (var (name, offset) in mapping)
                values[name] = Rating(raw[offset]);
            return values;
        }

        private static string Height(int rawCm)
        {
            if (rawCm < 140 || rawCm > 220)
                throw new MemoryUnavailableException("Invalid FM24 height.");
            int inches = (int)Math.Round(rawCm / 2.54, MidpointRounding.ToEven);
            return inches / 12 + "'" + inches % 12 + "\"";
        }

        // The object at ``address`` has the layout's class at ``vtable``.
        private static bool Is(GameProcess game, long address, long vtable) => game.U64(address) == game.ImageBase + vtable;

        private static string Nation(GameProcess game, long record)
        {
            long nation = game.Pointer(record + game.Layout.Nation);
            // The u32 at +8 is 0 for some nations (Algeria), so only the class tells.
            if (!Is(game, nation, game.Layout.NationVtable))
                throw new MemoryUnavailableException("Invalid FM24 nation object.");
            return Text(game, game.Pointer(nation + 0x30), limit: 40);
        }

        // A db::TEAM's name. A first team is named by its club details (db::CLUB,
        // at +0x30, with the same ID). A club's other teams point at the same
        // club details: some carry their own name (+0x18: "Jong Sparta
        // Rotterdam"); youth and reserve teams FM names on screen ("Reading
        // Under 21s") have none, and their club's name is used, since they
        // wear its kit.
        private static string TeamName(GameProcess game, long team)
        {
            if (!Is(game, team, game.Layout.TeamVtable))
                throw new MemoryUnavailableException("Invalid FM24 club object.");
            long details = game.Pointer(team + 0x30);
            if (!Is(game, details, game.Layout.ClubVtable))
                throw new MemoryUnavailableException("FM24 club details do not match the club.");
            long own = game.Pointer(team + 0x18);
            if (own != 0 && game.U32(details + 12) != game.U32(team + 12))
                return Text(game, own, limit: 80);
            return Text(game, game.Pointer(details + 0xC8), limit: 80);
        }

        // The club holding the person's contract (the parent club during a loan).
        public static string Club(GameProcess game, long record)
        {
            long contract = game.Pointer(record + game.Layout.Contract);
            if (contract == 0)
                throw new NoContractException();  // About one player in six in a 2026-10-19 Reading save.
            if (game.Pointer(contract + 8) != record - 12)
                throw new MemoryUnavailableException("FM24 contract points to another person.");
            return TeamName(game, game.Pointer(contract + 0x10));
        }

        // The club a player plays for now, so a loaned player wears the loan
        // club's kit. A player's record holds it: the loan club while on loan,
        // otherwise the contract club. Staff records leave it empty.
        private static string PlayingClub(GameProcess game, long record, string fallback)
        {
            long club = game.Pointer(record + game.Layout.PlayingClub);
            return club != 0 ? TeamName(game, club) : fallback;
        }

        private static string Position(GameProcess game, long record)
        {
            var ratings = game.Read(record + game.Layout.PositionRatings, Positions.Length);
            if (ratings.Any(value => value < 1 || value > 20))
                throw new MemoryUnavailableException("Invalid FM24 position ratings.");
            int best = ratings.Max();
            if (best < 15)
                throw new MemoryUnavailableException("No validated FM24 player position.");
            return Positions[Array.IndexOf(ratings, (byte)best)];
        }

        // Prompt fields for the uniquely selected profile. ``identityOnly`` stops
        // after the verified name, age and club, which is all Update face needs;
        // another club's player can have attributes that are not fully known.
        // ``screen`` names the person and title shown, so the UI texts aren't
        // searched for "(ID:n)" titles, which need screen IDs. With the record
        // and the ages it shows too, FM24's memory isn't searched at all.
        public static (string PersonId, ProfileSnapshot Profile) ReadOpenProfile(GameProcess game, bool identityOnly = false,
                                                                                 OpenScreen screen = null)
        {
            Dictionary<string, List<string>> titles;
            HashSet<int> ages;
            if (screen != null && screen.Ages.Count > 0)
                (titles, ages) = (new Dictionary<string, List<string>>(), new HashSet<int>(screen.Ages));
            else
                (titles, ages) = ScanUi(game);
            if (screen != null)
                titles = new Dictionary<string, List<string>> { [screen.PersonId] = new List<string> { screen.Title } };
            var people = new List<(string Id, long Record, PersonIdentity Identity, List<string> Names)>();
            foreach (var (personId, names) in titles)
            {
                (long, PersonIdentity) found;
                try
                {
                    found = PersonRecord(game, personId, screen?.Record ?? 0);
                }
                catch (Exception error) when (error is MemoryUnavailableException || error is ArgumentException)
                {
                    continue;  // Club/team IDs are also present in the title cache.
                }
                if (names.Count > 0)
                    people.Add((personId, found.Item1, found.Item2, names));
            }
            if (people.Count != 1)
                throw new MemoryUnavailableException("Expected one open FM24 person profile; found " + people.Count + ".");
            var person = people[0];
            var matching = new HashSet<int>(ages);
            matching.IntersectWith(PossibleAges(person.Identity, screen?.Today));
            if (matching.Count != 1)
                throw new MemoryUnavailableException("Could not verify the open FM24 person's age.");
            return (person.Id, Profile(game, person.Record, person.Identity, person.Names, matching.First(), identityOnly));
        }

        // The ages the person can be: exactly, on the in-game date shown, else
        // the two the record's year allows.
        private static HashSet<int> PossibleAges(PersonIdentity identity, DateTime? today) =>
            today != null ? new HashSet<int> { identity.AgeOn(today.Value) }
                          : new HashSet<int> { identity.PossibleAges.Item1, identity.PossibleAges.Item2 };

        // A person ReadOpenProfile found, read again without a memory search, to
        // check that nothing changed during capture. ``title`` is the open
        // screen's "Role - Club" header text, ``age`` the age read the first
        // time and ``today`` the in-game date shown, if any.
        public static (string PersonId, ProfileSnapshot Profile) RereadProfile(GameProcess game, string personId, string title, int age,
                                                                               bool identityOnly = false, DateTime? today = null)
        {
            var (record, identity) = PersonRecord(game, personId);
            if (!PossibleAges(identity, today).Contains(age))
                throw new MemoryUnavailableException("The FM24 person's age changed during capture.");
            return (personId, Profile(game, record, identity, new List<string> { title }, age, identityOnly));
        }

        private static (string Before, string After) SplitTitle(string title)
        {
            int at = title.LastIndexOf(" - ", StringComparison.Ordinal);
            return at < 0 ? (title, null) : (title.Substring(0, at), title.Substring(at + 3));
        }

        internal static ProfileSnapshot Profile(GameProcess game, long record, PersonIdentity identity, List<string> names, int age,
                                                bool identityOnly)
        {
            string nation = Nation(game, record);
            string club;
            try
            {
                club = Club(game, record);
            }
            catch (NoContractException)
            {
                club = null;
            }
            string playing;
            List<string> shown;
            if (club == null)
            {
                // A free agent's header names their national team, if any, instead
                // of a club, so there is no club to check it against.
                playing = identity.Kind == "player" ? PlayingClub(game, record, FreeAgent) : "";
                club = playing;
                shown = new List<string>(names);
            }
            else
            {
                playing = identity.Kind == "player" ? PlayingClub(game, record, club) : club;
                // The header names the contract club, or for a reserve player the team they play in.
                string contract = club, played = playing;
                shown = names.Where(title => title.EndsWith(" - " + contract, StringComparison.Ordinal) ||
                                             title.EndsWith(" - " + played, StringComparison.Ordinal)).ToList();
                if (shown.Count == 0)
                {
                    // A club's youth or reserve team that FM names only on screen:
                    // "Reading Under 21s" for a player of Reading's.
                    var teams = new HashSet<string>(names.Where(title => title.Contains(" - ")).Select(title => SplitTitle(title).After));
                    var named = teams.Where(team => team.StartsWith(played + " ", StringComparison.Ordinal)).ToList();
                    if (named.Count == 1)
                    {
                        playing = named[0];
                        shown = names.Where(title => title.EndsWith(" - " + named[0], StringComparison.Ordinal)).ToList();
                    }
                }
            }
            if (shown.Count == 0)
                throw new MemoryUnavailableException("FM24 title and live contract club disagree.");
            if (identity.Kind == "staff")
            {
                var jobs = new HashSet<string>(shown.Select(title => SplitTitle(title).Before.Split('\0').Last().Trim()));
                if (jobs.Count != 1 || jobs.First().Length == 0)
                    throw new MemoryUnavailableException("Could not verify the FM24 staff job title.");
                if (identityOnly)
                    return new ProfileSnapshot { Type = "staff", Gender = Gender, Name = identity.Name, Age = age, Club = club };
                return new ProfileSnapshot
                {
                    Type = "staff", Gender = Gender, Name = identity.Name, Age = age, Nationality = nation,
                    JobTitle = jobs.First(), Club = club,
                };
            }
            if (identityOnly)
                return new ProfileSnapshot { Type = "player", Gender = Gender, Name = identity.Name, Age = age, Club = playing };
            var raw = game.Read(record + game.Layout.Attributes, 54);
            int heightCm = game.U16(record + game.Layout.Height);
            string rated = Position(game, record);
            var roles = new HashSet<string>(shown.Select(title => SplitTitle(title).Before.Trim()));
            if (roles.Count != 1)
                throw new MemoryUnavailableException("Could not verify the FM24 position title.");
            string position = roles.First();
            string family = rated.Split(" (")[0];
            if (!position.Contains(family) && !(family == "Midfielder" && position.Contains("M (")))
                throw new MemoryUnavailableException("FM24 position ratings and title disagree.");
            var profile = new ProfileSnapshot
            {
                Type = "player", Gender = Gender, Name = identity.Name, Age = age, Nationality = nation,
                Position = position, Club = playing, Height = Height(heightCm),
                Physical = Attributes(raw, Physical), Mental = Attributes(raw, Mental),
            };
            if (rated == "Goalkeeper")
                profile.Goalkeeping = Attributes(raw, Goalkeeping);
            else
                profile.Technical = Attributes(raw, Technical);
            return profile;
        }

        // The club most Squad rows are contracted to (loanees belong elsewhere).
        public static string SquadClub(GameProcess game, IList<long> records)
        {
            var clubs = new Dictionary<string, int>();
            foreach (long record in records)
            {
                try
                {
                    string club = Club(game, record);
                    clubs[club] = clubs.GetValueOrDefault(club) + 1;
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                }
            }
            if (clubs.Count == 0)
                throw new MemoryUnavailableException("Could not read the FM24 Squad club.");
            var (best, count) = MostCommon(clubs);
            if (count * 2 <= records.Count)
                throw new MemoryUnavailableException("Could not identify the FM24 Squad club.");
            return best;
        }

        // The team most Squad rows play for (a reserve team's name on its own
        // Squad view), or "" if no team has most of them.
        public static string ShownTeam(GameProcess game, IList<long> records)
        {
            var teams = new Dictionary<string, int>();
            foreach (long record in records)
            {
                try
                {
                    string team = PlayingClub(game, record, Club(game, record));
                    teams[team] = teams.GetValueOrDefault(team) + 1;
                }
                catch (Exception error) when (error is MemoryUnavailableException || People.Unreadable(error))
                {
                }
            }
            if (teams.Count == 0)
                return "";
            var (best, count) = MostCommon(teams);
            return count * 2 > records.Count ? best : "";
        }

        // The highest count, the first counted on a tie.
        private static (string, int) MostCommon(Dictionary<string, int> counts)
        {
            var best = counts.First();
            foreach (var entry in counts)
                if (entry.Value > best.Value)
                    best = entry;
            return (best.Key, best.Value);
        }

        // Prompt fields for a Squad row's player, read by record without opening
        // it. ``name`` is the row's display name and ``club`` the Squad's club,
        // used unless the player plays elsewhere (on loan). The position comes
        // from the player's position ratings.
        public static ProfileSnapshot SquadPlayerProfile(GameProcess game, long record, string name, string club, DateTime today)
        {
            var raw = game.Read(record, 0x40);
            uint number = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (!raw.AsSpan(0, 8).SequenceEqual(People.IdTwice(number)) || People.PersonKind(game, record, raw[16]) != "player")
                throw new MemoryUnavailableException("Not a validated FM24 player record.");
            DateTime birth;
            try
            {
                birth = People.RecordDates(game, record, raw).Birth;
            }
            catch (InvalidDataException)
            {
                throw new MemoryUnavailableException("Invalid FM24 birth date.");
            }
            int age = today.Year - birth.Year - ((today.Month, today.Day).CompareTo((birth.Month, birth.Day)) < 0 ? 1 : 0);
            if (age < 14 || age > 60)
                throw new MemoryUnavailableException("Invalid FM24 player age.");
            var ratings = game.Read(record + game.Layout.Attributes, 54);
            int heightCm = game.U16(record + game.Layout.Height);
            string position = Position(game, record);
            var profile = new ProfileSnapshot
            {
                Type = "player", Gender = Gender, Name = name, Age = age, Nationality = Nation(game, record),
                Position = position, Club = PlayingClub(game, record, club), Height = Height(heightCm),
                Physical = Attributes(ratings, Physical), Mental = Attributes(ratings, Mental),
            };
            if (position == "Goalkeeper")
                profile.Goalkeeping = Attributes(ratings, Goalkeeping);
            else
                profile.Technical = Attributes(ratings, Technical);
            return profile;
        }
    }
}
