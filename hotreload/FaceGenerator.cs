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

namespace FMFaceHotReload
{
    // The profile read from FM26's screen. Stat values are an int (1-20) or a
    // scouted range string such as "12-15".
    internal sealed class ProfileSnapshot
    {
        public string Type = "player";
        public string Name;
        public int Age;
        public string Nationality;
        public string JobTitle;
        public string Position;
        public string Club;
        public string Height;
        public string BiographyHeadline;
        public Dictionary<string, object> Physical = new Dictionary<string, object>();
        public Dictionary<string, object> Technical = new Dictionary<string, object>();
        public Dictionary<string, object> Mental = new Dictionary<string, object>();
        public Dictionary<string, object> Goalkeeping = new Dictionary<string, object>();
    }

    internal sealed class ImageSettings
    {
        public string ApiKey;
        public string Model = "gpt-image-2.5-sunburst";
        public string Quality = "medium";
        public string BaseUrl = "https://api.openai.com/v1";
    }

    // Prompts, the image request and the saved portrait. This is the FM26 copy
    // of fmface.py's generator; tests/test_fm26_generator.py checks that both
    // build the same prompts.
    internal static class FaceGenerator
    {
        public const int PortraitSize = 256;
        public const int MaxReference = 8 << 20;
        public static readonly string[] Qualities = { "low", "medium", "high", "xhigh", "max", "auto" };
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
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        private static readonly object SaveLock = new object();

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
        private const string PortraitStyle =
            PortraitFraming +
            "Follow the individual's supplied expression; otherwise use a relaxed neutral expression. " +
            PortraitPhotography +
            "Give this individual distinctive facial proportions, nose, jaw, eyes, hairline and hairstyle " +
            "consistent with the supplied appearance details and age. Allow varied hair lengths and " +
            "textures and age-appropriate facial hair; avoid defaulting to a model-like face or the " +
            "same tousled brown haircut. " + PortraitBackground;

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
            string biography = BiographyHeadline(profile.BiographyHeadline);
            string biographyText = biography.Length == 0 ? "" :
                " Biography headline: " + biography + (biography.EndsWith(".") || biography.EndsWith("!") || biography.EndsWith("?") ? "" : ".");
            int age = profile.Age;
            if (age < 14 || age > 100)
                throw new ArgumentException(label + " age is missing or invalid.");
            if (kind == "staff")
            {
                string jobTitle = Text(profile.JobTitle, "jobTitle", label);
                string staffClub = string.IsNullOrEmpty(profile.Club) ? null : Text(profile.Club, "club", label);
                string clubText = staffClub != null ? " at " + staffClub : "";
                string staffAppearance = AppearancePrompt(name, age, personId, appearanceDir);
                return "Football Manager profile picture for " + name + ", age " + age + ", nationality " + nationality + ", " +
                       "job title " + jobTitle + clubText + "." + biographyText + " " +
                       "Create one plausible, original staff portrait for a sports management game. " +
                       staffAppearance + PortraitStyle +
                       "Wear a simple unbranded dark jacket or training top. " +
                       "Use age and biography to guide appearance, but do not infer " +
                       "ethnicity from nationality. No text, logos, badges, borders, or other people.";
            }
            string position = Text(profile.Position, "position", "Player");
            string club = Text(profile.Club, "club", "Player");
            string height = Text(profile.Height, "height", "Player");
            if (age > 60)
                throw new ArgumentException("Player age is missing or invalid.");
            var physical = Stats(profile.Physical, "physical");
            var missing = PhysicalStats.Where(stat => !physical.ContainsKey(stat)).OrderBy(stat => stat, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                throw new ArgumentException("Missing physical stats: " + string.Join(", ", missing));
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
            string appearance = AppearancePrompt(name, age, personId, appearanceDir);
            return "Football Manager profile picture for " + name + ", a " + age + "-year-old " + position + " " +
                   "who plays for " + club + ". Nationality: " + nationality + ". " + firstName + " is " + height + " tall." + biographyText + " " +
                   "Physical stats: " + string.Join(", ", PhysicalStats.Select(stat => stat + " " + physical[stat].Text + "/20")) + ". " +
                   "Best non-physical stats: " + format(best) + ". Weakest non-physical stats: " + format(worst) + ". " +
                   "Create one plausible, original footballer portrait for a sports management game. " +
                   appearance + PortraitStyle +
                   KitText(club) + "Use age and physical details to guide appearance, but do not depict " +
                   "numbers or infer ethnicity from nationality. No text, logos, badges, borders, or other people.";
        }

