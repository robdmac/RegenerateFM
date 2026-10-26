using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using StbImageSharp;

namespace RegenerateFM.Desktop.FM24
{
    // The portrait FM24 currently uses for a person, for Update face.
    //
    // FM24 maps pictures with config.xml files: each record maps a file stem in
    // the same folder to "graphics/pictures/person/<id>/portrait". Pictures in
    // the user's graphics folder replace the game's bundled ones, and
    // RegenerateFM's own folder is checked first. Bundled squad photos are a
    // JPEG plus an optional "<name>_[mask].png" whose values become the alpha
    // channel.
    public static class ExistingPortrait
    {
        private static readonly string[] ImageSuffixes = { ".png", ".jpg", ".jpeg" };
        private const long MaxConfig = 8 << 20;
        private const long MaxImage = 16 << 20;
        private const string Prefix = "graphics/pictures/person/";
        // For "has this person a face?" while FM24's portrait menu opens: parsed
        // configs are kept until the file changes, and the folder walk for
        // configs outside RegenerateFM's own folder is repeated at most every
        // ConfigRescan seconds.
        public const double ConfigRescan = 30.0;

        private static readonly object BundledLock = new object();
        private static (string Key, Dictionary<string, GameArchive.Entry> Entries, Dictionary<string, string> Mapped)? _bundled;
        private static readonly object IndexLock = new object();
        private static readonly Dictionary<string, (double At, List<string> Configs)> ConfigFolders = new Dictionary<string, (double, List<string>)>();
        private static readonly Dictionary<string, ((DateTime, long) Key, Dictionary<string, List<string>> People)> ConfigPeople =
            new Dictionary<string, ((DateTime, long), Dictionary<string, List<string>>)>();
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        public static Func<double> Clock = () => Uptime.Elapsed.TotalSeconds;

        // For tests: forgets everything indexed.
        public static void Forget()
        {
            lock (BundledLock)
                _bundled = null;
            lock (IndexLock)
            {
                ConfigFolders.Clear();
                ConfigPeople.Clear();
            }
        }

        private static HashSet<string> Targets(string personId) =>
            new HashSet<string> { Prefix + personId + "/portrait", Prefix + "r-" + personId + "/portrait" };

        private static XElement Parse(byte[] xml)
        {
            try
            {
                using var stream = new MemoryStream(xml);
                return XDocument.Load(stream).Root;
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static IEnumerable<XElement> Records(XElement root) =>
            root == null ? Enumerable.Empty<XElement>() : root.DescendantsAndSelf("record");

        private static string Normalized(string target) => (target ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

        // The stems mapped to the person, in config order.
        private static List<string> Stems(byte[] config, HashSet<string> targets) =>
            Records(Parse(config)).Where(record => targets.Contains(Normalized((string)record.Attribute("to"))) &&
                                                   !string.IsNullOrEmpty((string)record.Attribute("from")))
                                  .Select(record => (string)record.Attribute("from")).ToList();

        // Whether a PNG or JPEG has all its parts, as Pillow checks: stb_image
        // fills in a truncated one (and loops on a JPEG cut in its first segment).
        public static bool Complete(byte[] data)
        {
            if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                for (long at = 8; at + 12 <= data.Length;)
                {
                    long length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at));
                    if (at + 12 + length > data.Length)
                        return false;
                    if (data.AsSpan((int)at + 4, 4).SequenceEqual("IEND"u8))
                        return true;
                    at += 12 + length;
                }
                return false;
            }
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            {
                // Segments up to the first scan, then the end-of-image marker
                // (a 0xFF in scan data is always followed by 0x00 or a restart marker).
                for (int at = 2; at + 4 <= data.Length;)
                {
                    if (data[at] != 0xFF)
                        return false;
                    byte marker = data[at + 1];
                    if (marker == 0xFF)
                    {
                        at++;
                        continue;
                    }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        at += 2;
                        continue;
                    }
                    if (marker == 0xD8 || marker == 0xD9)
                        return false;
                    int length = data[at + 2] << 8 | data[at + 3];
                    if (length < 2 || at + 2 + length > data.Length)
                        return false;
                    at += 2 + length;
                    if (marker == 0xDA)
                        return data.AsSpan(at).IndexOf(new byte[] { 0xFF, 0xD9 }) >= 0;
                }
                return false;
            }
            return true;  // Not PNG or JPEG: the decoder refuses it.
        }

