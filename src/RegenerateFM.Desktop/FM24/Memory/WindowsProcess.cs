using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RegenerateFM.Desktop.FM24.Memory
{
    // FM24 on Windows, and under Proton, where the companion runs in the
    // game's Wine prefix: ReadProcessMemory and VirtualQueryEx with a handle
    // opened for querying and reading only.
    public sealed class WindowsProcess : GameProcess
    {
        private const int QueryInformation = 0x0400, QueryLimitedInformation = 0x1000, VmRead = 0x0010;
        private const int MemCommit = 0x1000, MemPrivate = 0x20000, PageReadWrite = 0x04, PageGuard = 0x100;
        private const int MinRegion = 64;
        private readonly IntPtr _handle;
        private long? _imageBase;
        private string _executable;

        public WindowsProcess(int pid) : base(pid, Layout.Windows)
        {
            _handle = OpenProcess(QueryInformation | VmRead, false, pid);
            if (_handle == IntPtr.Zero)
                throw new IOException("Opening process " + pid + " failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        public override long ImageBase
        {
            get
            {
                if (_imageBase == null)
                {
                    var (module, path) = MainModule();
                    if (!Processes.IsFm24(path))
                        throw new InvalidDataException("This process has no mapped FM24 executable header.");
                    _imageBase = module;
                    _executable = path;
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
                    _ = ImageBase;
                    return _executable;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }
        }

        private (long, string) MainModule()
        {
            var modules = new IntPtr[1];
            if (!EnumProcessModulesEx(_handle, modules, IntPtr.Size, out _, 3) || modules[0] == IntPtr.Zero)
                throw new InvalidDataException("Could not read the FM24 executable module: " +
                                               new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var name = new StringBuilder(1024);
            if (GetModuleFileNameExW(_handle, modules[0], name, name.Capacity) == 0)
                throw new InvalidDataException("Could not read the FM24 executable module: " +
                                               new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return (modules[0].ToInt64(), name.ToString());
        }

        public override unsafe int ReadInto(long address, Span<byte> buffer)
        {
            fixed (byte* target = buffer)
            {
                bool ok = ReadProcessMemory(_handle, new IntPtr(address), (IntPtr)target, (IntPtr)buffer.Length, out IntPtr copied);
                if (!ok && copied == IntPtr.Zero)
                    throw new IOException("ReadProcessMemory failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return (int)copied;
            }
        }

        public override IEnumerable<(long Start, long End)> WritableRegions()
        {
            long address = 0;
            int size = Marshal.SizeOf<MemoryInfo>();
            while (VirtualQueryEx(_handle, new IntPtr(address), out var info, (IntPtr)size) != IntPtr.Zero)
            {
                long start = info.BaseAddress.ToInt64();
                long end = start + info.RegionSize.ToInt64();
                if (info.State == MemCommit && info.Type == MemPrivate && (info.Protect & 0xFF) == PageReadWrite &&
                    (info.Protect & PageGuard) == 0 && info.RegionSize.ToInt64() >= MinRegion)
                    yield return (start, end);
                if (end <= address)
                    break;
                address = end;
            }
        }

        public override void Dispose()
        {
            CloseHandle(_handle);
        }

        // (process ID, executable path) for every process this user can query.
        public static IEnumerable<(int Pid, string Path)> Running()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);  // TH32CS_SNAPPROCESS
            if (snapshot == new IntPtr(-1))
                throw new IOException("CreateToolhelp32Snapshot failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var found = new List<(int, string)>();
            try
            {
                var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
                for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    string path = entry.ExeFile;
                    IntPtr process = OpenProcess(QueryLimitedInformation, false, entry.ProcessId);
                    if (process != IntPtr.Zero)
                    {
                        var buffer = new StringBuilder(1024);
                        int length = buffer.Capacity;
                        if (QueryFullProcessImageNameW(process, 0, buffer, ref length))
                            path = buffer.ToString();
                        CloseHandle(process);
                    }
                    found.Add((entry.ProcessId, path));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return found;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public int AllocationProtect;
            public short PartitionId;
            public IntPtr RegionSize;
            public int State;
            public int Protect;
            public int Type;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public int Size;
            public int Usage;
            public int ProcessId;
            public IntPtr DefaultHeapId;
            public int ModuleId;
            public int Threads;
            public int ParentProcessId;
            public int PriorityClassBase;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryInfo info, IntPtr length);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("psapi", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, int filter);

        [DllImport("psapi", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder name, int size);
    }
}
