using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace RegenerateFM.Desktop.Linux
{
    // BepInEx makes FM26's interop assemblies on the game's first start, from
    // Unity's base libraries, which it downloads from unity.bepinex.dev. If
    // that site doesn't answer, FM26 starts without RegenerateFM and only
    // BepInEx's log says why. BepInEx uses a zip already in BepInEx/unity-libs
    // instead, so the installer downloads it first and says if it couldn't.
    public static class UnityLibraries
    {
        // BepInEx's UnityBaseLibrariesSource, with {0} for the version.
        public const string Source = "https://unity.bepinex.dev/libraries/{0}.zip";

        // Replaced by tests: the zip's bytes from a URL.
        public static Func<string, byte[]> Download = DownloadZip;

        // FM26's Unity version as BepInEx names its zip ("6000.0.52" for
        // 6000.0.52f1), from the header of the game's globalgamemanagers; null
        // if it can't be read.
        public static string Version(string gameFolder)
        {
            foreach (string data in new[] { "fm_Data", "Data", "fm.app/Contents/Resources/Data" })
            {
                string path = Path.Combine(gameFolder, data, "globalgamemanagers");
                if (!File.Exists(path))
                    continue;
                var header = new byte[256];
                int read;
                using (var stream = File.OpenRead(path))
                    read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                var match = Regex.Match(Encoding.ASCII.GetString(header, 0, read), @"(\d+)\.(\d+)\.(\d+)[abfp]\d+");
                return match.Success ? match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value : null;
            }
            return null;
        }

        // Puts the zip in the game's BepInEx/unity-libs, unless it is there
        // already. What happened, for the installer's output.
        public static string Fetch(string gameFolder)
        {
            string version = Version(gameFolder);
            if (version == null)
                return "Couldn't read FM26's Unity version, so BepInEx will download Unity's libraries itself when FM26 first starts.";
            string zip = Path.Combine(gameFolder, "BepInEx", "unity-libs", version + ".zip");
            if (File.Exists(zip))
                return null;
            string url = string.Format(Source, version);
            byte[] data;
            try
            {
                data = Download(url);
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException || error is OperationCanceledException)
            {
                return "Couldn't download Unity's libraries for BepInEx from " + url + " (" + error.Message + "). BepInEx tries again " +
                       "when FM26 starts; if RegenerateFM's commands are missing in FM26, run install-linux.sh again later.";
            }
            if (data.Length < 4 || data[0] != 'P' || data[1] != 'K' || data[2] != 3 || data[3] != 4)
                return "Unity's libraries from " + url + " aren't a ZIP file, so BepInEx will download them itself when FM26 first starts.";
            AtomicFile.Write(zip, data);
            return "Downloaded Unity's libraries for BepInEx (" + version + ").";
        }

        private static byte[] DownloadZip(string url)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
    }
}
