using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RegenerateFM.Desktop.FM24.Bridge;
using RegenerateFM.Desktop.FM24.Memory;
using RegenerateFM.Desktop.FM24.Setup;

namespace RegenerateFM.Desktop.FM24
{
    // regeneratefm-fm24's command line: the companion FM24 starts, its setup,
    // and double-clicked, setting FM24 up and asking for the key.
    public static class CompanionProgram
    {
        public const string Usage =
            "regeneratefm-fm24 [--preview-only] [--graphics-dir DIR] [--game-dir DIR] [--native-port PORT]\n" +
            "  The FM24 companion. FM24 starts it itself once RegenerateFM is set up; with --preview-only\n" +
            "  it prints the prompt instead of making a billed image request.\n" +
            "regeneratefm-fm24 " + Fm24Setup.Usage;

        private static string Here => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        // A companion FM24 started has no window: everything goes to
        // regeneratefm-fm24.log beside it (the previous run's in regeneratefm-fm24.log.old).
        private static void LogToFile()
        {
            string log = Path.Combine(Here, "regeneratefm-fm24.log");
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > 1_000_000)
                    File.Move(log, log + ".old", overwrite: true);
                var stream = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                Console.SetOut(stream);
                Console.SetError(stream);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
        }

        // A companion FM24 started stops when FM24 does.
        private static void ExitWithGame(int pid)
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr handle = OpenProcess(0x00100000, false, pid);  // SYNCHRONIZE
                if (handle != IntPtr.Zero)
                    WaitForSingleObject(handle, 0xFFFFFFFF);
            }
            else
            {
                while (kill(pid, 0) == 0 || Marshal.GetLastWin32Error() == 1)  // EPERM: it's still there
                    Thread.Sleep(2000);
            }
            Console.WriteLine("FM24 has closed, so the companion is stopping.");
            Environment.Exit(0);
        }

        private static string DefaultGraphicsDir(string game, Fm24Setup setup)
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support",
                                    "Sports Interactive", "Football Manager 2024", "graphics", "regeneratefm");
            if (OperatingSystem.IsWindows())
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return documents.Length > 0
                    ? Path.Combine(documents, "Sports Interactive", "Football Manager 2024", "graphics", "regeneratefm") : null;
            }
            // From source on Linux: inside FM24's Proton prefix.
            string userData = game != null ? setup.FindUserDataDir(game) : null;
            return userData != null ? Path.Combine(userData, "graphics", "regeneratefm") : null;
        }

        // From a companion FM24 started: builds the optional skin if the setup
        // couldn't, then on Windows and Proton checks the key, asking for one in
        // the key window if none is saved.
        private static void FirstStart(Companion companion)
        {
            if (companion.GameDir != null)
            {
                var setup = new Fm24Setup();
                string userData = setup.FindUserDataDir(companion.GameDir);
                if (userData != null && setup.BuildSkinWithPopups(companion.GameDir, Path.Combine(userData, "skins", "regeneratefm")))
                    Console.WriteLine("Built the optional RegenerateFM skin; choose it in FM24's Preferences > Interface after restarting FM24.");
            }
            // On macOS the key is asked for in the game when a face command needs it.
            if (companion.PreviewOnly || OperatingSystem.IsMacOS())
                return;
            companion.CheckSavedKey();
            if (companion.SavedKey() == null || companion.KeyRejected)
                companion.AskKeyInWindow(tellFm24: false);
        }

        private static void Quietly(Action action)
        {
            new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    Console.WriteLine(error.Message);
                }
            }) { IsBackground = true }.Start();
        }

        // The companion. Returns the exit code (or doesn't return while it runs).
        public static int Run(string[] args)
        {
            string graphicsDir = null, gameDir = null;
            int nativePort = NativeInstall.DefaultPort;
            int? startedBy = null;
            bool preview = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new SetupException(args[i] + " needs a value.\n" + Usage);
                switch (args[i])
                {
                    case "--graphics-dir": graphicsDir = Value(); break;
                    case "--game-dir": gameDir = Value(); break;
                    case "--native-port": nativePort = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--started-by-fm24": startedBy = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--preview-only": preview = true; break;
                    case "--help":
                    case "-h":
                        Console.WriteLine(Usage);
                        return 0;
                    default: throw new SetupException("Unknown option " + args[i] + ".\n" + Usage);
                }
            }
            if (nativePort < 1024 || nativePort > 65534)
                throw new SetupException("--native-port must be between 1024 and 65534");
            if (startedBy != null)
            {
                Console.WriteLine("Started by FM24 (process " + startedBy + ") at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ".");
                int pid = startedBy.Value;
                new Thread(() => ExitWithGame(pid)) { IsBackground = true }.Start();
            }
            var setup = new Fm24Setup();
            string game = gameDir != null ? Path.GetFullPath(gameDir) : Fm24Setup.FindGameDir();
            string token = game != null ? Fm24Setup.InstalledToken(game) : null;
            if (token == null)
                throw new SetupException("RegenerateFM isn't set up for FM24" + (game != null ? " in " + game : "") +
                                         " (no regeneratefm24.ini with its token). Run the RegenerateFM setup again.");
            string defaultGraphics = DefaultGraphicsDir(game, setup);
            graphicsDir = graphicsDir != null ? Path.GetFullPath(graphicsDir) : defaultGraphics;
            if (graphicsDir == null)
                throw new SetupException("Pass --graphics-dir: FM24's user-data graphics/regeneratefm folder.");
            if (graphicsDir == defaultGraphics && Directory.Exists(Path.GetDirectoryName(graphicsDir)))
                Directory.CreateDirectory(graphicsDir);
            var graphics = new DirectoryInfo(graphicsDir);
            if (!graphics.Exists || graphics.Name != "regeneratefm" || graphics.Parent?.Name != "graphics" ||
                graphics.Parent.Parent?.Name != "Football Manager 2024")
                throw new SetupException("--graphics-dir must be the existing Football Manager 2024/graphics/regeneratefm directory");
            var companion = new Companion
            {
                Token = token, GraphicsDir = graphicsDir, GameDir = game, PreviewOnly = preview, StartedByFm24 = startedBy,
                // The DLL takes dialog, progress and reload requests on the port after the bridge.
                ReloadPort = nativePort + 1,
            };
            if (!preview && !OperatingSystem.IsMacOS() && startedBy == null)
                companion.AskForKeyInTerminal();
            Listener listener;
            try
            {
                listener = new Listener(nativePort, token);
            }
            catch (InvalidOperationException error)
            {
                if (startedBy != null)
                {
                    Console.WriteLine("Another RegenerateFM companion is already running (" + error.Message + "); this one is stopping.");
                    return 0;
                }
                throw new SetupException(error.Message);
            }
            var squad = new SquadBatch(companion);
            listener.Generate = companion.Generate;
            listener.Squad = () => squad.Run();
            listener.Staff = () => squad.RunStaff();
            listener.HasFace = companion.FaceExists;
            listener.Resolve = companion.ResolveReference;
            listener.KeyReady = companion.EnsureKey;
            listener.Notify = text => Dll.Notify(companion.ReloadPort, token, text, message => Console.WriteLine("RegenerateFM: " + message));
            listener.Run();
            // Index the portraits now, so the first popup's face check is quick
            // (the game archive only if FM24 is already running).
            Quietly(() =>
            {
                try
                {
                    companion.FaceExists("1");
                }
                catch (ExistingPortrait.StillIndexingException)
                {
                }
            });
            if (OperatingSystem.IsMacOS())
                Quietly(companion.FindPersonTable);
            if (!preview && OperatingSystem.IsMacOS())
                Quietly(companion.CheckSavedKey);
            if (startedBy != null)
                Quietly(() => FirstStart(companion));
            Console.WriteLine("FM24 companion listening on 127.0.0.1:" + nativePort + " (" + (preview ? "preview" : "generation") + " mode)");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        // Double-clicked: sets FM24 up the first time (and after an update);
        // FM24 then starts it. FM24's folder, or null.
        private static string SetUpIfNeeded()
        {
            var setup = new Fm24Setup();
            string game, userData;
            try
            {
                (game, userData) = setup.ResolveDirs(null, null);
            }
            catch (SetupException error)
            {
                Console.WriteLine(error.Message);
                return null;
            }
            string companion = Fm24Setup.PackagedCompanion();
            if (!setup.IsSetUp(game, Path.Combine(userData, "skins", "regeneratefm"), companion))
            {
                Console.WriteLine("Setting up RegenerateFM for FM24 (one time only).");
                setup.Install(game, userData, companion);
            }
            return game;
        }

        // Whether this program has its own console window (double-clicked), which
        // closes when it exits.
        private static bool OwnWindow()
        {
            if (!OperatingSystem.IsWindows())
                return false;
            var processes = new uint[2];
            return GetConsoleProcessList(processes, 2) == 1;
        }

        // The packaged companion's entry point. FM24's DLL starts it with
        // --started-by-fm24 (no window; it logs to regeneratefm-fm24.log).
        // Double-clicked, it sets FM24 up and asks for the key, keeping its
        // window readable on exit.
        public static int Start(string[] args)
        {
            if (Array.IndexOf(args, "--started-by-fm24") >= 0)
            {
                LogToFile();
                try
                {
                    return Run(args);
                }
                catch (Exception error)
                {
                    Console.WriteLine(error is SetupException ? error.Message : error.ToString());
                    return 1;
                }
            }
            bool pause = OwnWindow();
            try
            {
                if (args.Length > 0 && args[0] == "setup")
                {
                    int code = new Fm24Setup().Main(args[1..]);
                    if (pause)
                        Terminal.Pause();
                    return code;
                }
                if (args.Length == 0)
                {
                    string game = SetUpIfNeeded();
                    if (game == null)
                    {
                        if (pause)
                            Terminal.Pause();
                        return 1;
                    }
                    if (!OperatingSystem.IsMacOS())
                        new Companion { ReloadPort = 0 }.AskForKeyInTerminal();
                    if (!new NativeInstall(OperatingSystem.IsMacOS()).Running(game))
                    {
                        Console.WriteLine("\nRegenerateFM is set up. It starts by itself whenever you start FM24, so there's nothing else to run.");
                        if (pause)
                            Terminal.Pause();
                        return 0;
                    }
                    // FM24 is already running; its companion normally is too.
                }
                return Run(args);
            }
            catch (SetupException error)
            {
                Console.Error.WriteLine(error.Message);
                if (pause)
                    Terminal.Pause();
                return 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (pause)
                    Terminal.Pause();
                return 1;
            }
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32")]
        private static extern uint GetConsoleProcessList(uint[] processes, uint count);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
