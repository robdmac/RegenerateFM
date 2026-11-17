using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RegenerateFM
{
    // The game builds RegenerateFM has been checked on, beyond those built
    // into the plugin (GameBuild.cs): builds/regeneratefm-builds.json in the
    // repository, signed with the release key (Signed), so a game update can
    // be marked as checked the day it comes out, without a new release.
    // tools/sign_builds.py adds a build and signs the list. The plugin keeps
    // the last list it downloaded (Cached) for the next start, and refuses a
    // list older than the one it has, so an old list can't be sent again.
    public static class CheckedBuilds
    {
        public const string ListUrl = "https://raw.githubusercontent.com/robdmac/RegenerateFM/main/builds/regeneratefm-builds.json";
        public const string FileName = "checked-builds.json";
        public static string CachePath => Path.Combine(KeyStore.Folder, FileName);

        public const string Windows = "windows";
        public const string MacOS = "macos";

        private static readonly HttpClient Client = ImageApi.NewClient(TimeSpan.FromSeconds(20));
        private static readonly Regex Sha256Hex = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        public sealed class Build
        {
            public string Name;         // "FM27 27.0.1 (Steam)", for the log
            public string Os;           // Windows or MacOS
            public string GameAssembly; // SHA-256 of GameAssembly.dll / GameAssembly.dylib
            public string GamePlugin;   // SHA-256 of game_plugin.dll / game_plugin
            public GameInfo Game;       // from the user data folder's name ("Football Manager 27")
        }

        public sealed class BuildList
        {
            public DateTime Issued;
            public List<Build> Builds = new List<Build>();

            // The build with these binaries, or null.
            public Build Find(string os, string gameAssembly, string gamePlugin) =>
                Builds.FirstOrDefault(build => build.Os == os &&
                                               string.Equals(build.GameAssembly, gameAssembly, StringComparison.OrdinalIgnoreCase) &&
                                               string.Equals(build.GamePlugin, gamePlugin, StringComparison.OrdinalIgnoreCase));
        }

        // The list in a signed file, or null if it isn't one signed with the
        // release key. A build it can't use (an unknown system, a malformed
        // hash, a game before FM26) is left out rather than failing the list.
        public static BuildList Read(byte[] signedJson) => Read(signedJson, SelfUpdate.ReleaseKey);

        public static BuildList Read(byte[] signedJson, string publicKey)
        {
            byte[] payload = Signed.Payload(signedJson, publicKey, out _);
            if (payload == null)
                return null;
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var list = new BuildList
                {
                    Issued = DateTime.Parse(root.GetProperty("issued").GetString() ?? "", CultureInfo.InvariantCulture,
                                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                };
                foreach (var item in root.GetProperty("builds").EnumerateArray())
                {
                    var build = new Build
                    {
                        Name = StringOf(item, "name") ?? "",
                        Os = StringOf(item, "os"),
                        GameAssembly = StringOf(item, "game_assembly")?.ToLowerInvariant(),
                        GamePlugin = StringOf(item, "game_plugin")?.ToLowerInvariant(),
                        Game = GameInfo.FromAppInfo("Sports Interactive\n" + StringOf(item, "user_data")),
                    };
                    if ((build.Os == Windows || build.Os == MacOS) && build.Game != null &&
                        Sha256Hex.IsMatch(build.GameAssembly ?? "") && Sha256Hex.IsMatch(build.GamePlugin ?? ""))
                        list.Builds.Add(build);
                }
                return list;
            }
            catch (Exception error) when (error is JsonException || error is KeyNotFoundException || error is InvalidOperationException ||
                                          error is FormatException)
            {
                return null;
            }
        }

        private static string StringOf(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // The list kept from an earlier download, or null.
        public static BuildList Cached(string path)
        {
            try
            {
                return File.Exists(path) ? Read(File.ReadAllBytes(path)) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return null;
            }
        }

        // Downloads the list and keeps it in ``path`` if it is signed and newer
        // than the one there. Returns the newest list that checks out (the kept
        // one if the download is older, isn't signed or fails), or null.
        public static async Task<BuildList> Refresh(string path, string url = ListUrl, string publicKey = SelfUpdate.ReleaseKey)
        {
            BuildList kept;
            try
            {
                kept = File.Exists(path) ? Read(File.ReadAllBytes(path), publicKey) : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                kept = null;
            }
            byte[] downloaded;
            try
            {
                using var response = await Client.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return kept;
                downloaded = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException ||
                                          error is InvalidOperationException)
            {
                return kept;
            }
            var list = Read(downloaded, publicKey);
            if (list == null || (kept != null && list.Issued <= kept.Issued))
                return kept;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.Write(path, downloaded);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            return list;
        }
    }
}
