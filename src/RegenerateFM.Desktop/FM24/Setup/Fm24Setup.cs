using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RegenerateFM.Desktop.Steam;

namespace RegenerateFM.Desktop.FM24.Setup
{
    // RegenerateFM's one-step setup for FM24: finds the game, builds the
    // optional RegenerateFM skin from FM24's own panels and installs the native
    // library (the DLL on Windows and Proton, libregeneratefm24.dylib and
    // run_regeneratefm24.sh on macOS). The packaged companion runs it itself
    // the first time it starts, and the DLL then starts that companion with
    // FM24. On Linux the release's install-linux.sh runs it for the packaged
    // companion, which runs inside FM24's Proton prefix.
    public sealed class Fm24Setup
    {
        public const string AppId = "2252570";
        public const string GameFolder = "Football Manager 2024";
        // The game's install folder: Steam's (as the user-data folder), then Epic's.
        public static readonly string[] GameFolders = { GameFolder, "FootballManager24" };
        public static readonly Dictionary<string, string> Panels = new Dictionary<string, string>
        {
            ["player"] = "/panels/player/player overview panel.xml",
            ["staff"] = "/panels/non_player/non player profile.xml",
        };
        public const string QuitSteam = "Steam is running. Quit Steam first (Steam > Exit, not just closing its window), then run the " +
                                        "RegenerateFM setup again: Steam only keeps a game's launch option set while it is closed.";

        public Fm24Setup(NativeInstall native = null)
        {
            Native = native ?? new NativeInstall(OperatingSystem.IsMacOS());
        }

        public NativeInstall Native { get; }
        public Action<string> Log = text => Console.WriteLine(text);
        // Replaced by tests: the library this build carries, and the skin builder.
        public Func<byte[]> BundledLibrary;
        public Action<string, string> BuildSkinFromGame = BuildSkin;
        // Whether this is a Linux host setting up the companion inside Proton.
        public bool Proton = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

        public byte[] Library() => BundledLibrary != null ? BundledLibrary() : Resources.Bytes(Native.Library);

        // This packaged companion, which FM24 (on macOS run_regeneratefm24.sh) starts.
        public static string PackagedCompanion() =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? Environment.ProcessPath : null;

        // The companion's path as FM24 sees it: as is on Windows and macOS, under
        // Wine's Z: drive on Linux.
        public string WindowsPath(string companion) => Proton ? "Z:" + companion.Replace('/', '\\') : companion;

        public static string FindGameDir() => SteamLaunch.FindGameDir("fm24", OperatingSystem.IsMacOS() ? "fm.app/Contents/MacOS/fm" : "fm.exe");

