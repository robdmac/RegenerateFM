using System;
using System.IO;
using RegenerateFM.Desktop.FM24;
using RegenerateFM.Desktop.FM24.Setup;
using RegenerateFM.Desktop.Linux;

namespace RegenerateFM.Desktop
{
    // RegenerateFM outside the games. Built as regeneratefm-fm24 (Windows and
    // macOS: the FM24 companion and its setup) and as regeneratefm (Linux: the
    // installer for every game, which also runs the FM24 companion from source).
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Windows' console writes in its OEM code page (850 or 437), which
            // has no €; in UTF-8 every message shows as written.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                }
                catch (IOException)
                {
                }
            }
            if (args.Length > 0 && args[0] == "install")
            {
                try
                {
                    // Beside install-linux.sh: the release's own folder.
                    string release = Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath));
                    return new LinuxInstaller().Main(args[1..], release);
                }
                catch (SetupException error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 2;
                }
            }
            // The Linux build is the installer; by itself it says how to run it.
            if (OperatingSystem.IsLinux() && args.Length == 0)
            {
                Console.WriteLine("Run install-linux.sh in the unpacked release, or: regeneratefm install --release <folder>\n\n" +
                                  LinuxInstaller.Usage);
                return 2;
            }
            if (args.Length >= 4 && args[0] == "launch-option")
                return LaunchOption(args);
            return CompanionProgram.Start(args);
        }

        // `launch-option <localconfig.vdf> <app ID> <prefix> [--remove]`: the
        // file's text with RegenerateFM's launch option set, or NONE when the
        // account doesn't have the game. The macOS installer's tests check its
        // own launch options against this.
        private static int LaunchOption(string[] args)
        {
            try
            {
                string text = File.ReadAllText(args[1], new System.Text.UTF8Encoding(false, true));
                string updated = Steam.SteamLaunch.SetLaunchOption(text, args[2], args[3], remove: args.Length > 4 && args[4] == "--remove");
                using var output = Console.OpenStandardOutput();
                output.Write(new System.Text.UTF8Encoding(false).GetBytes(updated ?? "NONE\n"));
                return 0;
            }
            catch (FormatException error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
    }
}
