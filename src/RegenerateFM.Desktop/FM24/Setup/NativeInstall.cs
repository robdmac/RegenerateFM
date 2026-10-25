using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RegenerateFM.Desktop.FM24.Memory;

namespace RegenerateFM.Desktop.FM24.Setup
{
    // A setup step refused, with what to do about it.
    public sealed class SetupException : Exception
    {
        public SetupException(string message) : base(message) { }
    }

    // Installs or removes RegenerateFM's native library in FM24, pinned to the
    // checked build. On Windows and Proton it is winmm.dll beside fm.exe. On
    // macOS it is libregeneratefm24.dylib beside fm.app, which FM24 loads
    // through run_regeneratefm24.sh (its Steam launch option). Every file
    // change is journalled, so a failed install or removal is undone.
    public sealed class NativeInstall
    {
        public static readonly Dictionary<string, string> PanelFiles = new Dictionary<string, string>
        {
            ["player"] = "panels/player/player overview panel.xml",
            ["staff"] = "panels/non_player/non player profile.xml",
        };
        // Earlier versions added a Squad screen "Faces" button here. The squad
        // command now lives in FM24's own Squad menu, so an upgrade removes
        // that panel change.
        public const string LegacySquadPanel = "panels/team/team squad.xml";
        public const string BackupSuffix = ".regeneratefm-native-backup";
        public const string Config = "regeneratefm24.ini";
        public const string Manifest = "regeneratefm24-install.json";
        public const int DefaultPort = 8769;
        internal static readonly Regex TokenPattern = new Regex("^[A-Za-z0-9_-]{40,128}$", RegexOptions.Compiled);

        public NativeInstall(bool mac)
        {
            Mac = mac;
        }

        // For FM24 for macOS (the dylib and its launcher) or for fm.exe (the
        // DLL, on Windows and Proton).
        public bool Mac { get; }
        public string Executable => Mac ? "fm.app/Contents/MacOS/fm" : "fm.exe";
        public string Library => Mac ? "libregeneratefm24.dylib" : "winmm.dll";
        public string Launcher => Mac ? "run_regeneratefm24.sh" : null;

        // The checked FM24 executable's SHA-256 (native/fm24/supported_build*.json).
        public string SupportedSha256 = null;

        public string Supported => SupportedSha256 ??
            JsonNode.Parse(Resources.Text(Mac ? "supported_build_macos.json" : "supported_build.json"))["sha256"].GetValue<string>();

        // Replaced by tests: whether FM24 from a folder is running.
        public Func<string, bool> GameRunning;

        public static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        public static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // FM24 from this folder is running (not FM26, whose executable is fm.exe too).
        public bool Running(string game)
        {
            if (GameRunning != null)
                return GameRunning(game);
            string folder = Path.GetFileName(game.TrimEnd(Path.DirectorySeparatorChar));
            foreach (var (_, executable) in Processes.Running())
                if (Processes.IsFm24(executable, folder))
                    return true;
            return false;
        }

        // Journals every file change so a failed install or removal is undone.
        // Each write is atomic (a temporary file, then a rename). On failure the
        // journal is replayed in reverse, restoring prior bytes and removing new
        // files.
        public sealed class Transaction
        {
            private readonly List<(string Kind, string Path, byte[] Previous, UnixFileMode Mode)> _changes =
                new List<(string, string, byte[], UnixFileMode)>();

            // Replaced by tests: the atomic write and the removal.
            public Action<string, byte[], UnixFileMode> Write_ = AtomicWrite;
            public Action<string> Delete_ = File.Delete;

            public int Count => _changes.Count;

            private static UnixFileMode Mode(string path) => OperatingSystem.IsWindows() ? 0 : File.GetUnixFileMode(path);

            private void Record(string path)
            {
                _changes.Add(File.Exists(path) ? ("file", path, File.ReadAllBytes(path), Mode(path)) : ("file", path, null, 0));
            }

