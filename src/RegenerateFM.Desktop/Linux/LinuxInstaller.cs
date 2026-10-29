using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RegenerateFM.Desktop.FM24.Memory;
using RegenerateFM.Desktop.FM24.Setup;
using RegenerateFM.Desktop.Steam;

namespace RegenerateFM.Desktop.Linux
{
    // Installs or removes RegenerateFM on Linux (Steam/Proton): FM26, the FM26
    // Demo and FM24. The release's install-linux.sh runs it. With Steam quit
    // and the games closed, it:
    //
    //   FM26, FM26 Demo  copies the release's FM26 folder (BepInEx and the
    //                    plugin) into the game folder, downloads the Unity
    //                    libraries BepInEx needs (UnityLibraries), and sets
    //                    the launch option that makes Proton load BepInEx's
    //                    winhttp.dll
    //   FM24             copies the packaged companion to
    //                    ~/.local/share/RegenerateFM/FM24, so the downloaded
    //                    release can be deleted, and sets FM24 up for it
    //
    // Faces are kept: they are in the games' user-data folders, inside their
    // Proton prefixes.
    public sealed class LinuxInstaller
    {
        public static readonly string[] AllGames = { "fm26", "fm26demo", "fm24" };
        public const string Plugin = "BepInEx/plugins/RegenerateFM";
        // What the FM26 folder adds beside fm.exe. Faces and settings are in
        // FM26's user-data folder, so removing these keeps them.
        public static readonly string[] Fm26Added = { "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version" };
        public const string Companion = "regeneratefm-fm24.exe";
        // What to expect from FM26's first start after installing (as the macOS installer says).
        public const string FirstStartNote = "The first time FM26 starts with RegenerateFM, it takes a few minutes longer (up to about 10 on a slower computer) while BepInEx prepares the game; a RegenerateFM window shows its progress. Let it finish. This happens once: later starts are as quick as before. It happens once more after an FM26 update.";
        public const string QuitSteam = "Steam is running. Quit Steam first (Steam > Exit, not just closing its window), then run " +
                                        "install-linux.sh again: Steam only keeps a game's launch option set while it is closed.";
        public const string Usage =
            "install-linux.sh [fm26] [fm26demo] [fm24] [--game-dir DIR] [--remove]\n\n" +
            "Sets up every game it finds in the Steam libraries, or the ones named; --remove uninstalls\n" +
            "RegenerateFM and its launch options (your faces are kept). --game-dir names the folder of the one game named.";

        public Action<string> Log = text => Console.WriteLine(text);
        public Action<string> Error = text => Console.Error.WriteLine(text);
        // Replaced by tests.
        public Func<IEnumerable<(int Pid, string Path)>> RunningExecutables = Processes.Running;
        public Action<string[]> Fm24Setup;

        public static string Name(string game) => SteamLaunch.Games[game].Folder;

        public static string FindGame(string game) => SteamLaunch.FindGameDir(game, "fm.exe");

        // The game in this folder is running (FM24 and FM26 both run fm.exe).
        private bool GameRunning(string folder)
        {
            string suffix = "/" + Path.GetFileName(folder) + "/fm.exe";
            return RunningExecutables().Any(entry => (entry.Path ?? "").Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        public static string CompanionDir()
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "RegenerateFM", "FM24");
        }

