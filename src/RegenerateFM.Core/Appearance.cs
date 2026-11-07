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
    // Stable appearance traits for a generated face, chosen once per person
    // and saved beside their portraits. Most people get everyday looks; a few
    // get a standout hairstyle, beard or mark, and FM's ethnicity and
    // attributes shape the choice.
    public static class Appearance
    {
        public const string NoFacialHair = "no facial hair";

        // ---- Appearance traits ---------------------------------------------

        private static readonly string[] TraitKeys = { "face", "features", "eyes", "hair", "facialHair", "expression", "detail" };

        internal static string AppearancePrompt(string name, int age, string personId, string directory, string gender,
                                               string family, ProfileSnapshot profile) =>
            AppearanceInstruction(PromptTraits(name, age, personId, directory, gender, family, profile), gender);

        // The person's traits as a prompt gives them: saved ones, with FM's hair
        // colour and their build as they are now.
        internal static Dictionary<string, string> PromptTraits(string name, int age, string personId, string directory, string gender,
                                                                string family, ProfileSnapshot profile) =>
            WithBuild(WithHairColour(PersonTraits(name, age, personId, directory, gender, family, profile), profile,
                                     ShadeSeed(name, personId, family)), profile);

        // A strong or slight player shows it in the neck and build, read from
        // their Strength each time, as it changes over a career.
        private static Dictionary<string, string> WithBuild(Dictionary<string, string> traits, ProfileSnapshot profile)
        {
            double? strength = Attribute(profile, "Strength");
            if (traits == null || !traits.TryGetValue("face", out string face) || face == null || strength == null ||
                strength > 6 && strength < 16)
                return traits;
            return new Dictionary<string, string>(traits)
            {
                ["face"] = face + (strength >= 16 ? ", and a thick, muscular neck" : ", and a slender neck and slight build")
            };
        }

        // One of the person's attributes (1-20, or a scouted range's middle),
        // or null when FM hasn't shown it.
        public static double? Attribute(ProfileSnapshot profile, string name)
        {
            if (profile == null)
                return null;
            foreach (var section in new[] { profile.Physical, profile.Mental, profile.Goalkeeping, profile.Technical })
                foreach (var entry in section ?? new Dictionary<string, object>())
                {
                    if (!string.Equals(entry.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (entry.Value is int score && score >= 1 && score <= 20)
                        return score;
                    var range = entry.Value is string text ? AttributeRange.Match(text.Trim()) : Match.Empty;
                    if (range.Success)
                    {
                        int low = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                        int high = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (low >= 1 && high <= 20 && low <= high)
                            return (low + high) / 2.0;
                    }
                    return null;
                }
            return null;
        }

        private static readonly Regex AttributeRange = new Regex(@"^([0-9]{1,2})\s*[-–]\s*([0-9]{1,2})$", RegexOptions.Compiled);

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

        private static string Pick(string seed, string category, string[] choices) =>
            choices[(int)(Draw(seed, category) % (ulong)choices.Length)];

        // Pick, where a choice with weight 3 comes up three times as often as one with 1.
        private static string Pick(string seed, string category, IReadOnlyList<(string Text, int Weight)> choices)
        {
            ulong at = Draw(seed, category) % (ulong)choices.Sum(choice => choice.Weight);
            foreach (var (text, weight) in choices)
            {
                if (at < (ulong)weight)
                    return text;
                at -= (ulong)weight;
            }
            throw new InvalidOperationException("No choice was drawn.");
        }

        private static ulong Draw(string seed, string category)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes("regeneratefm-appearance-v1\0" + seed + "\0" + category));
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | digest[i];
            return value;
        }

        // The person's appearance traits: saved ones, or new ones chosen and saved.
        internal static Dictionary<string, string> PersonTraits(string name, int age, string personId, string directory,
                                                               string gender, string family = null, ProfileSnapshot profile = null)
        {
            if (personId == null)
            {
                if (directory != null)
                    throw new ArgumentException("A person ID is required to save an appearance profile.");
                return AppearanceTraits("name:" + name.ToLowerInvariant(), age, gender, family, profile);
            }
            string ethnicity = profile?.Ethnicity;
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
                    // Traits chosen before the person's gender or ethnicity was known
                    // (or for another one), or without one of the kinds of trait, are
                    // chosen again.
                    string savedGender = root.TryGetProperty("gender", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                    string savedEthnicity = root.TryGetProperty("ethnicity", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if ((gender == null || savedGender == gender) && (ethnicity == null || savedEthnicity == ethnicity) &&
                        TraitKeys.All(traits.ContainsKey))
                        return traits;
                }
            }
            var chosen = AppearanceTraits(personId, age, gender, family, profile);
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
                    if (ethnicity != null)
                        json.WriteString("ethnicity", ethnicity);
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
        private static readonly string[] FamilyTraits = { "faceShape", "features", "hairColour", "eyeColour" };

        // How often a choice comes up: most people get everyday looks, a few a
        // standout one.
        private const int Common = 5, Uncommon = 3, Standout = 1;

        // Hairstyles that suit a person's ethnicity, as FM gives it: tightly
        // curled or coiled hair, straight hair, or the rest (and anyone whose
        // ethnicity isn't known).
        private enum HairType { General, Coiled, Straight }

        private static HairType HairTypeOf(string ethnicity)
        {
            switch (ethnicity)
            {
                case "AfricanCaribbean": case "MixedRaceWhiteBlack": case "PacificIslander": case "NativeAustralian":
                    return HairType.Coiled;
                case "EastAsian": case "SouthEastAsian": case "NativeAmerican":
                    return HairType.Straight;
                default:
                    return HairType.General;
            }
        }

        private static List<(string, int)> MaleHairstyles(HairType type, int standout)
        {
            switch (type)
            {
                case HairType.Coiled:
                    return new List<(string, int)>
                    {
                        ("a close buzz cut", Common), ("a skin fade with short, coiled hair on top", Common),
                        ("short, coiled hair with a simple rounded outline", Common), ("a low fade with a sharp line-up", Common),
                        ("short, tightly curled hair", Common), ("short, loose curls", Common),
                        ("a shaved head", Uncommon), ("short twists", Uncommon), ("neat cornrows", Uncommon),
                        ("a short afro", Uncommon), ("short dreadlocks", Uncommon),
                        ("shoulder-length dreadlocks tied back", standout), ("a high-top fade", standout),
                        ("a full, rounded afro", standout), ("longer twists tied up on top", standout),
                        ("long braids pulled back into a bun", standout),
                    };
                case HairType.Straight:
                    return new List<(string, int)>
                    {
                        ("short, straight hair brushed forward", Common), ("a short textured crop with a light fringe", Common),
                        ("a plain short side-parted haircut", Common), ("a close buzz cut", Common),
                        ("short sides with longer, straight hair on top", Common), ("short, slightly spiky hair", Common),
                        ("medium-length straight hair with a centre parting", Uncommon), ("a shaved head", Uncommon),
                        ("short hair neatly slicked back", Uncommon), ("a messy, textured crop", Uncommon),
                        ("long, straight hair tied back", standout), ("a loose perm with soft waves", standout),
                        ("a tall, swept-up quiff", standout), ("shoulder-length straight hair", standout),
                        ("a modern mullet, short at the sides and longer at the back", standout),
                    };
                default:
                    return new List<(string, int)>
                    {
                        ("a close buzz cut", Common), ("short, straight hair brushed forward", Common),
                        ("a plain short side-parted haircut", Common), ("short hair with an uneven fringe", Common),
                        ("short, close-cropped hair with a naturally uneven hairline", Common),
                        ("a short back and sides with a little more length on top", Common),
                        ("a skin fade with short, textured hair on top", Common),
                        ("short, tightly curled hair", Uncommon), ("medium-length, loosely wavy hair pushed back", Uncommon),
                        ("longer curls loosely swept away from the forehead", Uncommon), ("a shaved head", Uncommon),
                        ("short hair neatly slicked back", Uncommon), ("a messy, textured crop", Uncommon),
                        ("shoulder-length hair tucked behind the ears", standout), ("long hair tied back in a bun", standout),
                        ("a modern mullet, short at the sides and longer at the back", standout),
                        ("a tall, swept-up quiff", standout), ("a curtained haircut with a centre parting", standout),
                        ("thick, jaw-length curly hair", standout),
                    };
            }
        }

        private static List<(string, int)> FemaleHairstyles(HairType type, int standout)
        {
            switch (type)
            {
                case HairType.Coiled:
                    return new List<(string, int)>
                    {
                        ("tightly coiled hair pulled back from the face", Common), ("braids tied back", Common),
                        ("short, cropped natural hair", Common), ("box braids in a high ponytail", Common),
                        ("cornrows", Uncommon), ("a curly afro puff", Uncommon), ("hair pulled back into a low bun", Uncommon),
                        ("long braids tied up on top", standout), ("a full, rounded afro", standout),
                    };
                case HairType.Straight:
                    return new List<(string, int)>
                    {
                        ("hair tied back in a neat ponytail", Common), ("hair pulled back into a low bun", Common),
                        ("a chin-length bob", Common), ("straight hair with a full fringe, tied back", Common),
                        ("a short pixie cut", Uncommon), ("long, straight hair tied back", Uncommon),
                        ("shoulder-length straight hair tucked behind the ears", Uncommon),
                        ("a short undercut", standout), ("a sleek high ponytail", standout),
                    };
                default:
                    return new List<(string, int)>
                    {
                        ("hair tied back in a neat ponytail", Common), ("hair pulled back into a low bun", Common),
                        ("a high ponytail", Common), ("shoulder-length straight hair tucked behind the ears", Common),
                        ("a chin-length bob", Common),
                        ("a short pixie cut", Uncommon), ("curly hair tied up loosely", Uncommon),
                        ("long hair in a single plait", Uncommon), ("wavy hair tied back, with loose strands at the front", Uncommon),
                        ("a short undercut", standout), ("a sleek, tight bun with a centre parting", standout),
                    };
            }
        }

        // Eye colours by ethnicity, chosen for a family; none when the ethnicity
        // isn't known, so that it doesn't sway the model's guess.
        private static List<(string, int)> EyeColours(string ethnicity)
        {
            switch (ethnicity)
            {
                case null:
                    return null;
                case "NorthernEuropean":
                    return new List<(string, int)> { ("blue", 4), ("grey-blue", 2), ("green", 2), ("hazel", 2), ("brown", 3) };
                case "MediterraneanHispanic": case "NorthAfricanMiddleEastern":
                    return new List<(string, int)> { ("dark brown", 5), ("brown", 4), ("hazel", 2), ("green", 1) };
                case "MixedRaceWhiteBlack":
                    return new List<(string, int)> { ("brown", 4), ("dark brown", 3), ("hazel", 2), ("green", 1) };
                default:
                    return new List<(string, int)> { ("dark brown", 3), ("brown", 2) };
            }
        }

        // gender is "female", "male" or null (not known). Women get their own
        // hairstyles, and only men get facial hair. family, a seed shared by
        // relatives (FamilySeed), chooses FamilyTraits. From profile, FM's
        // ethnicity chooses the hairstyles and eye colours that suit it, Flair or
        // Eccentricity makes a standout hairstyle likelier, and Aggression,
        // Determination and Leadership shape the expression.
        public static Dictionary<string, string> AppearanceTraits(string seed, int age, string gender = null, string family = null,
                                                                  ProfileSnapshot profile = null)
        {
            Func<string, IReadOnlyList<(string, int)>, string> pick = (category, choices) =>
                Pick(family != null && FamilyTraits.Contains(category) ? family : seed, category, choices);
            Func<string[], IReadOnlyList<(string, int)>> even = choices => choices.Select(choice => (choice, 1)).ToList();
            string ethnicity = profile?.Ethnicity;
            double Score(string name) => Attribute(profile, name) ?? 10;

            string shape = pick("faceShape", even(new[] { "a relatively long face", "a broad face", "an oval face",
                                                          "a square face", "a narrow face", "a rounder face" }));
            string jaw = pick("jaw", even(new[] { "a softly angular jaw", "a gently rounded jaw", "a modest chin",
                                                  "a broad chin", "a slightly tapered jaw", "a strong, square jaw",
                                                  "a small, receding chin" }));
            string cheeks = pick("cheeks", even(new[] { "fuller cheeks", "lean cheeks", "subtly uneven cheekbones",
                                                        "slightly hollow cheeks", "high, prominent cheekbones" }));
            string face = shape + ", with " + jaw + " and " + cheeks;
            string features = pick("features", even(new[]
            {
                "a broad, rounded nose and straight, moderately thick eyebrows",
                "a narrow nose with a softly rounded tip and low, straight eyebrows",
                "a slightly prominent nose bridge and fine, gently arched eyebrows",
                "a short, broad nose and thick eyebrows with a small natural unevenness",
                "slightly prominent ears and an ordinary straight nose",
                "a rounded nose tip and eyebrows that sit at subtly different heights",
                "a long, straight nose and widely spaced, moderately thick eyebrows",
                "a gently convex nose bridge and short, straight eyebrows",
                "a strong, prominent nose and heavy eyebrows",
                "a small, slightly upturned nose and light, thin eyebrows",
                "a heavy brow ridge with thick, straight eyebrows",
                "a slightly hooked nose and thick, slightly unruly eyebrows",
            }));
            string eyeShape = pick("eyeShape", even(new[] { "deep-set", "hooded", "large, round", "narrow, almond-shaped",
                                                            "close-set", "heavy-lidded", "slightly downturned", "wide-set",
                                                            "small, bright" }));
            var eyeColours = EyeColours(ethnicity);
            string eyes = eyeShape + (eyeColours != null ? " " + pick("eyeColour", eyeColours) : "") + " eyes";

            // A flamboyant or eccentric player is three times as likely to have a standout hairstyle.
            int standout = Score("Flair") >= 15 || Score("Eccentricity") >= 15 ? 3 * Standout : Standout;
            var styles = gender == "female" ? FemaleHairstyles(HairTypeOf(ethnicity), standout)
                                            : MaleHairstyles(HairTypeOf(ethnicity), standout);
            if (gender != "female" && age >= 25)
                styles.Add(("very short hair with a mildly receding hairline", age >= 32 ? Common : Uncommon));
            string style = pick(gender == "female" ? "hairStyleFemale" : "hairStyle", styles);
            var colours = new List<string> { "black", "dark brown", "medium brown", "light brown", "dark blond", "auburn" };
            if (age >= 45)
                colours.AddRange(new[] { "salt-and-pepper", "greying brown", "silver-grey" });
            string hair = style == "a shaved head" ? style : style + HairColourLabel + pick("hairColour", even(colours.ToArray()));

            // From 23, a full beard and a thick moustache each come up about one time in 33.
            var facialHair = new List<(string, int)> { ("clean-shaven", 18) };
            if (age >= 18)
                facialHair.AddRange(new[] { ("very light, patchy stubble", 9), ("a faint moustache", 3) });
            if (age >= 23)
                facialHair.AddRange(new[]
                {
                    ("short, uneven stubble", 12), ("heavy stubble", 9), ("a short, neatly trimmed beard", 6),
                    ("a modest moustache", 3), ("a neat goatee", 3), ("a full, thick beard", 2), ("a thick moustache", 2),
                });

            // An aggressive or very determined player looks more intense and never
            // grins, a mild one smiles more, and a leader looks self-assured.
            bool fiery = Score("Aggression") >= 15, mild = Score("Aggression") <= 7;
            string expression = pick("expression", new List<(string, int)>
            {
                ("a relaxed neutral expression with closed lips", 4),
                ("a calm, unsmiling expression with relaxed facial muscles", 2),
                ("a relaxed expression with a barely perceptible, slightly uneven smile", 2),
                ("a small, natural closed-mouth smile", mild ? 3 : 2),
                ("a restrained, natural smile showing a little teeth", mild ? 2 : 1),
                ("a broad, easy smile showing teeth", mild ? 2 : fiery ? 0 : 1),
                ("a serious, intense stare", 1 + (fiery ? 2 : 0) + (Score("Determination") >= 16 ? 1 : 0)),
                ("a guarded look with a slightly furrowed brow", fiery ? 2 : 1),
                ("a composed, self-assured look", Score("Leadership") >= 15 ? 3 : 1),
            });

            var details = new List<(string, int)>
            {
                ("ordinary skin with a few small blemishes", 12), ("a small mole on one cheek", 1),
                ("a small scar through one eyebrow", 1), ("a small scar on the chin", 1),
                ("a slightly crooked nose that looks as if it was once broken", 1),
                ("faint acne scarring on the cheeks", 1), ("faint dark circles under the eyes", 1),
            };
            if (ethnicity == "NorthernEuropean")
                details.AddRange(new[] { ("a light scattering of freckles across the nose and cheeks", 2), ("ruddy, slightly flushed cheeks", 2) });
            if (age <= 20)
                details.Add(("a few spots on the forehead and chin", 2));
            if (age >= 30)
                details.Add(("weathered skin with lines around the eyes", 3));
            if (age >= 35)
                details.Add(("deep lines across the forehead", 2));
            string detail = pick("detail", details);

            var values = new[] { face, features, eyes, hair, gender == "male" ? pick("facialHair", facialHair) : NoFacialHair, expression, detail };
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
                   "Show these features clearly, as a real person of this age has them, rather than smoothing them towards an average face. " +
                   "Preserve subtle facial asymmetry and ordinary skin texture. ";
        }
    }
}
