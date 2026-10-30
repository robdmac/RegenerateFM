using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RegenerateFM
{
    // RegenerateFM's settings, in settings.ini beside the saved key (KeyStore's
    // folder), so the games on one computer share them like the key. The file
    // is reread before each request and written with the defaults, and what
    // each one does, the first time it is needed.
    public sealed class Settings
    {
        public const string FileName = "settings.ini";
        public const string DefaultModel = "gpt-image-2.5-sunburst";
        public const string DefaultQuality = "medium";
        public const string DefaultApiBaseUrl = "https://api.openai.com/v1";

        public string Model = DefaultModel;
        public string Quality = DefaultQuality;
        public string ApiBaseUrl = DefaultApiBaseUrl;
        public string CreditsUrl = Credits.DefaultServiceUrl;

        public static string DefaultPath => Path.Combine(KeyStore.Folder, FileName);

        private const string Template =
            "; RegenerateFM settings, shared by FM27, FM26 and FM24 on this computer. They are\n" +
            "; read again before each face, so a change applies without restarting the game.\n" +
            "\n" +
            "[OpenAI]\n" +
            "; The OpenAI image model.\n" +
            "Model = " + DefaultModel + "\n" +
            "; Rendering quality: low, medium, high, xhigh, max or auto. Higher costs more per face.\n" +
            "Quality = " + DefaultQuality + "\n" +
            "; The image API's address. Leave it unchanged unless you are testing.\n" +
            "ApiBaseUrl = " + DefaultApiBaseUrl + "\n" +
            "\n" +
            "[Credits]\n" +
            "; The RegenerateFM credits service, used when you buy credits instead of using an OpenAI key.\n" +
            "ServiceUrl = " + Credits.DefaultServiceUrl + "\n";

        // The settings in ``path``; the defaults when it doesn't exist, and then
        // the file is written for the player to edit (if it can be).
        public static Settings Load(string path)
        {
            var settings = new Settings();
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, Template, new UTF8Encoding(false));
                }
                catch (Exception written) when (written is IOException || written is UnauthorizedAccessException)
                {
                }
                return settings;
            }
            foreach (var ((section, key), value) in Parse(text))
            {
                if (section == "openai" && key == "model")
                    settings.Model = value;
                else if (section == "openai" && key == "quality")
                    settings.Quality = value.ToLowerInvariant();
                else if (section == "openai" && key == "apibaseurl")
                    settings.ApiBaseUrl = value;
                else if (section == "credits" && key == "serviceurl")
                    settings.CreditsUrl = value;
            }
            return settings;
        }

        // (section, key) → value, both names in lower case; later lines win.
        public static Dictionary<(string Section, string Key), string> Parse(string text)
        {
            var values = new Dictionary<(string, string), string>();
            string section = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals > 0)
                    values[(section, line.Substring(0, equals).Trim().ToLowerInvariant())] = line.Substring(equals + 1).Trim();
            }
            return values;
        }

        // The image request settings for ``key``: OpenAI's address (or
        // ApiBaseUrl when testing), or the credits service's for a RegenerateFM
        // credits token. ArgumentException names a setting that isn't valid.
        public ImageSettings ForKey(string key)
        {
            var image = new ImageSettings
            {
                ApiKey = key ?? "",
                Model = (Model ?? "").Trim(),
                Quality = (Quality ?? "").Trim().ToLowerInvariant(),
                BaseUrl = Credits.IsToken(key) ? Credits.ApiBase(CheckedUrl(CreditsUrl, "ServiceUrl"))
                                               : CheckedUrl(ApiBaseUrl, "ApiBaseUrl"),
            };
            ImageApi.CheckSettings(image);
            return image;
        }

        // The credits service's address, checked like ForKey's.
        public string CreditsServiceUrl => CheckedUrl(CreditsUrl, "ServiceUrl");

        // An https address, or plain http on this computer only, so a key or
        // token is never sent unencrypted.
        private static string CheckedUrl(string value, string name)
        {
            string url = (value ?? "").Trim().TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" && uri.Host.Length > 0 ||
                 uri.Scheme == "http" && (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]")))
                return url;
            throw new ArgumentException(Text.T("{0} in {1} must be an https address, or http on this computer (127.0.0.1).", name, FileName));
        }
    }
}
