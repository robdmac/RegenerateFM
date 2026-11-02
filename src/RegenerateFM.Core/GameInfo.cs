using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RegenerateFM
{
    // Which Football Manager a build is, from Unity's app.info ("Sports
    // Interactive", then the product name), for a build whose binaries the
    // plugin hasn't been checked against: FM keeps its user data under the
    // same product name.
    public sealed class GameInfo
    {
        private static readonly Regex Product = new Regex(@"^Football Manager (\d{2})( Demo)?$", RegexOptions.CultureInvariant);

        public int Number;          // 26, 27...
        public bool Demo;
        public string UserDataName; // "Football Manager 27"

        public string ShortName => "FM" + Number.ToString(CultureInfo.InvariantCulture);

        // Null unless the file names a Sports Interactive game from FM26 on,
        // the first this plugin is for.
        public static GameInfo FromAppInfo(string text)
        {
            var lines = (text ?? "").Split('\n').Select(line => line.Trim()).ToArray();
            if (lines.Length < 2 || lines[0] != "Sports Interactive")
                return null;
            var match = Product.Match(lines[1]);
            if (!match.Success)
                return null;
            int number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (number < 26)
                return null;
            return new GameInfo { Number = number, Demo = match.Groups[2].Success, UserDataName = lines[1] };
        }

        // Whether text read as a person's name can be one: a squad number or
        // other text read in its place (as FM27's banner once gave) has no letters.
        public static bool PlausibleName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Any(char.IsLetter);
    }
}