            public void Write(string path, byte[] data, bool isPrivate = true, UnixFileMode? mode = null)
            {
                string parent = Path.GetDirectoryName(path);
                if (!Directory.Exists(parent))
                    MakeDirectory(parent);
                Record(path);
                Write_(path, data, mode ?? (isPrivate ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                      : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
            }

            public void Remove(string path)
            {
                if (!File.Exists(path))
                    return;
                Record(path);
                Delete_(path);
            }

            public void MakeDirectory(string path)
            {
                string parent = Path.GetDirectoryName(path);
                if (!Directory.Exists(parent))
                    MakeDirectory(parent);
                Directory.CreateDirectory(path);
                _changes.Add(("dir", path, null, 0));
            }

            public void RemoveEmptyDirectories(string path, string stop)
            {
                while (Path.GetFullPath(path) != Path.GetFullPath(stop) && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    var mode = Mode(path);
                    Directory.Delete(path);
                    _changes.Add(("rmdir", path, null, mode));
                    path = Path.GetDirectoryName(path);
                }
            }

            // Undoes every change; the files that couldn't be restored.
            public List<string> Rollback()
            {
                var failures = new List<string>();
                for (int i = _changes.Count - 1; i >= 0; i--)
                {
                    var (kind, path, previous, mode) = _changes[i];
                    try
                    {
                        if (kind == "dir")
                            Directory.Delete(path);
                        else if (kind == "rmdir")
                        {
                            Directory.CreateDirectory(path);
                            if (!OperatingSystem.IsWindows())
                                File.SetUnixFileMode(path, mode);
                        }
                        else if (previous == null)
                        {
                            if (File.Exists(path))
                                File.Delete(path);
                        }
                        else
                            AtomicWrite(path, previous, mode);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        failures.Add(path + ": " + error.Message);
                    }
                }
                _changes.Clear();
                return failures;
            }
        }

        public static void AtomicWrite(string path, byte[] data, UnixFileMode mode) => AtomicFile.Write(path, data, (int)mode);

        // The manifest, the original two-panel format upgraded in memory; null when there is none.
        public static JsonObject LoadManifest(string path)
        {
            if (!File.Exists(path))
                return null;
            var invalid = new InvalidDataException("Invalid manifest.");
            var metadata = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw invalid;
            if (!metadata.ContainsKey("version"))
            {
                if (!(metadata["panelSha256"] is JsonArray hashes) || hashes.Count != 2 || Text(hashes[0]) == null || Text(hashes[1]) == null)
                    throw invalid;
                var panels = new JsonObject
                {
                    [PanelFiles["player"]] = new JsonObject { ["sha256"] = Text(hashes[0]), ["created"] = false },
                    [PanelFiles["staff"]] = new JsonObject { ["sha256"] = Text(hashes[1]), ["created"] = false },
                };
                metadata = new JsonObject { ["version"] = 2, ["dllSha256"] = Text(metadata["dllSha256"]), ["panels"] = panels };
            }
            if (!(metadata["version"] is JsonValue version) || !version.TryGetValue(out int number) || number != 2)
                throw new InvalidDataException("Unsupported native RegenerateFM installation manifest.");
            if (Text(metadata["dllSha256"]) == null || !(metadata["panels"] is JsonObject entries))
                throw invalid;
            foreach (var (_, entry) in entries)
                if (!(entry is JsonObject panel) || Text(panel["sha256"]) == null ||
                    !(panel["created"] is JsonValue created) || !created.TryGetValue(out bool _))
                    throw invalid;
            return metadata;
        }

        private static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue(out string text) ? text : null;

        public static bool HasPopup(string panel)
        {
            string text = Skin.ReadText(panel);
            return text.Contains("id=\"fmnP\"") || text.Contains("id=\"fmnS\"");
        }

        // regeneratefm24.ini. The DLL reads it with GetPrivateProfileString,
        // which takes UTF-16 with a byte-order mark, so any companion path works.
        // On macOS it is UTF-8: the library reads only its port and token, and
        // run_regeneratefm24.sh starts the companion.
        public byte[] ConfigText(int port, string token, string companion)
        {
            string text = "[regeneratefm]\nenabled=1\nport=" + port + "\ntoken=" + token + "\n";
            if (!string.IsNullOrEmpty(companion))
            {
                if (companion.Contains('\n') || companion.Contains('\r'))
                    throw new ArgumentException("Invalid companion path.");
                text += "companion=" + companion + "\n";
            }
            if (Mac)
                return Encoding.UTF8.GetBytes(text);
            return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text.Replace("\n", "\r\n"))).ToArray();
        }

