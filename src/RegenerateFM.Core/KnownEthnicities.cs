using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM
{
    // A person's ethnicity as FM drew them in a match ("match"), from FM's
    // appearance for a newgen ("newgen", which also marks them as one), or as
    // the user picked it when the face was generated ("chosen").
    public sealed class KnownEthnicity
    {
        public string Ethnicity;
        public string SkinTone;
        public string HairColour;
        public string Source;
    }

    // The ethnicities FM26 showed, kept by person ID in the faces folder.
    public static class KnownEthnicities
    {
        // Kept on this computer only, in the faces folder, by person ID.
        public const string FileName = ".regeneratefm-ethnicities.json";

        public static KnownEthnicity Load(string folder, string personId)
        {
            Portraits.ValidateId(personId);
            lock (Portraits.SaveLock)
                return ReadAll(folder).TryGetValue(personId, out var known) ? known : null;
        }

        // Fills in the profile's ethnicity from the saved ones; false when none is saved.
        public static bool Apply(ProfileSnapshot profile, string folder, string personId)
        {
            var known = Load(folder, personId);
            if (known == null)
                return false;
            profile.Ethnicity = known.Ethnicity;
            profile.SkinTone = known.SkinTone;
            profile.HairColour = known.HairColour;
            return true;
        }

        // Adds or replaces people's ethnicities; a match never replaces a
        // newgen's look, and neither replaces the user's choice. Returns how
        // many changed.
        public static int Save(string folder, IDictionary<string, KnownEthnicity> people)
        {
            lock (Portraits.SaveLock)
            {
                var all = ReadAll(folder);
                int changed = 0;
                foreach (var entry in people)
                {
                    Portraits.ValidateId(entry.Key);
                    var known = entry.Value;
                    if (!ValidEthnicity(known))
                        throw new ArgumentException("Ethnicity is invalid.");
                    if (all.TryGetValue(entry.Key, out var old) &&
                        (SourceRank(old.Source) > SourceRank(known.Source) ||
                         old.Ethnicity == known.Ethnicity && old.SkinTone == known.SkinTone &&
                         old.HairColour == known.HairColour && old.Source == known.Source))
                        continue;
                    all[entry.Key] = known;
                    changed++;
                }
                if (changed > 0)
                {
                    var ordered = new List<string>(all.Keys);
                    ordered.Sort(StringComparer.Ordinal);
                    Portraits.AtomicWrite(Path.Combine(folder, FileName), Encoding.UTF8.GetBytes(Json.Text(json =>
                    {
                        json.WriteStartObject();
                        json.WriteNumber("schemaVersion", 1);
                        json.WriteStartObject("people");
                        foreach (string id in ordered)
                        {
                            var known = all[id];
                            json.WriteStartObject(id);
                            foreach (var (name, value) in new[] { ("ethnicity", known.Ethnicity), ("skinTone", known.SkinTone),
                                                                  ("hairColour", known.HairColour), ("source", known.Source) })
                                if (value != null)
                                    json.WriteString(name, value);
                            json.WriteEndObject();
                        }
                        json.WriteEndObject();
                        json.WriteEndObject();
                    }, indented: true) + "\n"));
                }
                return changed;
            }
        }

        private static int SourceRank(string source) => source == "chosen" ? 2 : source == "newgen" ? 1 : 0;

        private static bool ValidEthnicity(KnownEthnicity known) =>
            known != null && Prompts.Ethnicities.Contains(known.Ethnicity) &&
            (known.SkinTone == null || Prompts.SkinTones.Contains(known.SkinTone)) &&
            (known.HairColour == null || Prompts.HairColours.Contains(known.HairColour)) &&
            (known.Source == "match" || known.Source == "newgen" || known.Source == "chosen");

        // The saved ethnicities; an unreadable file, or entry, counts as none.
        private static Dictionary<string, KnownEthnicity> ReadAll(string folder)
        {
            var all = new Dictionary<string, KnownEthnicity>();
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
                return all;
            try
            {
                using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                        !root.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Object)
                        return all;
                    foreach (var person in people.EnumerateObject())
                    {
                        if (person.Value.ValueKind != JsonValueKind.Object || !Portraits.IdPattern.IsMatch(person.Name))
                            continue;
                        string field(string name) => person.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                            ? value.GetString() : null;
                        var known = new KnownEthnicity
                        {
                            Ethnicity = field("ethnicity"), SkinTone = field("skinTone"),
                            HairColour = field("hairColour"), Source = field("source"),
                        };
                        if (ValidEthnicity(known))
                            all[person.Name] = known;
                    }
                }
            }
            catch (JsonException)
            {
            }
            return all;
        }
    }
}