        // FM24's user-data folder (the one with skins and graphics), not the game folder.
        public string FindUserDataDir(string game)
        {
            string documents;
            if (OperatingSystem.IsWindows())
                documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            else if (OperatingSystem.IsMacOS())
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            else
                // Steam/Proton keeps it in the game's Wine prefix beside the library's common folder.
                documents = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(game).TrimEnd('/'))),
                                         "compatdata", AppId, "pfx", "drive_c", "users", "steamuser", "Documents");
            if (string.IsNullOrEmpty(documents))
                return null;
            string userData = Path.Combine(documents, "Sports Interactive", GameFolder);
            return Directory.Exists(userData) ? userData : null;
        }

        // The RegenerateFM skin, made from the panels in FM24's own panels.fmf.
        public static void BuildSkin(string game, string skinDir)
        {
            string archive = Path.Combine(game, "data", "game", "panels.fmf");
            var index = GameArchive.ReadIndex(archive);
            string scratch = Path.Combine(Path.GetTempPath(), "regeneratefm-skin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var sources = new Dictionary<string, string>();
                foreach (var (kind, name) in Panels)
                {
                    if (!index.TryGetValue(name.ToLowerInvariant(), out var entry))
                        throw new InvalidDataException("FM24's " + Path.GetFileName(archive) + " has no " + name + "; is this FM24 24.4.2?");
                    sources[kind] = Path.Combine(scratch, kind + ".xml");
                    File.WriteAllBytes(sources[kind], GameArchive.ReadFile(archive, entry));
                }
                Skin.AddPlayerPanel(sources["player"], skinDir);
                Skin.AddStaffPanel(sources["staff"], skinDir);
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }

        // From the companion FM24 started: the RegenerateFM skin and its portrait
        // popups, if the setup couldn't build it. The installer adopts these
        // panels the next time it runs. True if it was built now.
        public bool BuildSkinWithPopups(string game, string skinDir)
        {
            if (File.Exists(Path.Combine(skinDir, "skin_config.xml")))
                return false;
            BuildSkinFromGame(game, skinDir);
            foreach (var (kind, name) in NativeInstall.PanelFiles)
            {
                string panel = Path.Combine(skinDir, name);
                File.WriteAllText(panel, Skin.ModifyPanel(Skin.ReadText(panel), kind), new System.Text.UTF8Encoding(false));
            }
            return true;
        }

        // The library's settings (regeneratefm24.ini), if RegenerateFM was installed before.
        public static Dictionary<string, string> InstalledConfig(string game)
        {
            try
            {
                return NativeInstall.ReadConfig(Path.Combine(game, NativeInstall.Config));
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException)
            {
                return new Dictionary<string, string>();
            }
        }

        // The token the library and companion share, if RegenerateFM was installed before.
        public static string InstalledToken(string game)
        {
            InstalledConfig(game).TryGetValue("token", out string token);
            return token != null && NativeInstall.TokenPattern.IsMatch(token) ? token : null;
        }

        public static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // True when this library (and on macOS its launcher), the skin, a token
        // and the companion FM24 starts are installed.
        public bool IsSetUp(string game, string skinDir, string companion = null)
        {
            try
            {
                var library = Library();
                if (library == null)
                    return false;
                var metadata = NativeInstall.LoadManifest(Path.Combine(game, NativeInstall.Manifest));
                string bundled = NativeInstall.Digest(library);
                var config = InstalledConfig(game);
                config.TryGetValue("companion", out string installedCompanion);
                return metadata != null && File.Exists(Path.Combine(skinDir, "skin_config.xml")) &&
                       NativeInstall.Digest(Path.Combine(game, Native.Library)) == bundled &&
                       bundled == metadata["dllSha256"].GetValue<string>() &&
                       (companion == null || installedCompanion == WindowsPath(companion)) &&
                       InstalledToken(game) != null &&
                       (Native.Launcher == null || NativeInstall.Digest(Path.Combine(game, Native.Launcher)) ==
                                                   NativeInstall.Digest(Resources.Bytes(Native.Launcher)));
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is System.Text.Json.JsonException ||
                                          error is InvalidOperationException ||
                                          error is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // On Linux and macOS, FM24's launch option can only be set with Steam closed.
        public void CheckSteamClosed(string game, bool remove = false)
        {
            if (!OperatingSystem.IsWindows() && SteamLaunch.Running() && SteamLaunch.NeedsChange("fm24", game, remove))
                throw new SetupException(QuitSteam);
        }

        // Steam's launch option for FM24 (macOS and Proton), so Play starts RegenerateFM.
        private void LaunchOption(string game, bool remove = false)
        {
            if (!OperatingSystem.IsWindows())
                Log(SteamLaunch.Apply("fm24", game, remove));
        }

        public void Install(string game, string userData, string companion = null, bool launchOption = true)
        {
            string skinDir = Path.Combine(userData, "skins", "regeneratefm");
            if (launchOption)
                CheckSteamClosed(game);
            if (IsSetUp(game, skinDir, companion))
            {
                Log("RegenerateFM is already set up for FM24.");
                if (launchOption)
                    LaunchOption(game);
                return;
            }
            if (Native.Running(game))
                throw new SetupException("Close FM24, then run the RegenerateFM setup again.");
            var library = Library();
            if (library == null)
                throw new SetupException("This RegenerateFM build doesn't carry FM24's " + Native.Library + "; build it with native/fm24/" +
                                         (Native.Mac ? "build_macos.py" : "build.py") + " and build RegenerateFM again.");
            // A companion in a new folder (an update, say) keeps the token FM24 already has.
            string token = InstalledToken(game) ?? NewToken();
            if (!File.Exists(Path.Combine(skinDir, "skin_config.xml")))
            {
                try
                {
                    BuildSkinFromGame(game, skinDir);
                    Log("Built the RegenerateFM skin in " + skinDir);
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException)
                {
                    // The companion FM24 starts tries again, which helps only if the problem has gone.
                    Log("The optional RegenerateFM skin couldn't be built (" + error.Message + "). The face commands in a " +
                        "profile's Overview tab menu still work; for them on the portrait too, fix that and run the setup again.");
                }
            }
            Directory.CreateDirectory(Path.Combine(userData, "graphics", "regeneratefm"));
            Native.Log = Log;
            Native.Install(game, skinDir, library, token, companion != null ? WindowsPath(companion) : null);
            if (launchOption)
                LaunchOption(game);
            Log("\nIn FM24, Generate new face and Update face are in a profile's Overview tab menu, with any skin.\n" +
                "For them on the portrait too, choose the \"RegenerateFM\" skin in Preferences > Interface.\n");
        }

        public void Remove(string game, string userData, bool launchOption = true)
        {
            if (launchOption)
                CheckSteamClosed(game, remove: true);
            Native.Log = Log;
            Native.Remove(game, Path.Combine(userData, "skins", "regeneratefm"));
            if (launchOption)
                LaunchOption(game, remove: true);
        }

        public (string Game, string UserData) ResolveDirs(string gameDir, string userDataDir)
        {
            string game = gameDir != null ? SteamLaunch.Resolve(gameDir) : FindGameDir();
            if (game == null || !File.Exists(Path.Combine(game, Native.Executable)))
                throw new SetupException("RegenerateFM couldn't find FM24 in your Steam libraries. Run the setup again with " +
                                         "--game-dir \"<path to Football Manager 2024>\".");
            string userData = userDataDir != null ? SteamLaunch.Resolve(userDataDir) : FindUserDataDir(game);
            if (userData == null || !Directory.Exists(userData))
            {
                string where = Native.Mac ? "~/Library/Application Support/Sports Interactive/Football Manager 2024"
                                          : "Documents\\Sports Interactive\\Football Manager 2024";
                throw new SetupException("RegenerateFM couldn't find FM24's user-data folder (" + where + "). " +
                                         "Start FM24 once to create it, or pass --user-data-dir.");
            }
            return (game, userData);
        }

        public const string Usage =
            "setup [--game-dir DIR] [--user-data-dir DIR] [--companion EXE] [--remove] [--no-launch-option]\n" +
            "  Sets RegenerateFM up for FM24 (or removes it), with FM24 closed.";

        // `regeneratefm-fm24 setup …`. Returns the exit code.
        public int Main(string[] args)
        {
            string gameDir = null, userDataDir = null, companion = null;
            bool remove = false, noLaunchOption = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new SetupException(args[i] + " needs a value.\n" + Usage);
                switch (args[i])
                {
                    case "--game-dir": gameDir = Value(); break;
                    case "--user-data-dir": userDataDir = Value(); break;
                    case "--companion": companion = Value(); break;
                    case "--remove": remove = true; break;
                    case "--no-launch-option": noLaunchOption = true; break;
                    default: throw new SetupException("Unknown option " + args[i] + ".\n" + Usage);
                }
            }
            companion = companion != null ? Path.GetFullPath(companion) : PackagedCompanion();
            if (companion != null && !File.Exists(companion))
                throw new SetupException("There is no RegenerateFM companion at " + companion + ".");
            var (game, userData) = ResolveDirs(gameDir, userDataDir);
            Log("FM24: " + game + "\nFM24 user data: " + userData);
            if (remove)
                Remove(game, userData, launchOption: !noLaunchOption);
            else
                Install(game, userData, companion, launchOption: !noLaunchOption);
            return 0;
        }
    }
}
