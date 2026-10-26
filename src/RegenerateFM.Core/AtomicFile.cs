using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RegenerateFM
{
    // Files replaced in one step: written to a temporary file beside the
    // target, flushed, then renamed over it, so a crash leaves the old file or
    // the new one. Used for every file RegenerateFM writes into a game, Steam
    // or its own folder.
    public static class AtomicFile
    {
        public const int OwnerOnly = 0x180;   // 0600
        public const int Readable = 0x1A4;    // 0644
        public const int Executable = 0x1ED;  // 0755

        // ``mode`` (a Unix mode; ignored on Windows) is set before the data is
        // written, so a private file is never readable by others.
        public static void Write(string path, byte[] data, int? mode = null)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, "." + Path.GetFileName(path) + ".regeneratefm-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    if (mode != null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && chmod(temporary, (uint)mode.Value) != 0)
                        throw new IOException("Could not set the permissions of " + path + " (error " + Marshal.GetLastWin32Error() + ").");
                    file.Write(data, 0, data.Length);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);
    }
}
