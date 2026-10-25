using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RegenerateFM.Desktop.Steam
{
    // Steam's libraries, and RegenerateFM's launch option for a game, so Play
    // in Steam starts it.
    //
    // Steam keeps launch options per account in userdata/<account>/config/
    // localconfig.vdf, under UserLocalConfigStore > Software > Valve > Steam >
    // apps > <app ID> > LaunchOptions. Steam reads the file when it starts and
    // writes it back when it quits, so the file is only changed while Steam is
    // closed. Only accounts that have the game get the option, the rest of the
    // file is kept byte for byte, and the first change keeps a copy as
    // localconfig.vdf.regeneratefm-backup. A launch option the player already
    // has is kept: RegenerateFM's part goes in front.
    public static class SteamLaunch
    {
        public sealed class Game
        {
            public string App;
            public string Folder;
            public string Launcher;  // the macOS launch script beside the game
            public string Proton;    // what Proton needs to load RegenerateFM's library
        }

        public static readonly Dictionary<string, Game> Games = new Dictionary<string, Game>
        {
            ["fm24"] = new Game { App = "2252570", Folder = "Football Manager 2024", Launcher = "run_regeneratefm24.sh",
                                  Proton = "WINEDLLOVERRIDES=\"winmm=n,b\"" },
            ["fm26"] = new Game { App = "3551340", Folder = "Football Manager 26", Launcher = "run_regeneratefm.sh",
                                  Proton = "WINEDLLOVERRIDES=\"winhttp=n,b\"" },
            // The FM26 Demo is its own Steam app and folder, set up as FM26.
            ["fm26demo"] = new Game { App = "3551360", Folder = "Football Manager 26 Demo", Launcher = "run_regeneratefm.sh",
                                      Proton = "WINEDLLOVERRIDES=\"winhttp=n,b\"" },
        };
        private static readonly string[] AppsPath = { "userlocalconfigstore", "software", "valve", "steam", "apps" };
        public const string Command = "%command%";
        public const string BackupSuffix = ".regeneratefm-backup";

        // Replaced by tests: where Steam is, and whether it's running.
        public static Func<List<string>> Roots = DefaultRoots;
        public static Func<bool> Running = SteamRunning;

        public static List<string> DefaultRoots()
        {
            var roots = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var (hive, key, value) in new[]
                         {
                             (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                             (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                         })
                {
                    try
                    {
                        using var handle = hive.OpenSubKey(key);
                        if (handle?.GetValue(value) is string path)
                            roots.Add(path);
                    }
                    catch (Exception error) when (error is System.Security.SecurityException || error is UnauthorizedAccessException ||
                                                  error is IOException)
                    {
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
                roots.Add(Path.Combine(Home, "Library", "Application Support", "Steam"));
            else
                roots.AddRange(new[]
                {
                    Path.Combine(Home, ".steam", "steam"), Path.Combine(Home, ".local", "share", "Steam"),
                    Path.Combine(Home, ".steam", "debian-installation"),
                    Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                    Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam"),
                });
            return roots;
        }

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A path with its symbolic links followed, as Python's Path.resolve().
        public static string Resolve(string path)
        {
            var full = Path.GetFullPath(path);
            var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = OperatingSystem.IsWindows() ? "" : Path.DirectorySeparatorChar.ToString();
            for (int i = 0; i < parts.Length; i++)
            {
                current = i == 0 && OperatingSystem.IsWindows() ? parts[0] + Path.DirectorySeparatorChar : Path.Combine(current, parts[i]);
                try
                {
                    var info = new DirectoryInfo(current);
                    var target = info.LinkTarget != null ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
                    if (target != null)
                        current = Resolve(target.FullName);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
            return current;
        }

        // Every Steam library folder (the ones holding steamapps), without duplicates.
        public static List<string> Libraries()
        {
            var found = new List<string>();
            foreach (string root in Roots())
            {
                var candidates = new List<string> { root };
                try
                {
                    string text = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
                    foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                        candidates.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
                foreach (string library in candidates)
                {
                    string resolved;
                    try
                    {
                        resolved = Resolve(library);
                    }
                    catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (Directory.Exists(Path.Combine(resolved, "steamapps")) && !found.Contains(resolved))
                        found.Add(resolved);
                }
            }
            return found;
        }

        // The game's folder in the Steam libraries, or null.
        public static string FindGameDir(string game, string executable = null)
        {
            foreach (string library in Libraries())
            {
                string folder = Path.Combine(library, "steamapps", "common", Games[game].Folder);
                if (executable != null ? File.Exists(Path.Combine(folder, executable)) : Directory.Exists(folder))
                    return folder;
            }
            return null;
        }

        public static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

        // RegenerateFM's part of the launch option, or null where none is needed
        // (Windows). ``platform`` is "windows", "macos" or "linux" (this one).
        public static string Option(string game, string gameDir, string platform = null)
        {
            platform ??= Platform;
            if (platform == "windows")
                return null;
            if (platform == "macos")
                return "\"" + Path.Combine(gameDir, Games[game].Launcher) + "\"";
            return Games[game].Proton;
        }

        // The player's launch option with RegenerateFM's prefix in front of %command%.
        public static string Combined(string existing, string prefix)
        {
            existing = existing.Trim();
            if (existing.Contains(prefix))
                return existing;
            int at = existing.IndexOf(Command, StringComparison.Ordinal);
            if (at >= 0)
                return existing.Substring(0, at) + prefix + " " + Command + existing.Substring(at + Command.Length);
            return (prefix + " " + Command + " " + existing).Trim();
        }

        // The launch option without RegenerateFM's prefix; empty if nothing else was set.
        public static string Without(string existing, string prefix)
        {
            string option = string.Join(" ", existing.Replace(prefix, "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return option == Command ? "" : option;
        }

        // (kind, start, end, value) for each quoted string and brace; // comments are skipped.
        private static IEnumerable<(char Kind, int Start, int End, string Value)> Tokens(string text)
        {
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '"')
                {
                    int end = index + 1;
                    var value = new StringBuilder();
                    while (end < text.Length && text[end] != '"')
                    {
                        if (text[end] == '\\' && end + 1 < text.Length)
                            end++;
                        value.Append(text[end]);
                        end++;
                    }
                    if (end >= text.Length)
                        throw new FormatException("Unterminated string in localconfig.vdf.");
                    yield return ('s', index, end + 1, value.ToString());
                    index = end + 1;
                }
                else if (c == '{' || c == '}')
                {
                    yield return (c, index, index + 1, c.ToString());
                    index++;
                }
                else if (string.CompareOrdinal(text, index, "//", 0, 2) == 0)
                {
                    int newline = text.IndexOf('\n', index);
                    index = newline < 0 ? text.Length : newline;
                }
                else
                    index++;
            }
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // (open brace end, close brace start, LaunchOptions value token or null)
        // for the app's block under apps, or null if this account doesn't have the game.
        internal static (int Opened, int Closing, (int Start, int End, string Value)? Value)? AppBlock(string text, string appId)
        {
            var path = new List<string>();
            string key = null;
            int? block = null;
            (int, int, string)? value = null;
            foreach (var (kind, start, end, token) in Tokens(text))
            {
                if (kind == 's')
                {
                    if (key == null)
                        key = token;
                    else
                    {
                        if (block != null && path.Count == AppsPath.Length + 1 && key.ToLowerInvariant() == "launchoptions")
                            value = (start, end, token);
                        key = null;
                    }
                }
                else if (kind == '{')
                {
                    if (key == null)
                        throw new FormatException("localconfig.vdf has a block without a name.");
                    path.Add(key.ToLowerInvariant());
                    if (path.Count == AppsPath.Length + 1 && path.Take(AppsPath.Length).SequenceEqual(AppsPath) && path[^1] == appId)
                        block = end;
                    key = null;
                }
                else
                {
                    if (path.Count == 0)
                        throw new FormatException("localconfig.vdf has an unmatched brace.");
                    if (block != null && path.Count == AppsPath.Length + 1)
                        return (block.Value, start, value);
                    path.RemoveAt(path.Count - 1);
                }
            }
            return null;
        }

        // The file's text with RegenerateFM's launch option set (or removed), or
        // null when this account doesn't have the game.
        public static string SetLaunchOption(string text, string appId, string prefix, bool remove = false)
        {
            var found = AppBlock(text, appId);
            if (found == null)
                return null;
            var (opened, closing, value) = found.Value;
            string existing = value?.Value ?? "";
            string option = remove ? Without(existing, prefix) : Combined(existing, prefix);
            if (value != null)
                return text.Substring(0, value.Value.Start) + "\"" + Escape(option) + "\"" + text.Substring(value.Value.End);
            if (option.Length == 0)
                return text;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, closing - 1)) + 1;
            if (lineStart > closing)
                lineStart = 0;
            string before = text.Substring(lineStart, closing - lineStart);
            string indent = before.Trim().Length == 0 ? before : "";
            string entry = indent + "\t\"LaunchOptions\"\t\t\"" + Escape(option) + "\"\n";
            return text.Substring(0, lineStart) + entry + text.Substring(lineStart);
        }

        public static bool SteamRunning()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return Process.GetProcessesByName("steam").Length > 0;
                string name = OperatingSystem.IsMacOS() ? "steam_osx" : "steam";
                using var pgrep = Process.Start(new ProcessStartInfo("pgrep", "-x " + name)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                pgrep.WaitForExit();
                return pgrep.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static IEnumerable<string> Configs(string root)
        {
            string userdata = Path.Combine(root, "userdata");
            string[] accounts;
            try
            {
                accounts = Directory.Exists(userdata) ? Directory.GetDirectories(userdata) : Array.Empty<string>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
            return accounts.OrderBy(name => name, StringComparer.Ordinal)
                           .Select(account => Path.Combine(account, "config", "localconfig.vdf")).Where(File.Exists);
        }

        // Changes every account's localconfig.vdf that has the game; the files changed.
        public static List<string> UpdateAccounts(string appId, string prefix, bool remove = false)
        {
            var changed = new List<string>();
            foreach (string root in Roots())
                foreach (string config in Configs(root))
                {
                    string text = File.ReadAllText(config, new UTF8Encoding(false, true));
                    string updated = SetLaunchOption(text, appId, prefix, remove);
                    if (updated == null || updated == text)
                        continue;
                    string backup = config + BackupSuffix;
                    if (!File.Exists(backup))
                        File.Copy(config, backup);
                    AtomicFile.Write(config, new UTF8Encoding(false).GetBytes(updated));
                    changed.Add(config);
                }
            return changed;
        }

        // True if some account's launch option for the game isn't as RegenerateFM wants it yet.
        public static bool NeedsChange(string game, string gameDir, bool remove = false)
        {
            string prefix = Option(game, gameDir);
            if (prefix == null)
                return false;
            foreach (string root in Roots())
                foreach (string config in Configs(root))
                {
                    try
                    {
                        string text = File.ReadAllText(config, new UTF8Encoding(false, true));
                        string updated = SetLaunchOption(text, Games[game].App, prefix, remove);
                        if (updated != null && updated != text)
                            return true;
                    }
                    catch (Exception error) when (error is IOException || error is FormatException || error is DecoderFallbackException ||
                                                  error is UnauthorizedAccessException)
                    {
                        return true;
                    }
                }
            return false;
        }

        // Sets or removes RegenerateFM's launch option; a message for the player.
        public static string Apply(string game, string gameDir, bool remove = false)
        {
            string prefix = Option(game, gameDir);
            if (prefix == null)
                return "On Windows RegenerateFM needs no Steam launch option.";
            string manual = "set it yourself in Steam (Properties > General > Launch Options): " + prefix + " " + Command;
            string folder = Games[game].Folder;
            if (!NeedsChange(game, gameDir, remove))
                return remove ? "No Steam account had RegenerateFM's launch option."
                              : folder + "'s Steam launch option is already set for RegenerateFM.";
            if (Running())
                return remove ? "Steam is running; quit it and run this again to remove RegenerateFM's launch option."
                              : "Steam is running, so RegenerateFM didn't change its launch option. Quit Steam and run the setup again, or " + manual;
            List<string> changed;
            try
            {
                changed = UpdateAccounts(Games[game].App, prefix, remove);
            }
            catch (Exception error) when (error is IOException || error is FormatException || error is DecoderFallbackException ||
                                          error is UnauthorizedAccessException)
            {
                return "RegenerateFM couldn't change Steam's launch option (" + error.Message + "); " + manual;
            }
            if (changed.Count == 0)
                return remove ? "No Steam account had RegenerateFM's launch option."
                              : "No Steam account on this computer had the game's settings to change; " + manual;
            return (remove ? "Removed RegenerateFM from" : "Set") + " the Steam launch option for " + folder + " (" + changed.Count + " account(s)).";
        }
    }
}
