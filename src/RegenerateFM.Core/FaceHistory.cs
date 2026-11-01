using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace RegenerateFM
{
    // A person's faces: the one config.xml maps and the earlier ones, kept in
    // .regeneratefm-history/<id>/ as .face files (PNGs that FM doesn't load),
    // with an index of whose each one is. A new face no longer loses the old
    // one, and Previous face brings it back.
    //
    // FM gives its generated people (newgens, and the youngsters it adds when
    // a career starts) the same IDs in every save, so one ID is a different
    // person in another save, and one faces folder serves them all. The name
    // shown when a face was made says whose it is: Claim moves another
    // person's face aside and brings back this person's own.
    public static class FaceHistory
    {
        // Earlier faces kept for each person (each name, for a shared ID).
        public const int KeptPerOwner = 5;

        private const string Index = "faces.json";

        private sealed class Face
        {
            public string Stem;
            public string Owner;
            public DateTime Made;
            // Taken off by the user (Remove face): Claim doesn't bring it back.
            public bool Removed;
        }

        internal static string Folder(string directory, string personId) =>
            Path.Combine(directory, ".regeneratefm-history", Portraits.ValidateId(personId));

        private static string Earlier(string directory, string personId, string stem) =>
            Path.Combine(Folder(directory, personId), stem + ".face");

        private static string Current(string directory, string stem) => Path.Combine(directory, stem + ".png");

        // The same person's name, also as a narrow list shortens it ("S. Winton"
        // for "Sean Winton").
        public static bool SameOwner(string a, string b)
        {
            if (a == null || b == null)
                return false;
            a = a.Trim();
            b = b.Trim();
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || Shortened(a, b) || Shortened(b, a);
        }

        private static bool Shortened(string listed, string full)
        {
            int dot = listed.IndexOf(". ", StringComparison.Ordinal);
            if (dot < 1 || dot > 3)
                return false;
            string rest = listed.Substring(dot + 2);
            return full.Length > rest.Length + 2 &&
                   full.StartsWith(listed.Substring(0, 1), StringComparison.OrdinalIgnoreCase) &&
                   full.EndsWith(" " + rest, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Face> Load(string directory, string personId)
        {
            string path = Path.Combine(Folder(directory, personId), Index);
            var faces = new List<Face>();
            if (!File.Exists(path))
                return faces;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("faces", out var list) || list.ValueKind != JsonValueKind.Array)
                    return faces;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("stem", out var stem) || stem.ValueKind != JsonValueKind.String ||
                        !Portraits.IsPortraitStem(stem.GetString(), personId))
                        continue;
                    var face = new Face { Stem = stem.GetString() };
                    if (item.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.String)
                        face.Owner = owner.GetString();
                    if (item.TryGetProperty("made", out var made) && made.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(made.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                        face.Made = when;
                    face.Removed = item.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.True;
                    faces.Add(face);
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            return faces;
        }

        // Writes the index, without faces whose file is gone, keeping the
        // newest KeptPerOwner earlier faces of each owner; older ones are deleted.
        private static void Store(string directory, string personId, List<Face> faces, string current)
        {
            var kept = new List<Face>();
            foreach (var group in faces.GroupBy(face => face.Owner?.Trim().ToLowerInvariant() ?? ""))
            {
                int earlier = 0;
                foreach (var face in group.OrderByDescending(face => face.Made))
                {
                    if (face.Stem == current)
                    {
                        if (File.Exists(Current(directory, face.Stem)))
                            kept.Add(face);
                        continue;
                    }
                    // In the history, or still in the faces folder until Retire moves it.
                    string file = Earlier(directory, personId, face.Stem);
                    if (!File.Exists(file))
                        file = Current(directory, face.Stem);
                    if (!File.Exists(file))
                        continue;
                    if (++earlier > KeptPerOwner)
                    {
                        TryDelete(file);
                        continue;
                    }
                    kept.Add(face);
                }
            }
            string folder = Folder(directory, personId);
            if (kept.Count == 0)
            {
                TryDelete(Path.Combine(folder, Index));
                try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
                catch (IOException) { }
                return;
            }
            AtomicFile.Write(Path.Combine(folder, Index), Json.Write(json =>
            {
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", 1);
                json.WriteString("personId", personId);
                json.WriteStartArray("faces");
                foreach (var face in kept.OrderBy(face => face.Made))
                {
                    json.WriteStartObject();
                    json.WriteString("stem", face.Stem);
                    if (face.Owner != null)
                        json.WriteString("owner", face.Owner);
                    json.WriteString("made", face.Made.ToString("o", CultureInfo.InvariantCulture));
                    if (face.Removed)
                        json.WriteBoolean("removed", true);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }, indented: true));
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // A face SavePortrait has just mapped: ``owner`` is the name shown with
        // it, ``made`` when it was first made (a face brought back keeps its own).
        internal static void Added(string directory, string personId, string stem, string owner, DateTime made)
        {
            var faces = Load(directory, personId);
            faces.RemoveAll(face => face.Stem == stem);
            faces.Add(new Face { Stem = stem, Owner = owner, Made = made });
            Store(directory, personId, faces, stem);
        }

        // Moves the person's faces in the faces folder, other than ``current``,
        // into the history; one that can't be moved now is moved next time.
        internal static void Retire(string directory, string personId, string current)
        {
            var faces = Load(directory, personId);
            foreach (var file in Directory.EnumerateFiles(directory, personId + "-*.png"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem == current || !Portraits.IsPortraitStem(stem, personId))
                    continue;
                try
                {
                    Directory.CreateDirectory(Folder(directory, personId));
                    File.Move(file, Earlier(directory, personId, stem), overwrite: true);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                // A face saved before the history (or by hand) has no record yet.
                if (!faces.Any(face => face.Stem == stem))
                    faces.Add(new Face { Stem = stem, Made = File.GetLastWriteTimeUtc(Earlier(directory, personId, stem)) });
            }
            Store(directory, personId, faces, current);
        }

        // The stem config.xml maps for this person, or null.
        private static string MappedStem(string directory, string personId)
        {
            var current = Portraits.CurrentFile(directory, personId);
            return current == null ? null : Path.GetFileNameWithoutExtension(current);
        }

        // Who the person's mapped face was made for, or null (none, or not known).
        public static string Owner(string directory, string personId)
        {
            lock (Portraits.SaveLock)
            {
                string stem = MappedStem(directory, personId);
                return stem == null ? null : Load(directory, personId).FirstOrDefault(face => face.Stem == stem)?.Owner;
            }
        }

        // A face Previous face may bring back for ``owner``: one made for them,
        // or one whose owner isn't known (saved before faces had one).
        private static bool Choosable(Face face, string owner) => face.Owner == null || SameOwner(face.Owner, owner);

        // How many earlier faces of this person Previous face can bring back.
        public static int EarlierCount(string directory, string personId, string owner)
        {
            lock (Portraits.SaveLock)
            {
                string stem = MappedStem(directory, personId);
                return Load(directory, personId).Count(face => face.Stem != stem && Choosable(face, owner) &&
                                                                File.Exists(Earlier(directory, personId, face.Stem)));
            }
        }

        // Brings back the person's face made before the one shown, or after
        // the oldest the newest again; their faces in turn. The shown face goes
        // into the history. Returns the face's new file, or null if there is
        // no other face of theirs.
        public static string Previous(string directory, string personId, string owner)
        {
            lock (Portraits.SaveLock)
            {
                string stem = MappedStem(directory, personId);
                var faces = Load(directory, personId);
                var shown = faces.FirstOrDefault(face => face.Stem == stem);
                var choices = faces.Where(face => face.Stem != stem && Choosable(face, owner) &&
                                                  File.Exists(Earlier(directory, personId, face.Stem)))
                                   .OrderByDescending(face => face.Made).ToList();
                if (choices.Count == 0)
                    return null;
                var choice = shown == null ? choices[0] : choices.FirstOrDefault(face => face.Made < shown.Made) ?? choices[0];
                return Restore(directory, personId, choice);
            }
        }

        // Saved again under a new name (FM26 doesn't load a file it has drawn
        // before), keeping when it was made; its history file then goes.
        private static string Restore(string directory, string personId, Face face)
        {
            string file = Earlier(directory, personId, face.Stem);
            var png = File.ReadAllBytes(file);
            string saved = Portraits.SavePortrait(directory, personId, png, owner: face.Owner, made: face.Made);
            TryDelete(file);
            Store(directory, personId, Load(directory, personId), Path.GetFileNameWithoutExtension(saved));
            return saved;
        }

        // Takes the person's face off, so FM shows its own (a photo, its 3D
        // face or a silhouette); it stays in the history for Previous face.
        // False if they have no RegenerateFM face.
        public static bool Remove(string directory, string personId)
        {
            lock (Portraits.SaveLock)
            {
                string stem = MappedStem(directory, personId);
                if (stem == null)
                    return false;
                Portraits.Unmap(directory, personId);
                var faces = Load(directory, personId);
                var shown = faces.FirstOrDefault(face => face.Stem == stem);
                if (shown == null)
                    faces.Add(shown = new Face { Stem = stem, Made = File.GetLastWriteTimeUtc(Current(directory, stem)) });
                // All of theirs: Claim then brings none back, until they choose one.
                foreach (var face in faces)
                    if (face == shown || shown.Owner != null && SameOwner(face.Owner, shown.Owner))
                        face.Removed = true;
                Store(directory, personId, faces, stem);
                Retire(directory, personId, null);
                return true;
            }
        }

        // ``shownName`` is on screen with this ID. If the mapped face was made
        // for someone else (in another save), it goes into the history; if
        // this person has a face of their own there, it comes back, unless
        // they took it off. True if the mapping changed, so the game reloads.
        public static bool Claim(string directory, string personId, string shownName)
        {
            if (string.IsNullOrWhiteSpace(shownName) || !Directory.Exists(Folder(directory, personId)))
                return false;
            lock (Portraits.SaveLock)
            {
                var faces = Load(directory, personId);
                if (faces.Count == 0)
                    return false;
                string stem = MappedStem(directory, personId);
                var shown = faces.FirstOrDefault(face => face.Stem == stem);
                if (stem != null && (shown?.Owner == null || SameOwner(shown.Owner, shownName)))
                    return false;
                bool changed = false;
                if (stem != null)
                {
                    Portraits.Unmap(directory, personId);
                    Retire(directory, personId, null);
                    changed = true;
                    faces = Load(directory, personId);
                }
                var own = faces.Where(face => SameOwner(face.Owner, shownName) && !face.Removed &&
                                              File.Exists(Earlier(directory, personId, face.Stem)))
                               .OrderByDescending(face => face.Made).FirstOrDefault();
                if (own != null)
                {
                    Restore(directory, personId, own);
                    changed = true;
                }
                return changed;
            }
        }
    }
}
