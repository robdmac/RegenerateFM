using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RegenerateFM
{
    // RegenerateFM installing a newer release itself, when the player chooses
    // Update on the notice UpdateCheck brings up. Beside each release ZIP,
    // RegenerateFM-update.json names the files the update replaces (the plugin)
    // with their SHA-256, and the BepInEx builds it runs on. It is signed with
    // the release key, whose public half is below, so neither a changed
    // download nor a changed release can install anything: a manifest or file
    // that doesn't check out leaves the game as it was.
    public static class SelfUpdate
    {
        public const string ManifestAsset = "RegenerateFM-update.json";
        // The plugin's place under BepInEx's folder, as the release puts it.
        public const string PluginPath = "plugins/RegenerateFM/RegenerateFM.dll";
        public const string ReleaseDownloads = "https://github.com/robdmac/RegenerateFM/releases/download";

        // The release key's public half (SubjectPublicKeyInfo, ECDSA P-256).
        // tools/make_release.py signs with the private half, which never
        // leaves the computer releases are made on.
        internal const string ReleaseKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEO5XKQemui+GQiGmpMzCYs478xBFdnkmyyAa9LZ0EtGyom80z+RQoz6ud86H9l9LnJtpTwvrBI+HM87pNNoLSvg==";

        // The old copy of a replaced file, deleted at the next start (Windows
        // lets a running program's DLL be renamed, not overwritten).
        public const string OldSuffix = ".regeneratefm-old";
        private const string NewSuffix = ".regeneratefm-new";

        private static readonly HttpClient Client = ImageApi.NewClient(TimeSpan.FromSeconds(120));

        public sealed class UpdateFile
        {
            public string Path;   // under BepInEx's folder, with "/"
            public string Asset;  // its name among the release's files
            public string Sha256; // lower-case hex
        }

        public sealed class Manifest
        {
            public string Version;
            public List<string> BepInEx = new List<string>();
            public List<UpdateFile> Files = new List<UpdateFile>();
        }

        // Why an update didn't install, for the notice: the game is unchanged.
        public sealed class UpdateException : Exception
        {
            public UpdateException(string message) : base(message) { }
        }

        // Downloads ``version``'s manifest and files and puts them in place under
        // ``bepinexRoot``. ``bepinexVersion`` is the running BepInEx's
        // ("6.0.0-be.760+a1afbfb…"). The plugin goes where the running one
        // was loaded from, ``pluginFile``, wherever the player put it.
        // ``releaseDownloads`` replaces GitHub's address when testing; the
        // signature is checked either way.
        public static async Task Install(string version, string bepinexRoot, string bepinexVersion, string pluginFile,
                                         string releaseDownloads = null, CancellationToken cancel = default)
        {
            string release = (string.IsNullOrWhiteSpace(releaseDownloads) ? ReleaseDownloads : releaseDownloads.Trim().TrimEnd('/')) +
                             "/v" + version + "/";
            var manifest = Verify(await Download(release + ManifestAsset, cancel).ConfigureAwait(false));
            if (manifest.Version != version)
                throw new UpdateException("The update's manifest is for " + manifest.Version + ", not " + version + ".");
            if (!RunsOn(manifest, bepinexVersion))
                throw new UpdateException("RegenerateFM " + version + " needs a different BepInEx (this is " + bepinexVersion + ").");
            var files = new List<(string Target, byte[] Data)>();
            foreach (var file in manifest.Files)
            {
                byte[] data = await Download(release + Uri.EscapeDataString(file.Asset), cancel).ConfigureAwait(false);
                if (Hex(SHA256.HashData(data)) != file.Sha256)
                    throw new UpdateException(file.Asset + " isn't the file the update's manifest names.");
                files.Add((file.Path == PluginPath && !string.IsNullOrEmpty(pluginFile) ? pluginFile : Target(bepinexRoot, file.Path), data));
            }
            Replace(files);
        }

        // The manifest in a signed RegenerateFM-update.json:
        // {"payload": base64 of the manifest's JSON, "signature": base64 of an
        // ECDSA P-256 / SHA-256 signature of those bytes, as openssl writes it}.
        public static Manifest Verify(byte[] signedJson) => Verify(signedJson, ReleaseKey);

        public static Manifest Verify(byte[] signedJson, string publicKey)
        {
            byte[] payload = Signed.Payload(signedJson, publicKey, out var problem);
            if (problem == Signed.Problem.Unreadable)
                throw new UpdateException("The update's manifest can't be read.");
            if (problem == Signed.Problem.NotSigned)
                throw new UpdateException("The update isn't signed with RegenerateFM's release key.");
            return ParseManifest(payload);
        }

        private static Manifest ParseManifest(byte[] payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var manifest = new Manifest { Version = root.GetProperty("version").GetString() };
                foreach (var build in root.GetProperty("bepinex").EnumerateArray())
                    manifest.BepInEx.Add(build.GetString());
                foreach (var file in root.GetProperty("files").EnumerateArray())
                    manifest.Files.Add(new UpdateFile
                    {
                        Path = file.GetProperty("path").GetString(),
                        Asset = file.GetProperty("asset").GetString(),
                        Sha256 = file.GetProperty("sha256").GetString()?.ToLowerInvariant(),
                    });
                if (string.IsNullOrEmpty(manifest.Version) || manifest.Files.Count == 0 ||
                    manifest.Files.Any(file => !SafePath.IsMatch(file.Path ?? "") || file.Path.Split('/').Any(part => part == "." || part == "..") ||
                                               string.IsNullOrEmpty(file.Asset) || !Sha256Hex.IsMatch(file.Sha256 ?? "")))
                    throw new UpdateException("The update's manifest is incomplete.");
                return manifest;
            }
            catch (Exception error) when (error is JsonException || error is KeyNotFoundException || error is InvalidOperationException)
            {
                throw new UpdateException("The update's manifest can't be read.");
            }
        }

        // Whether the running BepInEx is one the update was made for: its
        // version without the commit ("6.0.0-be.760" of "6.0.0-be.760+a1afbfb").
        public static bool RunsOn(Manifest manifest, string bepinexVersion)
        {
            string running = (bepinexVersion ?? "").Split('+')[0].Trim();
            return running.Length > 0 && manifest.BepInEx.Contains(running);
        }

        // Folders and file names only, under BepInEx's folder: nothing absolute
        // and no "..".
        private static readonly Regex SafePath = new Regex(@"^(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
        private static readonly Regex Sha256Hex = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private static string Target(string bepinexRoot, string path) =>
            System.IO.Path.Combine(bepinexRoot, path.Replace('/', System.IO.Path.DirectorySeparatorChar));

        // Each file written beside its target, then swapped in: the old one is
        // renamed aside (it may be loaded) and the new one renamed into place.
        // If any write or swap fails, everything is put back as it was.
        public static void Replace(IReadOnlyList<(string Target, byte[] Data)> files)
        {
            var swapped = new List<string>();
            try
            {
                foreach (var (target, data) in files)
                    File.WriteAllBytes(target + NewSuffix, data);
                foreach (var (target, _) in files)
                {
                    if (File.Exists(target))
                        File.Move(target, target + OldSuffix, overwrite: true);
                    File.Move(target + NewSuffix, target);
                    swapped.Add(target);
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                foreach (var target in swapped)
                    try
                    {
                        File.Delete(target);
                        if (File.Exists(target + OldSuffix))
                            File.Move(target + OldSuffix, target);
                    }
                    catch (Exception) { }
                foreach (var (target, _) in files)
                {
                    try { File.Delete(target + NewSuffix); }
                    catch (Exception) { }
                    if (!File.Exists(target) && File.Exists(target + OldSuffix))
                        try { File.Move(target + OldSuffix, target); }
                        catch (Exception) { }
                }
                throw new UpdateException("The game's folder couldn't be changed: " + error.Message);
            }
        }

        // The old copies an update left in ``folder``, once nothing has them loaded.
        public static void CleanUp(string folder)
        {
            try
            {
                foreach (var path in Directory.GetFiles(folder, "*" + OldSuffix).Concat(Directory.GetFiles(folder, "*" + NewSuffix)))
                    try { File.Delete(path); }
                    catch (Exception) { }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }

        private static async Task<byte[]> Download(string url, CancellationToken cancel)
        {
            try
            {
                using var response = await Client.GetAsync(url, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException("The update couldn't be downloaded (" + (int)response.StatusCode + ").");
                return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                throw new UpdateException("The update couldn't be downloaded: " + error.Message);
            }
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
