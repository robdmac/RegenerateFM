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
    // The image prompts. tests/RegenerateFM.Tests checks them against
    // Fixtures/expected-prompts.json.
    public static class Prompts
    {
        private static readonly string[] PhysicalStats =
        {
            "Acceleration", "Agility", "Balance", "Jumping Reach",
            "Natural Fitness", "Pace", "Stamina", "Strength"
        };
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex Paragraph = new Regex(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
        private static readonly Regex Range = new Regex(@"^(?:[1-9]|1[0-9]|20)\s*[-–]\s*(?:[1-9]|1[0-9]|20)$", RegexOptions.Compiled);
        private static readonly Regex RangeSplit = new Regex(@"\s*[-–]\s*", RegexOptions.Compiled);
        private static readonly Regex IdPattern = new Regex(@"^[0-9]{1,20}$", RegexOptions.Compiled);

        private const string PortraitFraming =
            "Match the tight headshot framing of existing Football Manager squad photos. " +
            "The head should fill the square image: crown-to-chin height about 80–85% of the image, " +
            "with the full hairstyle and chin visible and only a small gap above the hair. " +
            "Crop at the base of the neck, showing just the collar and a narrow sliver of the shirt; " +
            "do not include the upper chest or broad shoulders. " +
            "Keep the face centered, looking at the camera, with a natural, slight variation in head angle. ";
        private const string PortraitPhotography =
            "Use everyday club media-day photography with restrained photographic processing, " +
            "gently directional lighting and natural facial " +
            "shadows and colour variation. Preserve skin texture, pores, small imperfections and " +
            "age-appropriate lines, with natural facial asymmetry and no beauty retouching. ";
        private const string PortraitBackground =
            "Use a genuinely transparent background with clean hair edges, " +
            "without a backdrop, halo or vignette. ";
        // The shared look of a generated portrait (regeneratefm.portrait_style).
        private static string PortraitStyle(string gender) =>
            PortraitFraming +
            "Follow the individual's supplied expression; otherwise use a relaxed neutral expression. " +
            PortraitPhotography +
            "Give this individual distinctive facial proportions, nose, jaw, eyes, hairline and hairstyle " +
            "consistent with the supplied appearance details and age. Allow varied hair lengths and " +
            // Only a man may have facial hair; for anyone else the prompt rules it out.
            "textures" + (gender == "male" ? " and age-appropriate facial hair" : ", with no facial hair") +
            "; avoid defaulting to a model-like face or the same tousled brown haircut. " + PortraitBackground;

        private static string Gender(ProfileSnapshot profile)
        {
            if (string.IsNullOrEmpty(profile.Gender))
                return null;
            if (profile.Gender != "female" && profile.Gender != "male")
                throw new ArgumentException("Gender must be female or male.");
            return profile.Gender;
        }

        // The game's family groups, as FM26 names them on the Favoured Personnel
        // list ("Brothers", "Father", "Half-Sisters"): only these count as relatives.
        public static readonly string[] FamilyRelations =
        {
            "twin brother", "twin sister", "twin", "father", "mother", "brother", "sister", "sibling",
            "son", "daughter", "parent", "child", "half-brother", "half-sister", "grandfather",
            "grandmother", "grandson", "granddaughter", "grandparent", "grandchild", "uncle", "aunt",
            "nephew", "niece", "cousin", "relative",
        };
        private static readonly Regex FamilyGroup = new Regex(
            "^(" + string.Join("|", FamilyRelations.Select(Regex.Escape)) + ")(?:s|ren)?$", RegexOptions.Compiled);

        // The profile's relatives as (relation, person ID), closest first: a
        // "Brothers" group gives ("brother", id). Team-mates and coaches are left out.
        public static List<(string Relation, string PersonId)> FamilyRelatives(ProfileSnapshot profile)
        {
            var found = new List<(string Relation, string PersonId)>();
            foreach (var (relation, personId) in profile.Relatives ?? new List<(string Relation, string PersonId)>())
            {
                var match = FamilyGroup.Match(string.Join(" ", (relation ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant());
                if (match.Success)
                    found.Add((match.Groups[1].Value, Portraits.ValidateId(personId ?? "")));
            }
            return found.OrderBy(item => Array.IndexOf(FamilyRelations, item.Relation)).ToList();
        }

        // The seed relatives share (FamilyTraits): the family's lowest person ID.
        public static string FamilySeed(string personId, List<(string Relation, string PersonId)> relatives)
        {
            if (relatives.Count == 0)
                return null;
            var ids = relatives.Select(item => System.Numerics.BigInteger.Parse(item.PersonId)).ToList();
            if (personId != null)
                ids.Add(System.Numerics.BigInteger.Parse(personId));
            return "family:" + ids.Min();
        }

        private static string GenderText(string name, string gender) =>
            gender == "female" ? " " + name + " is a woman." : gender == "male" ? " " + name + " is a man." : "";

        // GenderText for Update face and a newgen's photograph, where an under-18
        // described as a man or woman is drawn too old.
        private static string GenderText(string name, string gender, int age) =>
            age >= 18 ? GenderText(name, gender)
            : gender == "female" ? " " + name + " is a teenage girl." : gender == "male" ? " " + name + " is a teenage boy." : "";

        // FM's ethnicities (its SI.Match.Ethnicity, in the game's order) and how
        // the prompt describes each.
        public static readonly string[] Ethnicities =
        {
            "NorthernEuropean", "MediterraneanHispanic", "NorthAfricanMiddleEastern", "AfricanCaribbean",
            "Asian", "SouthEastAsian", "PacificIslander", "NativeAmerican", "NativeAustralian",
            "MixedRaceWhiteBlack", "EastAsian",
        };
        public static readonly string[] EthnicityLabels =
        {
            "white Northern European", "Mediterranean or Hispanic", "North African or Middle Eastern",
            "Black African or Caribbean", "South Asian", "South-East Asian", "Pacific Islander",
            "Indigenous American", "Indigenous Australian", "mixed race (Black and white)", "East Asian",
        };
        // FM's skin tones (SI.Match.SkinTone, in order; a newgen's 1-20 counts
        // through them) and the words for each.
        public static readonly string[] SkinTones =
        {
            "White1", "White2", "White3", "White4", "White5",
            "Latin1", "Latin2", "Latin3", "Latin4", "Latin5", "Latin6", "Latin7",
            "African1", "African2", "African3", "African4", "African5", "African6", "African7", "African8",
        };
        private static readonly string[] SkinToneWords =
        {
            "fair", "fair", "light", "light", "light",
            "light olive", "light olive", "olive", "olive", "olive", "tan", "tan",
            "light brown", "light brown", "medium brown", "medium brown", "brown", "brown", "dark brown", "dark brown",
        };
        public static readonly string[] HairColours = { "black", "dark brown", "light brown", "blond", "ginger", "grey", "white" };

        public static string EthnicityLabel(string ethnicity)
        {
            int index = Array.IndexOf(Ethnicities, ethnicity);
            if (index < 0)
                throw new ArgumentException("Ethnicity is invalid.");
            return EthnicityLabels[index];
        }

        // The prompt's ethnicity: FM's own when known, otherwise a best guess
        // from the person's name and nationality.
        private static string EthnicityText(ProfileSnapshot profile, string name)
        {
            if (profile.Ethnicity == null)
                return "Ethnicity is not known: give " + name + " the most likely ethnicity and skin tone for their full " +
                       "name and nationality, remembering that many people's heritage differs from the country they represent. ";
            string text = "Ethnicity: " + EthnicityLabel(profile.Ethnicity);
            string skin = null;
            if (profile.SkinTone != null)
            {
                int tone = Array.IndexOf(SkinTones, profile.SkinTone);
                if (tone < 0)
                    throw new ArgumentException("Skin tone is invalid.");
                skin = SkinToneWords[tone] + " skin";
            }
            if (profile.HairColour != null && !HairColours.Contains(profile.HairColour))
                throw new ArgumentException("Hair colour is invalid.");
            string hair = profile.HairColour != null ? "naturally " + profile.HairColour + " hair" : null;
            if (skin != null || hair != null)
                text += ", with " + string.Join(" and ", new[] { skin, hair }.Where(part => part != null));
            return text + "; follow this over any other appearance detail. ";
        }

        // ---- Prompts -------------------------------------------------------

        public static string ProfilePrompt(ProfileSnapshot profile, string personId, string appearanceDir)
        {
            if (profile == null)
                throw new ArgumentException("Profile is missing.");
            string kind = profile.Type ?? "player";
            if (kind != "player" && kind != "staff")
                throw new ArgumentException("Profile type must be player or staff.");
            string label = kind == "staff" ? "Staff" : "Player";
            string name = Text(profile.Name, "name", label);
            string nationality = Text(profile.Nationality, "nationality", label);
            string gender = Gender(profile);
            string family = FamilySeed(personId, FamilyRelatives(profile));
            string biography = BiographyHeadline(profile.BiographyHeadline);
            string biographyText = biography.Length == 0 ? "" :
                " Biography headline: " + biography + (biography.EndsWith(".") || biography.EndsWith("!") || biography.EndsWith("?") ? "" : ".");
            int age = profile.Age;
            if (age < 14 || age > 100)
                throw new ArgumentException(label + " age is missing or invalid.");
            if (profile.Likeness)
                return LikenessPrompt(profile, kind, label, name, nationality, gender, age);
            if (kind == "staff")
            {
                string jobTitle = Text(profile.JobTitle, "jobTitle", label);
                string staffClub = string.IsNullOrEmpty(profile.Club) ? null : Text(profile.Club, "club", label);
                string clubText = staffClub != null ? " at " + staffClub : "";
                string staffAppearance = Appearance.AppearancePrompt(name, age, personId, appearanceDir, gender, family, profile);
                return "Football Manager profile picture for " + name + ", age " + age + ", nationality " + nationality + ", " +
                       "job title " + jobTitle + clubText + "." + GenderText(name, gender) + biographyText + " " +
                       "Create one plausible, original staff portrait for a sports management game. " +
                       EthnicityText(profile, name) + staffAppearance + PortraitStyle(gender) +
                       "Wear a simple unbranded dark jacket or training top. " +
                       "Use age and biography to guide appearance. No text, logos, badges, borders, or other people.";
            }
            string position = Text(profile.Position, "position", "Player");
            string club = Text(profile.Club, "club", "Player");
            string height = Text(profile.Height, "height", "Player");
            if (age > 60)
                throw new ArgumentException("Player age is missing or invalid.");
            // Only the attributes known: a partly scouted player may have some hidden.
            var physical = Stats(profile.Physical ?? new Dictionary<string, object>(), "physical");
            var unexpected = physical.Keys.Where(stat => !PhysicalStats.Contains(stat)).OrderBy(stat => stat, StringComparer.Ordinal).ToList();
            if (unexpected.Count > 0)
                throw new ArgumentException("Unknown physical stats: " + string.Join(", ", unexpected));
            var knownPhysical = PhysicalStats.Where(physical.ContainsKey).ToList();
            var other = new List<(string Section, string Stat, string Score, double Strength)>();
            foreach (var (section, values) in new[] { ("technical", profile.Technical), ("mental", profile.Mental), ("goalkeeping", profile.Goalkeeping) })
                foreach (var stat in Stats(values ?? new Dictionary<string, object>(), section))
                    other.Add((section, stat.Key, stat.Value.Text, stat.Value.Strength));
            if (other.Count < 6)
                throw new ArgumentException("At least six technical, mental, or goalkeeping stats are required.");
            var best = other.OrderBy(item => -item.Strength).ThenBy(item => item.Stat, StringComparer.Ordinal).Take(3).ToList();
            var worst = other.Where(item => !best.Contains(item)).OrderBy(item => item.Strength)
                             .ThenBy(item => item.Stat, StringComparer.Ordinal).Take(3).ToList();
            Func<List<(string Section, string Stat, string Score, double Strength)>, string> format =
                items => string.Join(", ", items.Select(item => item.Section + " " + item.Stat + " " + item.Score + "/20"));
            string firstName = Whitespace.Split(name)[0];
            string appearance = Appearance.AppearancePrompt(name, age, personId, appearanceDir, gender, family, profile);
            return "Football Manager profile picture for " + name + ", a " + age + "-year-old " + position + " " +
                   (NoClub(club) ? "who is a free agent" : "who plays for " + club) + ". Nationality: " + nationality + "." +
                   GenderText(firstName, gender) + " " + firstName + " is " + height + " tall." + biographyText + " " +
                   (knownPhysical.Count == 0 ? "" : "Physical stats: " +
                    string.Join(", ", knownPhysical.Select(stat => stat + " " + physical[stat].Text + "/20")) + ". ") +
                   "Best non-physical stats: " + format(best) + ". Weakest non-physical stats: " + format(worst) + ". " +
                   "Create one plausible, original footballer portrait for a sports management game. " +
                   EthnicityText(profile, name) + appearance + PortraitStyle(gender) +
                   KitText(club) + "Use age and physical details to guide appearance, but do not depict " +
                   "numbers. No text, logos, badges, borders, or other people.";
        }

        // ProfilePrompt for a real person: their own likeness, from who and where
        // they are, with FM's ethnicity when it is known.
        private static string LikenessPrompt(ProfileSnapshot profile, string kind, string label, string name,
                                             string nationality, string gender, int age)
        {
            string firstName = Whitespace.Split(name)[0];
            string ethnicity = profile.Ethnicity != null ? EthnicityText(profile, name) : "";
            if (kind == "staff")
            {
                string jobTitle = Text(profile.JobTitle, "jobTitle", label);
                string staffClub = string.IsNullOrEmpty(profile.Club) ? null : Text(profile.Club, "club", label);
                return "Football Manager profile picture of " + name + ", the " + age + "-year-old " + jobTitle +
                       (staffClub != null ? " at " + staffClub : "") + ". Nationality: " + nationality + "." +
                       GenderText(firstName, gender) + " Make it look like " + name + " as they look now. " + ethnicity +
                       "Wear a simple unbranded dark jacket or training top. " +
                       PortraitFraming + PortraitPhotography + PortraitBackground +
                       "No text, logos, badges, borders, or other people.";
            }
            string position = Text(profile.Position, "position", "Player");
            string club = Text(profile.Club, "club", "Player");
            if (age > 60)
                throw new ArgumentException("Player age is missing or invalid.");
            return "Football Manager profile picture of " + name + ", the " + age + "-year-old " + position + " " +
                   (NoClub(club) ? "who is a free agent" : "who plays for " + club) + ". Nationality: " + nationality + "." +
                   GenderText(firstName, gender) + " Make it look like " + name + " as they look now. " + ethnicity +
                   KitText(club) + PortraitFraming + PortraitPhotography + PortraitBackground +
                   "Do not depict numbers. No text, logos, badges, borders, or other people.";
        }

        // ProfilePrompt, for an image request whose input image is a relative's
        // RegenerateFM face: the new face takes only a slight family resemblance.
        public static string RelativePrompt(ProfileSnapshot profile, string relation, string personId, string appearanceDir)
        {
            if (!FamilyRelations.Contains(relation))
                throw new ArgumentException("Relation must be one of the family relations.");
            string prompt = ProfilePrompt(profile, personId, appearanceDir);
            string name = Text(profile.Name, "name", profile.Type == "staff" ? "Staff" : "Player");
            return prompt + " The supplied image is a portrait of " + name + "'s " + relation + ", not of " + name + ". " +
                   "Give " + name + " only a slight family resemblance to them, such as a similar face shape, eye shape or " +
                   "colouring, while keeping " + name + " clearly a different person, with their own age, build, hairstyle and " +
                   "features as described above. Do not copy the supplied face, its expression or its pose.";
        }

        // Refreshing an existing portrait supplied as the input image, which may
        // be years old: the same person, clearly aged to now, in their current
        // kit. ``personId`` seeds whether this update also changes their facial
        // hair and makes their hairstyle more grown-up (about half of updates
        // each, the same for a person at the same age). ``pictureAge`` is their
        // age in the supplied picture, when RegenerateFM made it, so the prompt
        // can say how many years to add.
        public static string UpdatePrompt(ProfileSnapshot profile, string personId = null, int? pictureAge = null)
        {
            if (profile == null)
                throw new ArgumentException("Profile is missing.");
            string kind = profile.Type ?? "player";
            if (kind != "player" && kind != "staff")
                throw new ArgumentException("Profile type must be player or staff.");
            string label = kind == "staff" ? "Staff" : "Player";
            int age = profile.Age;
            if (age < 14 || age > (kind == "staff" ? 100 : 60))
                throw new ArgumentException(label + " age is missing or invalid.");
            string name = Text(profile.Name, "name", label);
            string gender = Gender(profile);
            string attire = kind == "staff" ? "Dress them in a simple unbranded dark jacket or training top. "
                                            : KitText(Text(profile.Club, "club", label));
            string seed = (personId ?? name) + "\0" + age.ToString(CultureInfo.InvariantCulture);
            string hair = Appearance.Chance(seed, "hairstyle")
                ? "Give them a more grown-up hairstyle than in the supplied picture, such as a shorter, neater or differently styled cut that suits a " + age + "-year-old, keeping their hair colour and texture. "
                : "Their hairstyle may be cut or styled a little differently, as it naturally would be by now. ";
            string facialHair = gender != "male" ? "Do not add any facial hair. "
                : Appearance.Chance(seed, "facialHair")
                    ? "Change their facial hair from the supplied picture: if they are clean-shaven, give them natural stubble or a short beard; if they have facial hair, give them a different style or shave it off. "
                    : "Keep their facial hair much as it is, grown or trimmed naturally for their age. ";
            int years = pictureAge.HasValue ? age - pictureAge.Value : 0;
            string earlier = years > 0
                ? " The supplied image is a portrait of " + name + " at age " + pictureAge.Value + ", taken " + years +
                  (years == 1 ? " year" : " years") + " ago."
                : pictureAge.HasValue ? " The supplied image is a recent portrait of " + name + ", taken at their current age."
                : " The supplied image is an earlier portrait of " + name + " and may be several years old.";
            string ageing = years > 0
                ? "Make them look clearly " + years + (years == 1 ? " year" : " years") + " older than in the picture, aged from " +
                  pictureAge.Value + " to " + age + ", rather than copying how old they look in it. "
                : pictureAge.HasValue ? "Their age hasn't changed, so keep how old they look. "
                : "Age the face to match, rather than copying how old it looks in the picture; if it already looks about " + age +
                  ", change little. ";
            return "Update this Football Manager profile picture of " + name + "." + earlier + GenderText(name, gender, age) +
                   PositionText(profile, name) + " Show " + name + " as they look now, at age " + age + ": " + AgeSigns(age, gender) + ". " +
                   ageing + "Keep them recognisably the same person, with the same bone structure, face shape, nose, eyes, " +
                   "eye colour, skin tone and distinguishing marks, and the same hair colour apart from any natural greying. " +
                   hair + facialHair +
                   "Make it a realistic photograph of " + name + " as a real person: if the supplied picture is a " +
                   "3D render, game graphic or illustration, keep the likeness but replace its rendered look with " +
                   "real photographic skin, hair and lighting. " + attire + PortraitFraming + PortraitPhotography +
                   PortraitBackground + "Do not depict numbers. No text, logos, badges, borders, or other people.";
        }

        // How a face looks at ``age``, for Update face to age towards.
        private static string AgeSigns(int age, string gender)
        {
            string person = gender == "female" ? "woman" : gender == "male" ? "man" : "person";
            if (age < 20)
                return "a teenager's face, with youthful skin and softer features";
            if (age < 24)
                return "a young adult's face, losing its teenage softness, with a slightly more defined jaw and cheekbones";
            if (age < 28)
                return "a " + person + " in their mid-twenties: a fully adult, more defined face with no teenage softness, and slightly more textured skin";
            if (age < 33)
                return "a " + person + " around thirty: a lean, mature face with a defined jaw, faint lines across the forehead and at the corners of the eyes, " +
                       "deeper smile lines and more weathered, textured skin";
            if (age < 40)
                return "a " + person + " in their thirties: clear forehead lines and crow's feet, deeper smile lines, a little less fullness in the cheeks, " +
                       "weathered skin, and possibly a few grey hairs or a slightly higher hairline";
            return "a " + person + " of " + age + ": visible lines and wrinkles, looser skin along the jaw, greying hair and, if it suits them, " +
                   "a receding or thinning hairline";
        }

        // " Name is a Goalkeeper.", so the kit is a goalkeeper's where it should be.
        private static string PositionText(ProfileSnapshot profile, string name) =>
            (profile.Type ?? "player") == "player" && !string.IsNullOrWhiteSpace(profile.Position) && profile.Position.Length <= 120
                ? " " + name + " plays as a " + Whitespace.Replace(profile.Position.Trim(), " ") + "."
                : "";

        // UpdatePrompt, for a supplied image that is FM26's own 3D face for a
        // newgen: the render is only a guide to the person's colouring and hair,
        // and the photograph is of an ordinary person with their appearance traits.
        public static string NewgenPrompt(ProfileSnapshot profile, string personId = null, string appearanceDir = null)
        {
            if (profile == null)
                throw new ArgumentException("Profile is missing.");
            string kind = profile.Type ?? "player";
            if (kind != "player" && kind != "staff")
                throw new ArgumentException("Profile type must be player or staff.");
            string label = kind == "staff" ? "Staff" : "Player";
            int age = profile.Age;
            if (age < 14 || age > (kind == "staff" ? 100 : 60))
                throw new ArgumentException(label + " age is missing or invalid.");
            string name = Text(profile.Name, "name", label);
            string gender = Gender(profile);
            string attire = kind == "staff" ? "Dress them in a simple unbranded dark jacket or training top. "
                                            : KitText(Text(profile.Club, "club", label));
            string family = FamilySeed(personId, FamilyRelatives(profile));
            var traits = Appearance.PersonTraits(name, age, personId, appearanceDir, gender, family);
            Appearance.AppearanceInstruction(traits, gender); // refuses invalid saved traits
            string facialHair = gender == "male" ? traits["facialHair"] : Appearance.NoFacialHair;
            string youth = age < 20 ? " Show " + name + " as the teenager they are, with a youthful face and skin, not as someone in their twenties." : "";
            return "Create a realistic photograph of " + name + ", a real " + age + "-year-old, for their Football Manager profile picture. " +
                   "The supplied image is the game's computer-generated 3D face for " + name + ". Use it only as a loose guide to " +
                   name + "'s skin tone, eye colour, hair colour, hairstyle and rough face shape: it is not a likeness to copy, and its " +
                   "smooth, symmetrical, rendered features are not how a real person looks." + GenderText(name, gender, age) +
                   PositionText(profile, name) + youth + " Make " + name + " an ordinary-looking person rather than a model, with their own " +
                   "individual features: " + traits["face"] + "; " + traits["features"] + "; " + facialHair + "; " + traits["expression"] + ". " +
                   "Give them real, imperfect skin with uneven tone and texture" + (age < 22 ? " and the odd blemish" : "") +
                   ", natural asymmetry and ordinary proportions; avoid a chiselled, symmetrical, airbrushed or conventionally " +
                   "handsome look. " + attire + PortraitFraming + PortraitPhotography + PortraitBackground +
                   "Do not depict numbers. No text, logos, badges, borders, or other people.";
        }

        private static string Text(string value, string key, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || key == "name" && !GameInfo.PlausibleName(value))
                throw new ArgumentException(label + " " + key + " is missing or invalid.");
            return Whitespace.Replace(value.Trim(), " ");
        }

        private static string BiographyHeadline(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            if (value.Length > 1000)
                throw new ArgumentException("Biography headline is invalid or too long.");
            value = Paragraph.Split(value.Trim(), 2)[0];
            value = Whitespace.Replace(value.Trim(), " ");
            if (value.Length <= 600)
                return value;
            value = value.Substring(0, 600);
            int space = value.LastIndexOf(' ');
            return space >= 0 ? value.Substring(0, space) : value;
        }

        private static Dictionary<string, (string Text, double Strength)> Stats(Dictionary<string, object> values, string section)
        {
            if (values == null)
                throw new ArgumentException("Player " + section + " stats are missing.");
            var result = new Dictionary<string, (string, double)>();
            foreach (var entry in values)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 60)
                    throw new ArgumentException("Player " + section + " has an invalid stat name.");
                string name = entry.Key.Trim();
                if (entry.Value is int score && score >= 1 && score <= 20)
                    result[name] = (score.ToString(CultureInfo.InvariantCulture), score);
                else if (entry.Value is string text && Range.IsMatch(text.Trim()))
                {
                    var parts = RangeSplit.Split(text.Trim());
                    int low = int.Parse(parts[0], CultureInfo.InvariantCulture), high = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (low > high)
                        throw new ArgumentException("Player " + section + " stat " + entry.Key + " has an inverted range.");
                    result[name] = (low + "–" + high, (low + high) / 2.0);
                }
                else
                    throw new ArgumentException("Player " + section + " stat " + entry.Key + " must be 1–20 or a scouted range.");
            }
            return result;
        }

        public static bool NoClub(string club)
        {
            string folded = (club ?? "").Trim().ToLowerInvariant();
            return folded == "free agent" || folded == "unattached" || folded == "no club";
        }

        private static string KitText(string club)
        {
            if (NoClub(club))
                return "The player has no club; use a plain unbranded football shirt. ";
            return "Make sure the player is wearing the home kit and colours of " + club + ", " +
                   "with the club's recognisable shirt colours and kit pattern. " +
                   "For a goalkeeper, use the club's goalkeeper kit. ";
        }
    }
}