        // regeneratefm24.ini's settings, in either encoding.
        public static Dictionary<string, string> ReadConfig(string path)
        {
            var data = File.ReadAllBytes(path);
            string text = data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE) ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
                        : data.Length >= 2 && (data[0] == 0xFE && data[1] == 0xFF) ? Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
                        : new UTF8Encoding(false, true).GetString(data);
            var values = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                string clean = line.TrimEnd('\r');
                int equals = clean.IndexOf('=');
                if (equals >= 0)
                    values[clean.Substring(0, equals)] = clean.Substring(equals + 1);
            }
            return values;
        }

        // A RegenerateFM build of the native library for this system.
        private bool IsLibrary(byte[] data)
        {
            bool magic = Mac ? data.Length >= 4 && data[0] == 0xCF && data[1] == 0xFA && data[2] == 0xED && data[3] == 0xFE
                             : data.Length >= 2 && data[0] == (byte)'M' && data[1] == (byte)'Z';
            return magic && data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("REGENERATEFM24_NATIVE_MENU_HOOK_INSTALLED")) >= 0;
        }

        private static void RestorePanel(string skin, string name, JsonNode entry, Transaction transaction)
        {
            string panel = Path.Combine(skin, name);
            if (entry["created"].GetValue<bool>())
            {
                transaction.Remove(panel);
                transaction.RemoveEmptyDirectories(Path.GetDirectoryName(panel), skin);
            }
            else
            {
                string backup = panel + BackupSuffix;
                transaction.Write(panel, File.ReadAllBytes(backup));
                transaction.Remove(backup);
            }
        }

        // What Install and Remove print.
        public Action<string> Log = text => Console.WriteLine(text);
        public Func<Transaction> NewTransaction = () => new Transaction();

        // Installs the library into ``game``, with its token, the companion FM24
        // starts (as Windows sees its path) and the portrait popups in the skin's
        // panels. ``library`` is the RegenerateFM build to install.
        public void Install(string game, string skin, byte[] library, string token, string companion = null, int port = DefaultPort)
        {
            game = Path.GetFullPath(game);
            skin = Path.GetFullPath(skin);
            var metadata = Check(game, skin);
            if (Digest(Path.Combine(game, Executable)) != Supported)
                throw new SetupException("This FM24 executable is not the validated 24.4.2 build; no hook was installed.");
            if (!TokenPattern.IsMatch(token ?? "") || port < 1024 || port > (Mac ? 65533 : 65535))
                throw new SetupException("Invalid private token or bridge port.");
            if (!IsLibrary(library))
                throw new SetupException("The library is not a RegenerateFM native menu build for this system.");

            // Every panel's original text is worked out before anything is written.
            var installed = metadata?["panels"]?.AsObject();
            // The skin is optional: without it (or a panel of it) only the
            // portrait popup is missing. The companion builds it later if needed.
            var modified = new Dictionary<string, string>();
            var adopted = new Dictionary<string, JsonObject>();
            foreach (var (kind, name) in PanelFiles)
            {
                string panel = Path.Combine(skin, name), backup = panel + BackupSuffix;
                string original;
                if (installed != null && installed.ContainsKey(name) && installed[name]["created"].GetValue<bool>())
                {
                    // Adopted last time (Check confirmed it is unchanged): still owned, no backup.
                    adopted[name] = new JsonObject { ["sha256"] = Digest(panel), ["created"] = true };
                    continue;
                }
                else if (installed != null && installed.ContainsKey(name))
                    original = Skin.ReadText(backup);
                else if (!File.Exists(panel))
                    continue;
                else if (HasPopup(panel))
                {
                    // Built with the popup by the companion: owned, removed on uninstall.
                    adopted[name] = new JsonObject { ["sha256"] = Digest(panel), ["created"] = true };
                    continue;
                }
                else if (File.Exists(backup))
                    throw new SetupException("Unowned panel backup already exists; refusing to replace it: " + backup);
                else
                    original = Skin.ReadText(panel);
                try
                {
                    modified[name] = Skin.ModifyPanel(original, kind);
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidDataException)
                {
                    throw new SetupException(name + ": " + error.Message);
                }
            }

            var transaction = NewTransaction();
            try
            {
                if (installed != null && installed.ContainsKey(LegacySquadPanel))
                    RestorePanel(skin, LegacySquadPanel, installed[LegacySquadPanel], transaction);
                var panels = new JsonObject();
                foreach (var (name, entry) in adopted)
                    panels[name] = entry;
                foreach (var (name, text) in modified)
                {
                    string panel = Path.Combine(skin, name);
                    if (installed == null || !installed.ContainsKey(name))
                        transaction.Write(panel + BackupSuffix, File.ReadAllBytes(panel));
                    transaction.Write(panel, Encoding.UTF8.GetBytes(text));
                    panels[name] = new JsonObject { ["sha256"] = Digest(panel), ["created"] = false };
                }
                string target = Path.Combine(game, Library);
                transaction.Write(target, library, isPrivate: false);
                if (Launcher != null)
                    transaction.Write(Path.Combine(game, Launcher), Resources.Bytes(Launcher),
                                      mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                transaction.Write(Path.Combine(game, Config), ConfigText(port, token, companion));
                var manifest = new JsonObject { ["version"] = 2, ["dllSha256"] = Digest(target), ["panels"] = panels };
                transaction.Write(Path.Combine(game, Manifest),
                                  Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"));
            }
            catch (Exception)
            {
                Undo(transaction);
                throw;
            }
            Log("Installed the FM24 native portrait menus and the Squad menu's \"Generate missing squad faces...\" item " +
                "with an authenticated loopback bridge.");
            if (installed != null && installed.ContainsKey(LegacySquadPanel))
                Log("Removed the earlier Squad screen Faces button; the command is now in the Squad menu.");
            if (Mac)
                Log("Steam launch option for FM24: \"" + Path.Combine(game, Launcher) + "\" %command%");
            else if (OperatingSystem.IsWindows())
                Log("On Windows, FM24 loads the DLL from its folder; no launch option is needed.");
            if (companion != null)
                Log("FM24 starts the RegenerateFM companion itself.");
            else
                Log("Start the companion while you play (add --preview-only for no-charge tests).");
        }

        // Removes the library, its settings and the launcher, and restores the panels.
        public void Remove(string game, string skin)
        {
            game = Path.GetFullPath(game);
            skin = Path.GetFullPath(skin);
            var metadata = Check(game, skin);
            if (metadata == null)
                throw new SetupException("No native RegenerateFM installation was found in this folder.");
            var transaction = NewTransaction();
            try
            {
                foreach (var (name, entry) in metadata["panels"].AsObject())
                    RestorePanel(skin, name, entry, transaction);
                foreach (string file in new[] { Library, Config, Manifest, Launcher }.Where(file => file != null))
                    transaction.Remove(Path.Combine(game, file));
            }
            catch (Exception)
            {
                Undo(transaction);
                throw;
            }
            Log("Removed the native library and configuration and restored the original RegenerateFM panels.");
        }

        private void Undo(Transaction transaction)
        {
            var failures = transaction.Rollback();
            Log(failures.Count > 0
                ? "RegenerateFM could not undo every change. Restore these files manually:\n  " + string.Join("\n  ", failures)
                : "The native RegenerateFM change failed; all files were restored.");
        }

        // The checks before any change: FM24 closed, no other library, panels
        // as installed. The manifest, or null when RegenerateFM isn't installed.
        private JsonObject Check(string game, string skin)
        {
            if (Running(game))
                throw new SetupException("Close FM24 before installing or removing its native DLL.");
            string target = Path.Combine(game, Library), manifest = Path.Combine(game, Manifest);
            JsonObject metadata;
            try
            {
                metadata = LoadManifest(manifest);
            }
            catch (Exception error) when (error is JsonException || error is InvalidDataException || error is InvalidOperationException ||
                                          error is FormatException)
            {
                throw new SetupException("Could not read " + manifest + ": " + error.Message);
            }
            if (File.Exists(target) && (metadata == null || Digest(target) != metadata["dllSha256"].GetValue<string>()))
                throw new SetupException("There is an unrecognized " + Library + " in this game folder; it will not be overwritten.");
            if (metadata != null)
                foreach (var (name, entry) in metadata["panels"].AsObject())
                {
                    string panel = Path.Combine(skin, name);
                    if (!File.Exists(panel) || Digest(panel) != entry["sha256"].GetValue<string>())
                        throw new SetupException("The native panel has been edited since installation: " + panel);
                    if (!entry["created"].GetValue<bool>() && !File.Exists(panel + BackupSuffix))
                        throw new SetupException("Native menu panel backup is missing for " + panel + ".");
                }
            return metadata;
        }
    }
}
