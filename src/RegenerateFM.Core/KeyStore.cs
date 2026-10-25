using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM
{
    // The player's OpenAI key, kept outside the game folder (which people zip
    // up when asking for mod support). On Windows, and under Proton, it is
    // encrypted with DPAPI for the current Windows user. On macOS it is in
    // Application Support, readable only by the user.
    public static class KeyStore
    {
        public const string KeysPage = "https://platform.openai.com/api-keys";
        private static readonly byte[] Protected = Encoding.ASCII.GetBytes("RFMK1\n");
        private static readonly byte[] Plain = Encoding.ASCII.GetBytes("RFMK0\n");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RegenerateFM OpenAI API key");
        // An OpenAI key, or a RegenerateFM credits token (Credits).
        private static readonly Regex KeyPattern = new Regex(@"^(sk-[A-Za-z0-9_\-]{16,300}|rfm_[A-Za-z0-9_\-]{20,100})$", RegexOptions.Compiled);

        // .NET 6 maps ApplicationData to ~/.config on macOS, not where Mac programs keep their data.
        public static string ApplicationSupport =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");

        // RegenerateFM's folder for the key and settings.ini: %APPDATA%\RegenerateFM
        // on Windows (inside the game's prefix under Proton), Application Support
        // on macOS, ~/.config elsewhere.
        public static string Folder => FolderOverride ??
            Path.Combine(OperatingSystem.IsMacOS() ? ApplicationSupport : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "RegenerateFM");

        // Set by tests, so they never touch the player's own key or settings.
        public static string FolderOverride;

        public static string DefaultPath => Path.Combine(Folder, "openai-key");

        // The pasted key without surrounding spaces, or an exception whose
        // message tells the player what is wrong.
        public static string Clean(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0)
                throw new ArgumentException("Paste your OpenAI API key first.");
            if (!KeyPattern.IsMatch(key))
                throw new ArgumentException("That doesn't look like an OpenAI API key. It starts with sk- and has no spaces.");
            return key;
        }

        public static string Load(string path)
        {
            if (!File.Exists(path))
                return null;
            var data = File.ReadAllBytes(path);
            byte[] payload;
            if (StartsWith(data, Protected))
                payload = Unprotect(data.Skip(Protected.Length).ToArray());
            else if (StartsWith(data, Plain))
                payload = data.Skip(Plain.Length).ToArray();
            else
                throw new InvalidDataException("The saved OpenAI key file is not in RegenerateFM's format: " + path);
            string key = Encoding.UTF8.GetString(payload).Trim();
            return key.Length > 0 ? key : null;
        }

        public static void Save(string path, string key)
        {
            var payload = Encoding.UTF8.GetBytes(Clean(key));
            var data = OperatingSystem.IsWindows() ? Protected.Concat(Protect(payload)).ToArray() : Plain.Concat(payload).ToArray();
            // Owner-only before the key is written.
            AtomicFile.Write(path, data, AtomicFile.OwnerOnly);
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            return data.Length >= prefix.Length && data.Take(prefix.Length).SequenceEqual(prefix);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
                                                    IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
                                                      IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private const int UiForbidden = 0x1;

        private static byte[] Protect(byte[] data) => Transform(data, protect: true);

        private static byte[] Unprotect(byte[] data)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidDataException("The saved OpenAI key is encrypted for Windows and can't be read here.");
            return Transform(data, protect: false);
        }

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var output = new DataBlob();
            try
            {
                var inputBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref inputBlob, "RegenerateFM OpenAI API key", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok)
                    throw new InvalidDataException((protect ? "Could not encrypt" : "Could not decrypt") +
                                                   " the OpenAI key (Windows error " + Marshal.GetLastWin32Error() + ").");
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                input.Free();
                entropy.Free();
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }
    }
}
