using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RegenerateFM.Desktop.FM24.Memory;

namespace RegenerateFM.Desktop.Epic
{
    // The Epic Games Launcher's installed games, and on the Mac the program it
    // starts for one, so Play in Epic starts RegenerateFM.
    //
    // Epic keeps a record per installed game (a JSON .item file) in its
    // Manifests folder: ~/Library/Application Support/Epic/EpicGamesLauncher/
    // Data/Manifests on macOS, %ProgramData%\Epic\EpicGamesLauncher\Data\
    // Manifests on Windows. InstallLocation is the game's folder and
    // LaunchExecutable the program in it that Epic starts, with the player's
    // Epic sign-in on its command line (FM24 quits without it). Epic has no
    // launch options, so RegenerateFM's launcher becomes that program. The
    // record is only changed while Epic is closed, as Epic may write back what
    // it read; the first change keeps a copy as <record>.regeneratefm-backup.
    // An Epic update or Verify can put the game's own program back, and the
    // setup then sets it again.
    public static class EpicLaunch
    {
        public const string BackupSuffix = ".regeneratefm-backup";

        // Replaced by tests: Epic's Manifests folder, and whether Epic is running.
        public static Func<string> ManifestsDir = DefaultManifestsDir;
        public static Func<bool> Running = EpicRunning;

        public static string DefaultManifestsDir()
        {
            string data = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            return Path.Combine(data, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        }

        // (record file, install folder, program) for every game Epic has installed.
        public static List<(string Record, string Folder, string Program)> Installed()
        {
            var found = new List<(string, string, string)>();
            string manifests = ManifestsDir();
            string[] records;
            try
            {
                records = Directory.Exists(manifests) ? Directory.GetFiles(manifests, "*.item") : Array.Empty<string>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return found;
            }
            Array.Sort(records, StringComparer.Ordinal);
            foreach (string record in records)
            {
                try
                {
                    var item = JsonNode.Parse(File.ReadAllText(record)) as JsonObject;
                    string folder = (string)item?["InstallLocation"], program = (string)item?["LaunchExecutable"];
                    if (!string.IsNullOrEmpty(folder))
                        found.Add((record, folder, program ?? ""));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException ||
                                              error is InvalidOperationException)
                {
                }
            }
            return found;
        }

        // The game's folder among Epic's installs: the one called ``folder``
        // (Epic's name for it, "FootballManager24") holding ``executable``, or null.
        public static string FindGameDir(string folder, string executable)
        {
            foreach (var (_, location, _) in Installed())
                if (string.Equals(Path.GetFileName(Trimmed(location)), folder, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(location, executable)))
                    return Trimmed(location);
            return null;
        }

        // Epic's record for the game in ``gameDir``, or null if Epic didn't install it.
        public static string Record(string gameDir)
        {
            foreach (var (record, location, _) in Installed())
                if (SameFolder(location, gameDir))
                    return record;
            return null;
        }

        // True if Epic's program for the game in ``gameDir`` isn't as RegenerateFM wants it yet.
        public static bool NeedsChange(string gameDir, string launcher, string program, bool remove = false)
        {
            foreach (var (_, location, current) in Installed())
                if (SameFolder(location, gameDir))
                    return current != (remove ? program : launcher);
            return false;
        }

        // Makes Epic start ``launcher`` (or, removing, the game's own
        // ``program``) for the game in ``gameDir``; a message for the player.
        // Both are relative to the game's folder.
        public static string Apply(string gameDir, string launcher, string program, bool remove = false)
        {
            string record = Record(gameDir);
            if (record == null)
                return "Epic didn't install the game in " + gameDir + ", so there is no Epic setting to change.";
            if (!NeedsChange(gameDir, launcher, program, remove))
                return remove ? "Epic already starts the game itself." : "Epic already starts the game with RegenerateFM.";
            if (Running())
                return remove ? "The Epic Games Launcher is running; quit it and run this again to take RegenerateFM out of Epic's setting."
                              : "The Epic Games Launcher is running, so RegenerateFM didn't change what Epic starts. Quit it and run the setup again.";
            try
            {
                var item = (JsonObject)JsonNode.Parse(File.ReadAllText(record));
                item["LaunchExecutable"] = remove ? program : launcher;
                string backup = record + BackupSuffix;
                if (!File.Exists(backup))
                    File.Copy(record, backup);
                AtomicFile.Write(record, new UTF8Encoding(false).GetBytes(item.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException ||
                                          error is InvalidCastException)
            {
                return "RegenerateFM couldn't change what Epic starts (" + error.Message + ").";
            }
            return remove ? "Epic starts the game itself again." : "Epic now starts the game with RegenerateFM: press Launch in Epic.";
        }

        private static string Trimmed(string folder) => folder.TrimEnd('/', '\\');

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Trimmed(Path.GetFullPath(a)), Trimmed(Path.GetFullPath(b)),
                                     OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException)
            {
                return false;
            }
        }

        // Epic's launcher, by its program's path: a Mac process name is cut short.
        public static bool EpicRunning()
        {
            string program = OperatingSystem.IsWindows() ? "\\EpicGamesLauncher.exe" : "/EpicGamesLauncher-Mac-Shipping";
            foreach (var (_, path) in Processes.Running())
                if (path.EndsWith(program, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
