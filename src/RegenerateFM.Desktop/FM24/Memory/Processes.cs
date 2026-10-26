using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // The running programs, and FM24 among them.
    public static class Processes
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<int, (string Started, GameProcess Game)> Open_ = new Dictionary<int, (string, GameProcess)>();

        // The game's layout on this system: the Mac build's on macOS, fm.exe's elsewhere.
        public static Layout GameLayout => OperatingSystem.IsMacOS() ? Layout.MacOS : Layout.Windows;

        // Replaced by tests: the process list and opening a game.
        internal static Func<IEnumerable<(int Pid, string Path)>> RunningSource;
        internal static Func<int, GameProcess> Opener;

        // (process ID, executable path) for every process this user can see.
        public static IEnumerable<(int Pid, string Path)> Running()
        {
            if (RunningSource != null)
                return RunningSource();
            if (OperatingSystem.IsWindows())
                return WindowsProcess.Running();
            if (OperatingSystem.IsMacOS())
                return MacProcess.Running();
            return LinuxProcess.Running();
        }

        // FM24's executable: fm.exe in "Football Manager 2024" (not FM26's, also
        // fm.exe), or fm.app's on the Mac.
        public static bool IsFm24(string executable, string folder = "Football Manager 2024")
        {
            string path = (executable ?? "").Replace('\\', '/');
            string suffix = "/" + folder + (OperatingSystem.IsMacOS() ? "/fm.app/Contents/MacOS/fm" : "/fm.exe");
            return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        // The game in process ``pid``, the same object while that process runs,
        // so what it has found is kept (Wine reuses process IDs, so the
        // process's start time is checked too).
        public static GameProcess Open(int pid)
        {
            if (Opener != null)
                return Opener(pid);
            string started = StartTime(pid);
            lock (Lock)
            {
                if (Open_.TryGetValue(pid, out var known) && known.Started == started)
                    return known.Game;
                // Forgotten first, so a failed reopen doesn't close the old handle twice.
                Open_.Remove(pid);
                known.Game?.Dispose();
                GameProcess game = OperatingSystem.IsWindows() ? new WindowsProcess(pid)
                                 : OperatingSystem.IsMacOS() ? new MacProcess(pid)
                                 : new LinuxProcess(pid);
                Open_[pid] = (started, game);
                return game;
            }
        }

        private static string StartTime(int pid)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    // Field 22 of /proc/<pid>/stat, after the command's parenthesised name.
                    string stat = File.ReadAllText("/proc/" + pid + "/stat");
                    var fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                    return fields[19];
                }
                if (OperatingSystem.IsWindows())
                {
                    IntPtr handle = OpenProcess(0x1000, false, pid);  // PROCESS_QUERY_LIMITED_INFORMATION
                    if (handle == IntPtr.Zero)
                        return "";
                    try
                    {
                        return GetProcessTimes(handle, out long created, out _, out _, out _) ? created.ToString() : "";
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
            }
            return "";
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    }
}