        // ``image`` (any PNG or JPEG) as an RGBA PNG, with ``mask``'s values (its
        // alpha, or its brightness) as transparency.
        public static byte[] RgbaPng(byte[] image, byte[] mask)
        {
            if (!Complete(image))
                throw new InvalidDataException("The portrait could not be read: the image file is truncated.");
            if (mask != null && !Complete(mask))
                throw new InvalidDataException("The portrait mask could not be read: the image file is truncated.");
            ImageResult picture;
            try
            {
                picture = ImageResult.FromMemory(image, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                throw new InvalidDataException("The portrait could not be read: " + error.Message);
            }
            var rgba = picture.Data;
            if (mask != null)
            {
                ImageResult alpha;
                try
                {
                    alpha = ImageResult.FromMemory(mask, ColorComponents.RedGreenBlueAlpha);
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
                {
                    throw new InvalidDataException("The portrait mask could not be read: " + error.Message);
                }
                if (alpha.Width != picture.Width || alpha.Height != picture.Height)
                    throw new InvalidDataException("The portrait mask does not match its photo.");
                // As Pillow: an RGBA mask's alpha, otherwise its brightness.
                bool hasAlpha = alpha.SourceComp == ColorComponents.RedGreenBlueAlpha;
                for (int i = 0; i < picture.Width * picture.Height; i++)
                {
                    int r = alpha.Data[i * 4], g = alpha.Data[i * 4 + 1], b = alpha.Data[i * 4 + 2];
                    // Pillow's "L": ITU-R 601-2 luma, rounded.
                    rgba[i * 4 + 3] = hasAlpha ? alpha.Data[i * 4 + 3] : (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                }
            }
            return Png.Encode(rgba, picture.Width, picture.Height, bottomUp: false);
        }

        private static IEnumerable<string> ConfigsUnder(string graphicsRoot, string preferred)
        {
            var found = new List<string>();
            void Walk(string folder)
            {
                string config = Path.Combine(folder, "config.xml");
                if (File.Exists(config) && !SameFolder(folder, preferred))
                    found.Add(config);
                IEnumerable<string> children;
                try
                {
                    children = Directory.GetDirectories(folder).OrderBy(name => name, StringComparer.Ordinal).ToList();
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    return;
                }
                foreach (string child in children)
                    Walk(child);
            }
            if (Directory.Exists(graphicsRoot))
                Walk(graphicsRoot);
            return found;
        }

        private static bool SameFolder(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.Ordinal);

        private static (byte[], string)? UserPortrait(string graphicsRoot, string preferred, HashSet<string> targets)
        {
            var configs = new List<string> { Path.Combine(preferred, "config.xml") };
            configs.AddRange(ConfigsUnder(graphicsRoot, preferred));
            foreach (string config in configs)
            {
                List<string> stems;
                try
                {
                    var info = new FileInfo(config);
                    if (!info.Exists || info.Length > MaxConfig)
                        continue;
                    stems = Stems(File.ReadAllBytes(config), targets);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    continue;
                }
                string folder = Path.GetDirectoryName(config);
                foreach (string stem in stems)
                    foreach (string suffix in ImageSuffixes)
                    {
                        var path = new FileInfo(Path.Combine(folder, stem + suffix));
                        if (path.Exists && path.Length <= MaxImage)
                        {
                            string mask = Path.Combine(folder, stem + "_[mask].png");
                            return (RgbaPng(File.ReadAllBytes(path.FullName), File.Exists(mask) ? File.ReadAllBytes(mask) : null), path.FullName);
                        }
                    }
            }
            return null;
        }

        // The archive's files, and the bundled picture stem of each mapped
        // person. Built once per archive version (about half a second).
        private static (Dictionary<string, GameArchive.Entry>, Dictionary<string, string>) BundledIndex(string archive)
        {
            string key = archive + "|" + File.GetLastWriteTimeUtc(archive).Ticks;
            lock (BundledLock)
            {
                if (_bundled is var (known, knownEntries, knownMapped) && known == key)
                    return (knownEntries, knownMapped);
                var entries = GameArchive.ReadIndex(archive);
                var mapped = new Dictionary<string, string>();
                foreach (var (path, entry) in entries)
                {
                    if (!path.StartsWith("/graphics/pictures/", StringComparison.Ordinal) || !path.EndsWith("/config.xml", StringComparison.Ordinal))
                        continue;
                    string folder = path.Substring(0, path.Length - "config.xml".Length);
                    XElement root;
                    try
                    {
                        root = Parse(GameArchive.ReadFile(archive, entry));
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }
                    foreach (var record in Records(root))
                    {
                        string target = ((string)record.Attribute("to") ?? "").Trim().ToLowerInvariant();
                        string stem = (string)record.Attribute("from");
                        if (!string.IsNullOrEmpty(stem) && target.StartsWith(Prefix, StringComparison.Ordinal) &&
                            target.EndsWith("/portrait", StringComparison.Ordinal))
                        {
                            string person = target.Substring(Prefix.Length, target.Length - Prefix.Length - "/portrait".Length);
                            if (person.StartsWith("r-", StringComparison.Ordinal))
                                person = person.Substring(2);
                            mapped.TryAdd(person, folder + stem.ToLowerInvariant());
                        }
                    }
                }
                _bundled = (key, entries, mapped);
                return (entries, mapped);
            }
        }

        private static string Archive(string gameDir) => Path.Combine(gameDir, "data", "game", "graphics.fmf");

        private static (byte[], string)? BundledPortrait(string gameDir, string personId)
        {
            string archive = Archive(gameDir);
            if (!File.Exists(archive))
                return null;
            var (entries, mapped) = BundledIndex(archive);
            if (!mapped.TryGetValue(personId, out string stem))
                return null;
            foreach (string suffix in ImageSuffixes)
                if (entries.TryGetValue(stem + suffix, out var entry))
                {
                    entries.TryGetValue(stem + "_[mask].png", out var mask);
                    return (RgbaPng(GameArchive.ReadFile(archive, entry), mask != null ? GameArchive.ReadFile(archive, mask) : null),
                            "FM24 bundled " + entry.Path);
                }
            return null;
        }

        // The person's current portrait as an RGBA PNG and where it came from.
        // ``graphicsDir`` is RegenerateFM's own folder inside FM24's graphics
        // folder. Null when FM24 would show no picture (it draws a silhouette).
        public static (byte[] Png, string Source)? Find(string personId, string graphicsDir, string gameDir)
        {
            Portraits.ValidateId(personId);
            graphicsDir = Path.TrimEndingDirectorySeparator(graphicsDir);
            var found = UserPortrait(Path.GetDirectoryName(graphicsDir), graphicsDir, Targets(personId));
            if (found == null && gameDir != null)
                found = BundledPortrait(gameDir, personId);
            return found;
        }

        // Person ID -> mapped file stems for one config.xml (kept by its time and size).
        private static Dictionary<string, List<string>> ConfigFor(string config)
        {
            var info = new FileInfo(config);
            var key = (info.LastWriteTimeUtc, info.Length);
            if (ConfigPeople.TryGetValue(config, out var cached) && cached.Key == key)
                return cached.People;
            var people = new Dictionary<string, List<string>>();
            if (info.Length <= MaxConfig)
                foreach (var record in Records(Parse(File.ReadAllBytes(config))))
                {
                    string target = Normalized((string)record.Attribute("to"));
                    string stem = (string)record.Attribute("from");
                    if (!string.IsNullOrEmpty(stem) && target.StartsWith(Prefix, StringComparison.Ordinal) &&
                        target.EndsWith("/portrait", StringComparison.Ordinal))
                    {
                        string person = target.Substring(Prefix.Length, target.Length - Prefix.Length - "/portrait".Length);
                        if (person.StartsWith("r-", StringComparison.Ordinal))
                            person = person.Substring(2);
                        if (!people.TryGetValue(person, out var stems))
                            people[person] = stems = new List<string>();
                        stems.Add(stem);
                    }
                }
            ConfigPeople[config] = (key, people);
            return people;
        }

        private static List<string> Configs(string graphicsRoot, string preferred, double now)
        {
            if (!ConfigFolders.TryGetValue(graphicsRoot, out var cached) || now - cached.At > ConfigRescan)
                ConfigFolders[graphicsRoot] = cached = (now, ConfigsUnder(graphicsRoot, preferred).ToList());
            var configs = new List<string> { Path.Combine(preferred, "config.xml") };
            configs.AddRange(cached.Configs);
            return configs;
        }

        // Thrown without ``wait`` while the game archive's index is being built.
        public sealed class StillIndexingException : InvalidOperationException
        {
            public StillIndexingException() : base("FM24's bundled pictures are still being indexed.") { }
        }

        // Whether the archive's index is built; if not, it is built in the background.
        private static bool BundledReady(string archive)
        {
            string key;
            try
            {
                key = archive + "|" + File.GetLastWriteTimeUtc(archive).Ticks;
            }
            catch (IOException)
            {
                return true;  // No archive: nothing to wait for.
            }
            lock (BundledLock)
                if (_bundled is var (known, _, _) && known == key)
                    return true;
            new Thread(() =>
            {
                try
                {
                    BundledIndex(archive);
                }
                catch (Exception)
                {
                    // Left unbuilt: Has and Find read the archive themselves and report the problem.
                }
            }) { IsBackground = true }.Start();
            return false;
        }

        // Whether Find would find a portrait, without decoding it. With ``wait``
        // false, a game archive whose index isn't built yet (about half a
        // second) throws StillIndexingException at once and is indexed in the
        // background.
        public static bool Has(string personId, string graphicsDir, string gameDir, bool wait = true)
        {
            Portraits.ValidateId(personId);
            graphicsDir = Path.TrimEndingDirectorySeparator(graphicsDir);
            lock (IndexLock)
            {
                foreach (string config in Configs(Path.GetDirectoryName(graphicsDir), graphicsDir, Clock()))
                {
                    try
                    {
                        if (!File.Exists(config) || !ConfigFor(config).TryGetValue(personId, out var stems))
                            continue;
                        foreach (string stem in stems)
                            foreach (string suffix in ImageSuffixes)
                            {
                                var path = new FileInfo(Path.Combine(Path.GetDirectoryName(config), stem + suffix));
                                if (path.Exists && path.Length <= MaxImage)
                                    return true;
                            }
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                    }
                }
            }
            if (gameDir == null)
                return false;
            string archive = Archive(gameDir);
            if (!File.Exists(archive))
                return false;
            if (!wait && !BundledReady(archive))
                throw new StillIndexingException();
            var (entries, mapped) = BundledIndex(archive);
            return mapped.TryGetValue(personId, out string found) && ImageSuffixes.Any(suffix => entries.ContainsKey(found + suffix));
        }

        // FM24's installation folder, from the running game's fm.exe (or, on
        // macOS, the folder holding fm.app, from fm.app/Contents/MacOS/fm).
        public static string GameDirFromExecutable(string executable)
        {
            if (executable == null)
                return null;
            string folder = Path.GetDirectoryName(executable);
            if (Path.GetFileName(folder) == "MacOS")
                return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(folder)));
            return folder;
        }
    }
}
