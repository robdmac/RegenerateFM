using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM.Desktop.FM24
{
    // FM24's interface language ("german", "english"), from its preferences
    // file current_language.xml: on macOS in ~/Library/Application Support/
    // Sports Interactive/Football Manager 2024/Preferences/version 1/. On
    // Windows and Proton it is looked for under %LOCALAPPDATA% and Documents'
    // Sports Interactive\Football Manager 2024\Preferences\version *\ (the
    // newest); those paths are unverified. Read again only when the file changes.
    public static class Fm24Language
    {
        private const string FileName = "current_language.xml";
        private static readonly object Gate = new object();
        private static string _path;
        private static DateTime _written;
        private static string _language;

        // FM24's language name, or null if its preferences can't be found or read.
        public static string Read()
        {
            lock (Gate)
            {
                try
                {
                    if (_path == null || !File.Exists(_path))
                    {
                        _path = Find();
                        _language = null;
                    }
                    if (_path == null)
                        return null;
                    var written = File.GetLastWriteTimeUtc(_path);
                    if (written == _written && _language != null)
                        return _language;
                    var record = XDocument.Load(_path);
                    string value = record.Descendants().Where(element => (string)element.Attribute("id") == "value")
                                         .Select(element => (string)element.Attribute("value")).FirstOrDefault();
                    _language = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    _written = written;
                    return _language;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is XmlException)
                {
                    return null;
                }
            }
        }

        // Sets RegenerateFM's text to FM24's language, English when unknown.
        public static void Apply() => Text.Language = Read() ?? "english";

        private static string Find()
        {
            var roots = OperatingSystem.IsMacOS()
                ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support") }
                : new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                          Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            return roots.Where(root => root.Length > 0)
                        .Select(root => Child(Child(Child(root, "Sports Interactive"), "Football Manager 2024"), "Preferences"))
                        .Where(Directory.Exists)
                        .SelectMany(preferences => Directory.EnumerateDirectories(preferences)
                            .Where(folder => Path.GetFileName(folder).StartsWith("version ", StringComparison.OrdinalIgnoreCase)))
                        .Select(folder => Path.Combine(folder, FileName))
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
        }

        // ``parent``'s folder called ``name``, whatever its case.
        private static string Child(string parent, string name)
        {
            string exact = Path.Combine(parent, name);
            if (Directory.Exists(exact) || !Directory.Exists(parent))
                return exact;
            return Directory.EnumerateDirectories(parent)
                       .FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), name, StringComparison.OrdinalIgnoreCase)) ?? exact;
        }
    }
}
