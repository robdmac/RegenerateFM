using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RegenerateFM
{
    // Faces a long save has left behind: a RegenerateFM face made when the
    // person was two or more years younger (FaceHistory notes their age when
    // it was made). The Squad menu's Update aged faces… updates them, and once
    // a season (an in-game year) the plugin offers to, for each team: the
    // years it has offered are kept in aged-faces-offered.json beside the key.
    public static class AgedFaces
    {
        public const int Years = 2;
        public const string FileName = "aged-faces-offered.json";
        public static string OfferedPath => Path.Combine(KeyStore.Folder, FileName);

        // Whether a face is out of date: made two or more in-game years before
        // ``yearNow`` (FM27's squad lists show no ages), or, for a face from
        // before RegenerateFM noted the year, at an age two or more years
        // below ``ageNow`` (0 when the list doesn't show it).
        public static bool IsAged(int? ageWhenMade, int ageNow, int? yearMade = null, int? yearNow = null)
        {
            if (yearMade.HasValue && yearNow.HasValue)
                return yearNow.Value - yearMade.Value >= Years;
            return ageWhenMade.HasValue && ageNow > 0 && ageNow - ageWhenMade.Value >= Years;
        }

        // FM's date as its top bar shows it, in any language's order
        // ("28/3/2027", "3/28/2027", "2027/3/28", "28.3.2027"), with or without
        // a day name: its year, or null if the text isn't a date.
        private static readonly Regex Date = new Regex(
            @"(?:^|[^0-9])(?:(\d{1,2})[./-](\d{1,2})[./-]((?:19|20|21)\d{2})|((?:19|20|21)\d{2})[./-](\d{1,2})[./-](\d{1,2}))(?:$|[^0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int? GameYear(string text)
        {
            var match = Date.Match(text ?? "");
            if (!match.Success)
                return null;
            return int.Parse(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Whether ``team``'s aged faces have been offered in ``year`` (or later).
        public static bool Offered(string path, string team, int year) =>
            Load(path).TryGetValue(team ?? "", out int offered) && offered >= year;

        public static void Record(string path, string team, int year)
        {
            var offered = Load(path);
            offered[team ?? ""] = year;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(offered, new JsonSerializerOptions { WriteIndented = true })));
        }

        private static Dictionary<string, int> Load(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ??
                           new Dictionary<string, int>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { }
            return new Dictionary<string, int>();
        }
    }
}
