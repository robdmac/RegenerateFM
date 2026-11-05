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
    // Stable, understated appearance traits for a generated face, chosen once
    // per person and saved beside their portraits.
    public static class Appearance
    {
        public const string NoFacialHair = "no facial hair";

        // ---- Appearance traits ---------------------------------------------

        private static readonly string[] TraitKeys = { "face", "features", "hair", "facialHair", "expression" };

        internal static string AppearancePrompt(string name, int age, string personId, string directory, string gender,
                                               string family, ProfileSnapshot profile) =>
            AppearanceInstruction(WithHairColour(PersonTraits(name, age, personId, directory, gender, family), profile,
                                                 ShadeSeed(name, personId, family)), gender);

        // The traits' random hair colour suits only a white Northern European:
        // anyone else gets FM's own hair colour, or one natural for their
        // ethnicity, so that a guessed ethnicity isn't swayed by it.
        private static Dictionary<string, string> WithHairColour(Dictionary<string, string> traits, ProfileSnapshot profile, string seed)
        {
            if (traits == null || !traits.TryGetValue("hair", out string hair) || hair == null ||
                profile.HairColour == null && profile.Ethnicity == "NorthernEuropean")
                return traits;
            int at = hair.LastIndexOf(HairColourLabel, StringComparison.Ordinal);
            if (at < 0)
                return traits;
            return new Dictionary<string, string>(traits)
            {
                ["hair"] = hair.Substring(0, at) + HairColourLabel +
                           (profile.HairColour != null ? HairShade(profile.HairColour, profile.Ethnicity, seed) : "natural for their ethnicity")
            };
        }

        private const string HairColourLabel = "; hair colour: ";

        // FM draws hair in a few colours, and a match gives about two thirds of
        // its players the one "dark brown" (from near-black to mousy). For a
        // white Northern European, FM's colour becomes a shade within it, the
        // same for a person (and their family) each time.
        private static readonly Dictionary<string, string[]> HairShades = new Dictionary<string, string[]>
        {
            ["dark brown"] = new[] { "very dark brown", "dark brown", "medium brown", "chestnut brown", "mousy light brown" },
            ["light brown"] = new[] { "light brown", "dark blond", "sandy blond" },
            ["blond"] = new[] { "blond", "dark blond", "pale blond" },
            ["ginger"] = new[] { "ginger", "copper red", "auburn", "strawberry blond" },
        };

        public static string HairShade(string hairColour, string ethnicity, string seed) =>
            ethnicity == "NorthernEuropean" && HairShades.TryGetValue(hairColour, out var shades)
                ? Pick(seed, "hairShade", shades) : hairColour;

        // The seed HairShade uses: a family's, so relatives share a shade, else the person's.
        public static string ShadeSeed(string name, string personId, string family) =>
            family ?? personId ?? "name:" + name.ToLowerInvariant();

        private static string Pick(string seed, string category, string[] choices)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes("regeneratefm-appearance-v1\0" + seed + "\0" + category));
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | digest[i];
            return choices[(int)(value % (ulong)choices.Length)];
        }

        // The person's appearance traits: saved ones, or new ones chosen and saved.
        internal static Dictionary<string, string> PersonTraits(string name, int age, string personId, string directory,
                                                               string gender, string family = null)
        {
            if (personId == null)
            {
                if (directory != null)
                    throw new ArgumentException("A person ID is required to save an appearance profile.");
                return AppearanceTraits("name:" + name.ToLowerInvariant(), age, gender, family);
            }
            Portraits.ValidateId(personId);
            string path = directory != null ? Path.Combine(directory, ".regeneratefm-appearances", personId + ".json") : null;
            if (path != null && File.Exists(path))
            {
                JsonDocument saved;
                try
                {
                    saved = JsonDocument.Parse(File.ReadAllBytes(path));
                }
                catch (JsonException)
                {
                    throw new ArgumentException("Saved appearance profile is invalid: " + Path.GetFileName(path));
                }
                using (var json = saved)
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1 ||
                        !root.TryGetProperty("personId", out var savedId) || savedId.ValueKind != JsonValueKind.String || savedId.GetString() != personId ||
                        !root.TryGetProperty("traits", out var savedTraits) || savedTraits.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Saved appearance profile is invalid: " + Path.GetFileName(path));
                    var traits = new Dictionary<string, string>();
                    foreach (var property in savedTraits.EnumerateObject())
                        traits[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    // Traits chosen before the person's gender was known (or for another
                    // one) are chosen again for it.
                    string savedGender = root.TryGetProperty("gender", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                    if (gender == null || savedGender == gender)
                        return traits;
                }
            }
            var chosen = AppearanceTraits(personId, age, gender, family);
            if (path != null)
            {
                AtomicFile.Write(path, Encoding.UTF8.GetBytes(Json.Text(json =>
                {
                    json.WriteStartObject();
                    json.WriteNumber("schemaVersion", 1);
                    json.WriteString("personId", personId);
                    json.WriteStartObject("traits");
                    foreach (var trait in chosen)
                        json.WriteString(trait.Key, trait.Value);
                    json.WriteEndObject();
                    if (gender != null)
                        json.WriteString("gender", gender);
                    json.WriteEndObject();
                }, indented: true) + "\n"));
            }
            return chosen;
        }

        // A coin flip that is always the same for ``seed`` and ``category``.
        public static bool Chance(string seed, string category)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes("regeneratefm-chance-v1\0" + seed + "\0" + category));
            return (digest[0] & 1) == 0;
        }

        // Relatives share these, chosen from the family's seed: a slight family
        // resemblance, while jaw, cheeks, hairstyle and expression stay their own.
        private static readonly string[] FamilyTraits = { "faceShape", "features", "hairColour" };

        // gender is "female", "male" or null (not known). Women get their own
        // hairstyles, and only men get facial hair. family, a seed shared by
        // relatives (FamilySeed), chooses FamilyTraits.
        public static Dictionary<string, string> AppearanceTraits(string seed, int age, string gender = null, string family = null)
        {
            Func<string, string[], string> pick = (category, choices) =>
                Pick(family != null && FamilyTraits.Contains(category) ? family : seed, category, choices);
            string shape = pick("faceShape", new[] { "a relatively long face", "a broad face", "an oval face",
                                                     "a square face", "a narrow face", "a rounder face" });
            string jaw = pick("jaw", new[] { "a softly angular jaw", "a gently rounded jaw", "a modest chin",
                                             "a broad chin", "a slightly tapered jaw" });
            string cheeks = pick("cheeks", new[] { "fuller cheeks", "lean cheeks", "subtly uneven cheekbones",
                                                   "slightly hollow cheeks" });
            string face = shape + ", with " + jaw + " and " + cheeks;
            string features = pick("features", new[]
            {
                "a broad, rounded nose and straight, moderately thick eyebrows",
                "a narrow nose with a softly rounded tip and low, straight eyebrows",
                "a slightly prominent nose bridge and fine, gently arched eyebrows",
                "a short, broad nose and thick eyebrows with a small natural unevenness",
                "slightly prominent ears and an ordinary straight nose",
                "a rounded nose tip and eyebrows that sit at subtly different heights",
                "a long, straight nose and widely spaced, moderately thick eyebrows",
                "a gently convex nose bridge and short, straight eyebrows",
            });
            string style;
            if (gender == "female")
                style = pick("hairStyleFemale", new[]
                {
                    "hair tied back in a neat ponytail", "hair pulled back into a low bun", "a high ponytail",
                    "shoulder-length straight hair tucked behind the ears", "a short pixie cut",
                    "tightly coiled hair pulled back from the face", "braids tied back", "a chin-length bob",
                    "curly hair tied up loosely", "short, cropped natural hair",
                });
            else
            {
                var styles = new List<string>
                {
                    "a close buzz cut", "short, tightly curled hair", "short, straight hair brushed forward",
                    "short, coiled hair with a simple rounded outline", "a plain short side-parted haircut",
                    "medium-length, loosely wavy hair pushed back", "a shaved head",
                    "short hair with an uneven fringe", "longer curls loosely swept away from the forehead",
                    "short, close-cropped hair with a naturally uneven hairline",
                };
                if (age >= 25)
                    styles.Add("very short hair with a mildly receding hairline");
                style = pick("hairStyle", styles.ToArray());
            }
            var colours = new List<string> { "black", "dark brown", "medium brown", "light brown", "dark blond", "auburn" };
            if (age >= 45)
                colours.AddRange(new[] { "salt-and-pepper", "greying brown", "silver-grey" });
            string hair = style == "a shaved head" ? style : style + HairColourLabel + pick("hairColour", colours.ToArray());
            var facialHair = new List<string> { "clean-shaven" };
            if (age >= 18)
                facialHair.AddRange(new[] { "clean-shaven", "very light, patchy stubble", "a faint moustache" });
            if (age >= 23)
                facialHair.AddRange(new[] { "short, uneven stubble", "a short, neatly trimmed beard", "a modest moustache" });
            string expression = pick("expression", new[]
            {
                "a relaxed neutral expression with closed lips",
                "a relaxed neutral expression with closed lips",
                "a calm, unsmiling expression with relaxed facial muscles",
                "a relaxed expression with a barely perceptible, slightly uneven smile",
                "a small, natural closed-mouth smile",
                "a restrained, natural smile showing a little teeth",
            });
            var values = new[] { face, features, hair, gender == "male" ? pick("facialHair", facialHair.ToArray()) : NoFacialHair, expression };
            var traits = new Dictionary<string, string>();
            for (int i = 0; i < TraitKeys.Length; i++)
                traits[TraitKeys[i]] = values[i];
            return traits;
        }

        internal static string AppearanceInstruction(Dictionary<string, string> traits, string gender)
        {
            // Saved traits may hold facial hair chosen for a man; only a man keeps it.
            if (traits != null && gender != "male" && traits.ContainsKey("facialHair"))
                traits = new Dictionary<string, string>(traits) { ["facialHair"] = NoFacialHair };
            if (traits == null || traits.Count != TraitKeys.Length || TraitKeys.Any(key => !traits.ContainsKey(key)) ||
                traits.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200))
                throw new ArgumentException("Saved appearance traits are invalid.");
            return "Individual appearance: " + string.Join("; ", TraitKeys.Select(key => traits[key])) + ". " +
                   "Keep these features understated, anatomically natural and consistent with the person's age. " +
                   "Preserve subtle facial asymmetry and ordinary skin texture. ";
        }
    }
}
