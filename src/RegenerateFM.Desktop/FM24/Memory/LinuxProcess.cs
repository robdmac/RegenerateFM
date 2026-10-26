using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // FM24 under Proton, read from Linux itself (a companion run from source
    // on Linux; the packaged one runs inside the game's prefix and reads it as
    // WindowsProcess). /proc/<pid>/maps, process_vm_readv for small reads and
    // /proc/<pid>/mem for scans.
    public sealed class LinuxProcess : GameProcess
    {
        private const int MinRegion = 64;
        private SafeFileHandle _memory;
        private long? _imageBase;

        public LinuxProcess(int pid) : base(pid, Layout.Windows)
        {
        }

        private IEnumerable<string[]> Maps()
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines("/proc/" + Pid + "/maps");
            }
            catch (UnauthorizedAccessException error)
            {
                throw new IOException(error.Message, error);
            }
            foreach (string line in lines)
                yield return line.Split((char[])null, 6, StringSplitOptions.RemoveEmptyEntries);
        }

        public override long ImageBase
        {
            get
            {
                if (_imageBase == null)
                {
                    try
                    {
                        foreach (var fields in Maps())
                            if (fields.Length == 6 && fields[1].StartsWith("r") && fields[2] == "00000000" &&
                                fields[5].Replace('\\', '/').EndsWith("/football manager 2024/fm.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                _imageBase = long.Parse(fields[0].Split('-')[0], NumberStyles.HexNumber);
                                break;
                            }
                    }
                    catch (IOException error)
                    {
                        throw new InvalidDataException("Could not read process " + Pid + "'s memory map: " + error.Message);
                    }
                    if (_imageBase == null)
                        throw new InvalidDataException("This process has no mapped FM24 executable header.");
                }
                return _imageBase.Value;
            }
        }

        public override string Executable
        {
            get
            {
                try
                {
                    foreach (var fields in Maps())
                        if (fields.Length == 6 && fields[5].EndsWith("/football manager 2024/fm.exe", StringComparison.OrdinalIgnoreCase))
                            return fields[5];
                }
                catch (IOException)
                {
                }
                return null;
            }
        }

        public override IEnumerable<(long Start, long End)> WritableRegions()
        {
            var regions = new List<(long, long)>();
            foreach (var parts in Maps())
            {
                if (parts.Length < 5 || !parts[1].StartsWith("rw") || parts[4] != "0")
                    continue;
                if (parts.Length == 6 && parts[5].Length > 0 && !parts[5].StartsWith("["))
                    continue;
                var range = parts[0].Split('-');
                long start = long.Parse(range[0], NumberStyles.HexNumber), end = long.Parse(range[1], NumberStyles.HexNumber);
                if (end - start >= MinRegion)
                    regions.Add((start, end));
            }
            return regions;
        }

        public override unsafe int ReadInto(long address, Span<byte> buffer)
        {
            if (buffer.Length <= MaxRead)
            {
                fixed (byte* target = buffer)
                {
                    var local = new IoVec { Base = (IntPtr)target, Length = (IntPtr)buffer.Length };
                    var remote = new IoVec { Base = new IntPtr(address), Length = (IntPtr)buffer.Length };
                    long count = process_vm_readv(Pid, ref local, 1, ref remote, 1, 0).ToInt64();
                    if (count < 0)
                    {
                        int errno = Marshal.GetLastWin32Error();
                        // EPERM: Yama (kernel.yama.ptrace_scope 1, Ubuntu's default) lets a
                        // program read only its own children's memory.
                        throw new IOException(errno == 1
                            ? "Linux doesn't let this program read FM24's memory (kernel.yama.ptrace_scope). Use the companion " +
                              "FM24 starts inside Proton, or allow it for this session with sudo sysctl kernel.yama.ptrace_scope=0"
                            : "process_vm_readv failed (errno " + errno + ").");
                    }
                    return (int)count;
                }
            }
            try
            {
                _memory ??= File.OpenHandle("/proc/" + Pid + "/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return RandomAccess.Read(_memory, buffer, address);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new IOException(error.Message);
            }
        }

        public override void Dispose()
        {
            _memory?.Dispose();
        }

        // (process ID, the first word of its command line) for every process:
        // Wine shows the Windows path of the program it runs.
        public static IEnumerable<(int Pid, string Path)> Running()
        {
            var found = new List<(int, string)>();
            foreach (string entry in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out int pid))
                    continue;
                byte[] command;
                try
                {
                    command = File.ReadAllBytes(Path.Combine(entry, "cmdline"));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    continue;
                }
                int end = Array.IndexOf(command, (byte)0);
                if (command.Length > 0 && end != 0)
                    found.Add((pid, Encoding.UTF8.GetString(command, 0, end < 0 ? command.Length : end)));
            }
            return found;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoVec
        {
            public IntPtr Base;
            public IntPtr Length;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr process_vm_readv(int pid, ref IoVec local, ulong localCount, ref IoVec remote,
                                                       ulong remoteCount, ulong flags);
    }
}