        // Copies source's files over target's, each replaced in one step; the number copied.
        public static int CopyTree(string source, string target)
        {
            int copied = 0;
            foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, path));
                AtomicFile.Write(destination, File.ReadAllBytes(path), (int)File.GetUnixFileMode(path));
                copied++;
            }
            return copied;
        }

        private static void Delete(string path)
        {
            var info = new FileInfo(path);
            if (Directory.Exists(path) && info.LinkTarget == null)
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path) || info.LinkTarget != null)
                File.Delete(path);
        }

        private static bool Installed(string game, string folder) =>
            game == "fm24" ? File.Exists(Path.Combine(folder, NativeInstall.Manifest))
                           : Directory.Exists(Path.Combine(folder, Plugin));

        private static List<string> InstallFm26(string folder, string release)
        {
            string payload = Path.Combine(release, "FM26");
            if (!File.Exists(Path.Combine(payload, Plugin, "RegenerateFM.dll")))
                throw new SetupException("The release's FM26 folder is missing: " + payload);
            var lines = new List<string> { "Copied " + CopyTree(payload, folder) + " files into " + folder + "." };
            string libraries = UnityLibraries.Fetch(folder);
            if (libraries != null)
                lines.Add(libraries);
            return lines;
        }

        // Removes what InstallFm26 added. Another mod's BepInEx plugins are kept,
        // with BepInEx, and then only RegenerateFM's plugin goes.
        private static List<string> RemoveFm26(string folder)
        {
            string plugins = Path.Combine(folder, "BepInEx", "plugins");
            var others = Directory.Exists(plugins)
                ? Directory.EnumerateFileSystemEntries(plugins).Select(Path.GetFileName)
                           .Where(name => name != "RegenerateFM" && !name.StartsWith("."))
                           .OrderBy(name => name, StringComparer.Ordinal).ToList()
                : new List<string>();
            if (others.Count > 0)
            {
                Delete(Path.Combine(folder, Plugin));
                return new List<string> { "Removed the RegenerateFM plugin. BepInEx was left in place because it has other plugins: " +
                                          string.Join(", ", others) + "." };
            }
            foreach (string added in Fm26Added)
                Delete(Path.Combine(folder, added));
            return new List<string> { "Removed RegenerateFM from " + Path.GetFileName(folder) + ". Your faces are kept in FM26's user-data folder." };
        }

        private void RunFm24Setup(string[] args)
        {
            if (Fm24Setup != null)
            {
                Fm24Setup(args);
                return;
            }
            new Fm24Setup { Log = Log }.Main(args);
        }

        private List<string> InstallFm24(string folder, string release)
        {
            string payload = Path.Combine(release, "FM24", Companion);
            if (!File.Exists(payload))
                throw new SetupException("The release's FM24 folder is missing: " + Path.GetDirectoryName(payload));
            string target = CompanionDir();
            string companion = Path.Combine(target, Companion);
            AtomicFile.Write(companion, File.ReadAllBytes(payload), AtomicFile.Executable);
            Log("Copied the companion to " + target + ".");
            RunFm24Setup(new[] { "--game-dir", folder, "--companion", companion });
            return new List<string>();
        }

        private List<string> RemoveFm24(string folder)
        {
            RunFm24Setup(new[] { "--game-dir", folder, "--remove" });
            string target = CompanionDir();
            if (!File.Exists(Path.Combine(target, Companion)))
                return new List<string>();
            // The companion and its log go; the key and settings are in FM24's prefix.
            foreach (string entry in new[] { Companion, "regeneratefm-fm24.log", "regeneratefm-fm24.log.old" })
                Delete(Path.Combine(target, entry));
            foreach (string directory in new[] { target, Path.GetDirectoryName(target) })
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(directory).Any())
                        break;
                    Directory.Delete(directory);
                }
                catch (IOException)
                {
                    break;
                }
            }
            return new List<string> { "Removed the companion from " + target + "." };
        }

        // Returns the exit code.
        public int Main(string[] args, string defaultRelease)
        {
            var games = new List<string>();
            string gameDir = null, release = defaultRelease;
            bool remove = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new SetupException(args[i] + " needs a value.\n\n" + Usage);
                switch (args[i])
                {
                    case "--game-dir": gameDir = Value(); break;
                    case "--release": release = Value(); break;
                    case "--remove": remove = true; break;
                    case "--help":
                    case "-h":
                        Log(Usage);
                        return 0;
                    default:
                        if (args[i].StartsWith("-") || !AllGames.Contains(args[i]))
                            throw new SetupException("Unknown game " + args[i] + ": name fm26, fm26demo or fm24.\n\n" + Usage);
                        games.Add(args[i]);
                        break;
                }
            }
            if (gameDir != null && games.Count != 1)
                throw new SetupException("--game-dir needs exactly one game: fm26, fm26demo or fm24.");
            release = Path.GetFullPath(release);

            var found = new Dictionary<string, string>();
            foreach (string game in games.Count > 0 ? games : AllGames.ToList())
            {
                string folder = gameDir != null ? SteamLaunch.Resolve(gameDir) : FindGame(game);
                if (folder != null && File.Exists(Path.Combine(folder, "fm.exe")))
                    found[game] = folder;
                else if (games.Count > 0)
                    throw new SetupException("Couldn't find " + Name(game) + " in your Steam libraries; pass --game-dir.");
            }
            if (found.Count == 0)
                throw new SetupException("Couldn't find FM26, the FM26 Demo or FM24 in your Steam libraries. Name the game and " +
                                         "pass --game-dir \"<its folder>\".");
            if (remove)
            {
                found = found.Where(entry => Installed(entry.Key, entry.Value)).ToDictionary(entry => entry.Key, entry => entry.Value);
                if (found.Count == 0)
                {
                    Log("RegenerateFM isn't installed in any game found.");
                    return 0;
                }
            }
            // Everything is checked before anything changes.
            var running = found.Where(entry => GameRunning(entry.Value)).Select(entry => Name(entry.Key)).ToList();
            if (running.Count > 0)
                throw new SetupException("Close " + string.Join(" and ", running) + ", then run install-linux.sh again.");
            if (SteamLaunch.Running() && found.Any(entry => SteamLaunch.NeedsChange(entry.Key, entry.Value, remove)))
                throw new SetupException(QuitSteam);

            var failed = new List<string>();
            foreach (var (game, folder) in found)
            {
                Log("\n" + Name(game) + ": " + folder);
                List<string> lines;
                try
                {
                    if (game == "fm24")
                        lines = remove ? RemoveFm24(folder) : InstallFm24(folder, release);
                    else
                    {
                        // Without BepInEx, Steam couldn't start the game with the launch option still set.
                        if (remove)
                            Log(SteamLaunch.Apply(game, folder, remove: true));
                        lines = remove ? RemoveFm26(folder) : InstallFm26(folder, release);
                        if (!remove)
                            lines.Add(SteamLaunch.Apply(game, folder));
                    }
                }
                catch (Exception error) when (error is SetupException || error is IOException || error is UnauthorizedAccessException)
                {
                    Error("RegenerateFM couldn't " + (remove ? "remove" : "install") + " it: " + error.Message);
                    failed.Add(Name(game));
                    continue;
                }
                if (lines.Count > 0)
                    Log(string.Join("\n", lines));
            }
            if (failed.Count > 0)
            {
                Error("\nNot done: " + string.Join(", ", failed) + ".");
                return 1;
            }
            if (!remove)
            {
                Log("\nStart Steam and press Play. The first time you choose a face command, RegenerateFM asks for your OpenAI key " +
                    "or a RegenerateFM sign-in.");
                if (found.Keys.Any(game => game != "fm24"))
                    Log(FirstStartNote);
            }
            return 0;
        }
    }
}