        // Refreshing an existing portrait supplied as the input image: only the
        // person's current age and attire change.
        public static string UpdatePrompt(ProfileSnapshot profile)
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
            string attire = kind == "staff" ? "Dress them in a simple unbranded dark jacket or training top. "
                                            : KitText(Text(profile.Club, "club", label));
            return "Update this Football Manager profile picture of " + name + ". The supplied image is " +
                   name + "'s current portrait. Keep them clearly the same person, with the same face shape, " +
                   "features, skin tone, eye colour, hairstyle, facial hair and distinguishing marks. " +
                   "Show " + name + " as they look now, at age " + age + ": if the supplied picture looks younger or " +
                   "older, change only the natural signs of age, such as hairline, hair colour, skin " +
                   "texture and facial lines. " + attire + PortraitFraming + PortraitPhotography +
                   PortraitBackground + "Otherwise make it an updated version of the supplied picture of " + name + ". " +
                   "Do not depict numbers. No text, logos, badges, borders, or other people.";
        }

        private static string Text(string value, string key, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
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

        private static string KitText(string club)
        {
            string folded = club.ToLowerInvariant();
            if (folded == "free agent" || folded == "unattached" || folded == "no club")
                return "The player has no club; use a plain unbranded football shirt. ";
            return "Make sure the player is wearing the home kit and colours of " + club + ", " +
                   "with the club's recognisable shirt colours and kit pattern. " +
                   "For a goalkeeper, use the club's goalkeeper kit. ";
        }

        // ---- Appearance traits (appearance.py) -----------------------------

        private static readonly string[] TraitKeys = { "face", "features", "hair", "facialHair", "expression" };

        private static string AppearancePrompt(string name, int age, string personId, string directory)
        {
            if (personId == null)
            {
                if (directory != null)
                    throw new ArgumentException("A person ID is required to save an appearance profile.");
                return AppearanceInstruction(AppearanceTraits("name:" + name.ToLowerInvariant(), age));
            }
            ValidateId(personId);
            string path = directory != null ? Path.Combine(directory, ".fmface-appearances", personId + ".json") : null;
            if (path != null && File.Exists(path))
            {
                using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                        !root.TryGetProperty("personId", out var savedId) || savedId.ValueKind != JsonValueKind.String || savedId.GetString() != personId ||
                        !root.TryGetProperty("traits", out var savedTraits) || savedTraits.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Saved appearance profile is invalid: " + Path.GetFileName(path));
                    var traits = new Dictionary<string, string>();
                    foreach (var property in savedTraits.EnumerateObject())
                        traits[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    return AppearanceInstruction(traits);
                }
            }
            var chosen = AppearanceTraits(personId, age);
            string instruction = AppearanceInstruction(chosen);
            if (path != null)
            {
                var saved = new Dictionary<string, object> { ["schemaVersion"] = 1, ["personId"] = personId, ["traits"] = chosen };
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                AtomicWrite(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved, options) + "\n"));
            }
            return instruction;
        }

        public static Dictionary<string, string> AppearanceTraits(string seed, int age)
        {
            Func<string, string[], string> pick = (category, choices) =>
            {
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(Encoding.UTF8.GetBytes("fmface-appearance-v1\0" + seed + "\0" + category));
                ulong value = 0;
                for (int i = 0; i < 8; i++)
                    value = (value << 8) | digest[i];
                return choices[(int)(value % (ulong)choices.Length)];
            };
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
            string style = pick("hairStyle", styles.ToArray());
            var colours = new List<string> { "black", "dark brown", "medium brown", "light brown", "dark blond", "auburn" };
            if (age >= 45)
                colours.AddRange(new[] { "salt-and-pepper", "greying brown", "silver-grey" });
            string hair = style == "a shaved head" ? style : style + "; hair colour: " + pick("hairColour", colours.ToArray());
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
            var values = new[] { face, features, hair, pick("facialHair", facialHair.ToArray()), expression };
            var traits = new Dictionary<string, string>();
            for (int i = 0; i < TraitKeys.Length; i++)
                traits[TraitKeys[i]] = values[i];
            return traits;
        }

        private static string AppearanceInstruction(Dictionary<string, string> traits)
        {
            if (traits == null || traits.Count != TraitKeys.Length || TraitKeys.Any(key => !traits.ContainsKey(key)) ||
                traits.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200))
                throw new ArgumentException("Saved appearance traits are invalid.");
            return "Individual appearance: " + string.Join("; ", TraitKeys.Select(key => traits[key])) + ". " +
                   "Keep these features understated, anatomically natural and consistent with the person's age. " +
                   "Preserve subtle facial asymmetry and ordinary skin texture. ";
        }

        // ---- Image API -------------------------------------------------------

        public static void CheckSettings(ImageSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new MissingApiKeyException();
            if (Array.IndexOf(Qualities, settings.Quality) < 0)
                throw new ArgumentException("Quality in fmface.hotreload.cfg must be low, medium, high, xhigh, max, or auto.");
            if (string.IsNullOrWhiteSpace(settings.Model))
                throw new ArgumentException("Model in fmface.hotreload.cfg is empty.");
        }

        // Asks OpenAI whether the key works, with a free request that lists
        // models. Returns null when it does, else a message for the player.
        public static async Task<string> CheckKey(ImageSettings settings, string key, CancellationToken cancel = default)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl.TrimEnd('/') + "/models"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                try
                {
                    using (var response = await Client.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                            return null;
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            return "OpenAI didn't accept this key. Check that you copied all of it.";
                        return "OpenAI couldn't check the key (HTTP " + (int)response.StatusCode + "). Try again.";
                    }
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
                {
                    return "Could not reach OpenAI to check the key: " + error.Message.Replace(key, "[REDACTED]");
                }
            }
        }

        private static List<KeyValuePair<string, string>> Fields(ImageSettings settings, string prompt)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("model", settings.Model),
                new KeyValuePair<string, string>("size", "1024x1024"),
                new KeyValuePair<string, string>("quality", settings.Quality),
                new KeyValuePair<string, string>("background", "transparent"),
                new KeyValuePair<string, string>("output_format", "png"),
                new KeyValuePair<string, string>("prompt", prompt),
            };
        }

        public static Task<byte[]> GeneratePng(ImageSettings settings, string prompt, CancellationToken cancel = default)
        {
            CheckSettings(settings);
            var body = Fields(settings, prompt).ToDictionary(field => field.Key, field => field.Value);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return ImageRequest(settings, "generations", content, cancel);
        }

        public static Task<byte[]> EditPng(ImageSettings settings, string prompt, byte[] reference, CancellationToken cancel = default)
        {
            if (!Png.IsPng(reference))
                throw new ArgumentException("The existing portrait must be a PNG image.");
            if (reference.Length > MaxReference)
                throw new ArgumentException("The existing portrait is too large to send.");
            CheckSettings(settings);
            var content = new MultipartFormDataContent("fmface-" + Guid.NewGuid().ToString("N"));
            foreach (var field in Fields(settings, prompt))
                content.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
            var image = new ByteArrayContent(reference);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(image, "image[]", "portrait.png");
            return ImageRequest(settings, "edits", content, cancel);
        }

        private static async Task<byte[]> ImageRequest(ImageSettings settings, string endpoint, HttpContent content, CancellationToken cancel)
        {
            string key = settings.ApiKey.Trim();
            using (content)
            using (var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/images/" + endpoint) { Content = content })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, cancel).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
                {
                    throw new InvalidOperationException("Could not reach the OpenAI image API: " + error.Message.Replace(key, "[REDACTED]"));
                }
                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = "";
                        try
                        {
                            using (var json = JsonDocument.Parse(body))
                                detail = json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
                        }
                        catch (Exception) { }
                        if (detail.Length == 0)
                            detail = response.ReasonPhrase ?? "";
                        string message = "OpenAI image API returned HTTP " + (int)response.StatusCode + ": " + detail.Replace(key, "[REDACTED]");
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            throw new ApiKeyRejectedException(message);
                        throw new InvalidOperationException(message);
                    }
                    string image;
                    try
                    {
                        using (var json = JsonDocument.Parse(body))
                            image = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
                    }
                    catch (Exception)
                    {
                        throw new InvalidOperationException("Image API response did not contain an image.");
                    }
                    byte[] png;
                    try
                    {
                        png = Convert.FromBase64String(image ?? "");
                    }
                    catch (FormatException)
                    {
                        throw new InvalidOperationException("Image API returned invalid image data.");
                    }
                    if (png.Length == 0)
                        throw new InvalidOperationException("Image API response did not contain an image.");
                    if (!Png.IsPng(png))
                        throw new InvalidOperationException("Image API returned data that is not a PNG.");
                    return FitPortrait(png);
                }
            }
        }

        // One in-game request: build the prompt, ask the image API and save the
        // face. Update face prefers FM Face's own saved file to the game's
        // on-screen copy. Nothing is saved when the request fails.
        public static async Task<string> MakePortrait(ImageSettings settings, string folder, string personId,
                                                      ProfileSnapshot profile, bool update, byte[] shownPng,
                                                      CancellationToken cancel = default)
        {
            ValidateId(personId);
            CheckGraphicsConfig(folder);
            byte[] png;
            if (update)
            {
                string prompt = UpdatePrompt(profile);
                var reference = CurrentPortrait(folder, personId) ?? shownPng;
                if (reference == null)
                    throw new InvalidOperationException("This person has no face to update yet. Use Generate face instead.");
                png = await EditPng(settings, prompt, reference, cancel).ConfigureAwait(false);
            }
            else
            {
                string prompt = ProfilePrompt(profile, personId, folder);
                png = await GeneratePng(settings, prompt, cancel).ConfigureAwait(false);
            }
            return SavePortrait(folder, personId, png);
        }

        // FM draws portraits at 256×256; the image API's smallest square output
        // is 1024×1024, so faces are scaled down, keeping transparency.
        public static byte[] FitPortrait(byte[] png)
        {
            Png.Image image;
            try
            {
                image = Png.Decode(png);
            }
            catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
            {
                throw new InvalidOperationException("Image API returned a PNG that could not be read: " + error.Message);
            }
            if (image.Width == PortraitSize && image.Height == PortraitSize)
                return png;
            var scaled = Png.Resize(image, PortraitSize, PortraitSize);
            return Png.Encode(scaled.Rgba, scaled.Width, scaled.Height, bottomUp: false);
        }

        // ---- Saving into FM26's graphics folder ----------------------------

        public static string ValidateId(string value)
        {
            if (value == null || !IdPattern.IsMatch(value))
                throw new ArgumentException("Player ID must contain only digits (up to 20).");
            return value;
        }

        // FM Face's saved portrait for this ID, if config.xml still maps it.
        public static byte[] CurrentPortrait(string directory, string personId)
        {
            ValidateId(personId);
            string path = Path.Combine(directory, personId + ".png");
            if (!File.Exists(path))
                return null;
            var (_, maps) = LoadMaps(directory);
            var targets = new HashSet<string> { "graphics/pictures/person/" + personId + "/portrait",
                                                "graphics/pictures/person/r-" + personId + "/portrait" };
            if (!maps.Elements("record").Any(record => (string)record.Attribute("from") == personId &&
                                                       targets.Contains((string)record.Attribute("to"))))
                return null;
            var png = File.ReadAllBytes(path);
            return Png.IsPng(png) && png.Length <= MaxReference ? png : null;
        }

        public static void CheckGraphicsConfig(string directory)
        {
            LoadMaps(directory);
        }

        private static (XDocument Document, XElement Maps) LoadMaps(string directory)
        {
            string path = Path.Combine(directory, "config.xml");
            if (!File.Exists(path))
            {
                var maps = new XElement("list", new XAttribute("id", "maps"));
                return (new XDocument(new XElement("record", maps)), maps);
            }
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch (XmlException error)
            {
                throw new ArgumentException("config.xml in the FM Face graphics folder could not be read: " + error.Message);
            }
            var found = document.Root?.Elements("list").FirstOrDefault(list => (string)list.Attribute("id") == "maps");
            if (found == null)
                throw new ArgumentException("Existing config.xml has no maps list; refusing to alter it.");
            return (document, found);
        }

        // Saves the portrait and maps it to the person's ordinary and
        // generated-player IDs. The image is rolled back if config.xml fails.
        public static string SavePortrait(string directory, string personId, byte[] png)
        {
            ValidateId(personId);
            if (!Png.IsPng(png))
                throw new ArgumentException("Portrait must be a PNG image.");
            lock (SaveLock)
            {
                Directory.CreateDirectory(directory);
                string imagePath = Path.Combine(directory, personId + ".png");
                string configPath = Path.Combine(directory, "config.xml");
                var (document, maps) = LoadMaps(directory);
                foreach (var record in maps.Elements("record").Where(record => (string)record.Attribute("from") == personId).ToList())
                    record.Remove();
                foreach (var prefix in new[] { "", "r-" })
                    maps.Add(new XElement("record", new XAttribute("from", personId),
                                          new XAttribute("to", "graphics/pictures/person/" + prefix + personId + "/portrait")));
                byte[] xml;
                using (var buffer = new MemoryStream())
                {
                    using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) }))
                        document.Save(writer);
                    xml = buffer.ToArray();
                }
                byte[] previous = File.Exists(imagePath) ? File.ReadAllBytes(imagePath) : null;
                AtomicWrite(imagePath, png);
                try
                {
                    AtomicWrite(configPath, xml);
                }
                catch (Exception)
                {
                    try
                    {
                        if (previous == null)
                            File.Delete(imagePath);
                        else
                            AtomicWrite(imagePath, previous);
                    }
                    catch (Exception rollback)
                    {
                        throw new InvalidOperationException("Could not save config.xml or restore the previous portrait: " + rollback.Message);
                    }
                    throw;
                }
                return imagePath;
            }
        }

        private static void AtomicWrite(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".fmface-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    internal sealed class MissingApiKeyException : ArgumentException
    {
        public MissingApiKeyException() : base("No OpenAI API key is saved yet.") { }
    }

    internal sealed class ApiKeyRejectedException : InvalidOperationException
    {
        public ApiKeyRejectedException(string message) : base(message) { }
    }

    // The player's OpenAI key, kept outside the game folder (which people zip
    // up when asking for mod support). On Windows, and under Proton, it is
    // encrypted with DPAPI for the current Windows user. On macOS it is in
    // Application Support, readable only by the user.
    internal static class KeyStore
    {
        private static readonly byte[] Protected = Encoding.ASCII.GetBytes("FMFK1\n");
        private static readonly byte[] Plain = Encoding.ASCII.GetBytes("FMFK0\n");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FM Face OpenAI API key");
        private static readonly Regex KeyPattern = new Regex(@"^sk-[A-Za-z0-9_\-]{16,300}$", RegexOptions.Compiled);

        // .NET 6 maps ApplicationData to ~/.config on macOS, not where Mac programs keep their data.
        public static string ApplicationSupport =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");

        public static string DefaultPath =>
            Path.Combine(OperatingSystem.IsMacOS() ? ApplicationSupport : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "FM Face", "openai-key");

        // The pasted key without surrounding spaces, or an exception whose
        // message tells the player what is wrong.
        public static string Clean(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0)
                throw new ArgumentException("Paste your OpenAI API key first.");
            if (!KeyPattern.IsMatch(key))
                throw new ArgumentException("That doesn't look like an OpenAI API key. It starts with sk- and has no spaces.");
            return key;
        }

        public static string Load(string path)
        {
            if (!File.Exists(path))
                return null;
            var data = File.ReadAllBytes(path);
            byte[] payload;
            if (StartsWith(data, Protected))
                payload = Unprotect(data.Skip(Protected.Length).ToArray());
            else if (StartsWith(data, Plain))
                payload = data.Skip(Plain.Length).ToArray();
            else
                throw new InvalidDataException("The saved OpenAI key file is not in FM Face's format: " + path);
            string key = Encoding.UTF8.GetString(payload).Trim();
            return key.Length > 0 ? key : null;
        }

        public static void Save(string path, string key)
        {
            var payload = Encoding.UTF8.GetBytes(Clean(key));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = OperatingSystem.IsWindows() ? Protected.Concat(Protect(payload)).ToArray() : Plain.Concat(payload).ToArray();
            string temporary = path + ".tmp";
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Owner-only before the key is written, like keystore.py's 0600.
                    using (File.Create(temporary)) { }
                    if (chmod(temporary, OwnerReadWrite) != 0)
                        throw new IOException("Could not make the key file private (error " + Marshal.GetLastWin32Error() + ").");
                }
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            return data.Length >= prefix.Length && data.Take(prefix.Length).SequenceEqual(prefix);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
                                                    IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
                                                      IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private const uint OwnerReadWrite = 0x180; // 0600

        private const int UiForbidden = 0x1;

        private static byte[] Protect(byte[] data) => Transform(data, protect: true);

        private static byte[] Unprotect(byte[] data)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidDataException("The saved OpenAI key is encrypted for Windows and can't be read here.");
            return Transform(data, protect: false);
        }

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var output = new DataBlob();
            try
            {
                var inputBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref inputBlob, "FM Face OpenAI API key", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok)
                    throw new InvalidDataException((protect ? "Could not encrypt" : "Could not decrypt") +
                                                   " the OpenAI key (Windows error " + Marshal.GetLastWin32Error() + ").");
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                input.Free();
                entropy.Free();
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }
    }

    // Minimal PNG support: FM26's IL2CPP interop can't encode PNGs, and the
    // generator must read the API's 8-bit RGB or RGBA output to scale it.
    internal static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        internal sealed class Image
        {
            public int Width, Height;
            public byte[] Rgba;  // top row first
        }

        public static bool IsPng(byte[] data)
        {
            if (data == null || data.Length < Signature.Length)
                return false;
            for (int i = 0; i < Signature.Length; i++)
                if (data[i] != Signature[i])
                    return false;
            return true;
        }

        public static Image Decode(byte[] png)
        {
            if (!IsPng(png))
                throw new InvalidDataException("not a PNG");
            int width = 0, height = 0, colourType = -1;
            var idat = new MemoryStream();
            int offset = 8;
            while (offset + 12 <= png.Length)
            {
                int length = (int)ReadBigEndian(png, offset);
                string type = Encoding.ASCII.GetString(png, offset + 4, 4);
                if (length < 0 || offset + 12 + length > png.Length)
                    throw new InvalidDataException("truncated chunk");
                if (type == "IHDR")
                {
                    width = (int)ReadBigEndian(png, offset + 8);
                    height = (int)ReadBigEndian(png, offset + 12);
                    int depth = png[offset + 16];
                    colourType = png[offset + 17];
                    if (depth != 8 || (colourType != 2 && colourType != 6) || png[offset + 20] != 0)
                        throw new NotSupportedException("only 8-bit, non-interlaced RGB or RGBA PNGs are supported");
                    if (width < 1 || height < 1 || width > 8192 || height > 8192)
                        throw new InvalidDataException("unexpected image size");
                }
                else if (type == "IDAT")
                    idat.Write(png, offset + 8, length);
                else if (type == "IEND")
                    break;
                offset += 12 + length;
            }
            if (colourType < 0)
                throw new InvalidDataException("no image header");
            int channels = colourType == 6 ? 4 : 3;
            int stride = width * channels;
            var raw = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var zlib = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int count = zlib.Read(raw, read, raw.Length - read);
                    if (count == 0)
                        throw new InvalidDataException("image data is truncated");
                    read += count;
                }
            }
            var pixels = new byte[stride * height];
            for (int row = 0; row < height; row++)
            {
                int filter = raw[row * (stride + 1)];
                int source = row * (stride + 1) + 1, target = row * stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= channels ? pixels[target + i - channels] : 0;
                    int b = row > 0 ? pixels[target - stride + i] : 0;
                    int c = row > 0 && i >= channels ? pixels[target - stride + i - channels] : 0;
                    int x = raw[source + i];
                    switch (filter)
                    {
                        case 0: break;
                        case 1: x += a; break;
                        case 2: x += b; break;
                        case 3: x += (a + b) >> 1; break;
                        case 4:
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            x += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        default: throw new InvalidDataException("unknown row filter");
                    }
                    pixels[target + i] = (byte)x;
                }
            }
            if (channels == 4)
                return new Image { Width = width, Height = height, Rgba = pixels };
            var rgba = new byte[width * height * 4];
            for (int i = 0, j = 0; i < pixels.Length; i += 3, j += 4)
            {
                rgba[j] = pixels[i];
                rgba[j + 1] = pixels[i + 1];
                rgba[j + 2] = pixels[i + 2];
                rgba[j + 3] = 255;
            }
            return new Image { Width = width, Height = height, Rgba = rgba };
        }

        // Lanczos-3 resampling (as Pillow's LANCZOS) with premultiplied alpha,
        // so transparent pixels don't darken the hair edges.
        public static Image Resize(Image image, int width, int height)
        {
            int count = image.Width * image.Height;
            var premultiplied = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                float alpha = image.Rgba[i * 4 + 3] / 255f;
                premultiplied[i * 4] = image.Rgba[i * 4] * alpha;
                premultiplied[i * 4 + 1] = image.Rgba[i * 4 + 1] * alpha;
                premultiplied[i * 4 + 2] = image.Rgba[i * 4 + 2] * alpha;
                premultiplied[i * 4 + 3] = image.Rgba[i * 4 + 3];
            }
            var horizontal = ResampleAxis(premultiplied, image.Width, image.Height, width, horizontalAxis: true);
            var both = ResampleAxis(horizontal, width, image.Height, height, horizontalAxis: false);
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                float alpha = Math.Clamp(both[i * 4 + 3], 0f, 255f);
                rgba[i * 4 + 3] = (byte)Math.Round(alpha);
                for (int c = 0; c < 3; c++)
                    rgba[i * 4 + c] = alpha <= 0f ? (byte)0 : (byte)Math.Round(Math.Clamp(both[i * 4 + c] * 255f / alpha, 0f, 255f));
            }
            return new Image { Width = width, Height = height, Rgba = rgba };
        }

        private static float[] ResampleAxis(float[] source, int width, int height, int size, bool horizontalAxis)
        {
            int sourceSize = horizontalAxis ? width : height;
            double scale = (double)sourceSize / size;
            double support = 3.0 * Math.Max(scale, 1.0);
            int outWidth = horizontalAxis ? size : width, outHeight = horizontalAxis ? height : size;
            var result = new float[outWidth * outHeight * 4];
            for (int o = 0; o < size; o++)
            {
                double centre = (o + 0.5) * scale;
                int first = Math.Max(0, (int)Math.Floor(centre - support));
                int last = Math.Min(sourceSize - 1, (int)Math.Ceiling(centre + support));
                var weights = new double[last - first + 1];
                double total = 0;
                for (int s = first; s <= last; s++)
                {
                    double weight = Lanczos((s + 0.5 - centre) / Math.Max(scale, 1.0));
                    weights[s - first] = weight;
                    total += weight;
                }
                int lines = horizontalAxis ? height : width;
                for (int line = 0; line < lines; line++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    for (int s = first; s <= last; s++)
                    {
                        double weight = weights[s - first];
                        if (weight == 0)
                            continue;
                        int index = (horizontalAxis ? line * width + s : s * width + line) * 4;
                        r += source[index] * weight;
                        g += source[index + 1] * weight;
                        b += source[index + 2] * weight;
                        a += source[index + 3] * weight;
                    }
                    int target = (horizontalAxis ? line * outWidth + o : o * outWidth + line) * 4;
                    result[target] = (float)(r / total);
                    result[target + 1] = (float)(g / total);
                    result[target + 2] = (float)(b / total);
                    result[target + 3] = (float)(a / total);
                }
            }
            return result;
        }

        private static double Lanczos(double x)
        {
            if (x == 0)
                return 1;
            if (x <= -3 || x >= 3)
                return 0;
            double pi = Math.PI * x;
            return 3 * Math.Sin(pi) * Math.Sin(pi / 3) / (pi * pi);
        }

        // 8-bit RGBA rows as a PNG. Unity's pixel rows are bottom first.
        public static byte[] Encode(byte[] rgba, int width, int height, bool bottomUp)
        {
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int row = 0; row < height; row++)
            {
                int source = (bottomUp ? height - 1 - row : row) * stride, target = row * (stride + 1);
                // The "up" filter compresses portraits better than none.
                raw[target] = (byte)(row == 0 ? 0 : 2);
                for (int i = 0; i < stride; i++)
                {
                    int above = row == 0 ? 0 : rgba[(bottomUp ? height - row : row - 1) * stride + i];
                    raw[target + 1 + i] = (byte)(rgba[source + i] - above);
                }
            }
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Optimal))
                    zlib.Write(raw, 0, raw.Length);
                compressed = buffer.ToArray();
            }
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // RGBA
            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                WriteChunk(png, "IHDR", header);
                WriteChunk(png, "IDAT", compressed);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var bytes = new byte[12 + data.Length];
            WriteBigEndian(bytes, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, bytes, 4);
            Buffer.BlockCopy(data, 0, bytes, 8, data.Length);
            uint crc = 0xffffffff;
            for (int i = 4; i < 8 + data.Length; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320 & (uint)-(int)(crc & 1));
            }
            WriteBigEndian(bytes, 8 + data.Length, ~crc);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static uint ReadBigEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);
        }

        private static void WriteBigEndian(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }
    }
}
