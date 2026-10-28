using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RegenerateFM
{
    // RegenerateFM's own text in the game's interface language. The English
    // text is the key: Translations/<language>.json maps it to the language's,
    // for FM's language names ("german", "spanish (latam)" in
    // spanish-latam.json). Text with no translation stays English, and a
    // regional language falls back to its base ("spanish").
    //
    // Only text the player reads goes through here: messages that only reach
    // a log, and the low-level reasons a game's memory or screen couldn't be
    // read, stay English, so a report of them can be read.
    public static class Text
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Loaded =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static readonly object Gate = new object();
        private static string _language = "english";

        // FM's name for the interface language, as in its preferences
        // ("german", "english (us)"). English when unknown.
        public static string Language
        {
            get => _language;
            set => _language = string.IsNullOrWhiteSpace(value) ? "english" : value.Trim().ToLowerInvariant();
        }

        // Whether the language is written right to left (FM's only one is Arabic).
        public static bool RightToLeft => _language == "arabic";

        // The text in the current language. Placeholders are string.Format's
        // ("Generating {0} ({1}/{2})…").
        public static string T(string english) => Lookup(english, _language);

        public static string T(string english, params object[] values) =>
            string.Format(CultureInfo.InvariantCulture, Lookup(english, _language), values);

        // For a test or a caller that knows the language.
        public static string In(string language, string english) => Lookup(english, (language ?? "english").Trim().ToLowerInvariant());

        private static string Lookup(string english, string language)
        {
            if (english == null || language.StartsWith("english", StringComparison.Ordinal))
                return english;
            if (Table(language).TryGetValue(english, out var text) && !string.IsNullOrEmpty(text))
                return text;
            int regional = language.IndexOf(" (", StringComparison.Ordinal);
            if (regional > 0 && Table(language.Substring(0, regional)).TryGetValue(english, out text) && !string.IsNullOrEmpty(text))
                return text;
            return english;
        }

        // A language's translations, read once from the assembly's resources.
        internal static Dictionary<string, string> Table(string language)
        {
            lock (Gate)
            {
                if (Loaded.TryGetValue(language, out var table))
                    return table;
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    using var stream = typeof(Text).Assembly.GetManifestResourceStream("Translations/" + FileName(language));
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        using var document = JsonDocument.Parse(reader.ReadToEnd());
                        foreach (var entry in document.RootElement.EnumerateObject())
                            if (entry.Value.ValueKind == JsonValueKind.String)
                                table[entry.Name] = entry.Value.GetString();
                    }
                }
                catch (Exception error) when (error is IOException || error is JsonException)
                {
                    // A broken file leaves the language English.
                }
                Loaded[language] = table;
                return table;
            }
        }

        // "spanish (latam)" → "spanish-latam.json".
        public static string FileName(string language) =>
            language.Replace(" (", "-").Replace(")", "").Replace(' ', '-') + ".json";
    }
}
