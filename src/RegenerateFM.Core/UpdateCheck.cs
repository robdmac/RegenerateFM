using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RegenerateFM
{
    // Whether a newer release is out, asked of GitHub once a session. It only
    // tells the player: the game has to be closed to replace RegenerateFM.
    public static class UpdateCheck
    {
        public const string ReleasesPage = "https://github.com/robdmac/RegenerateFM/releases/latest";
        private const string LatestRelease = "https://api.github.com/repos/robdmac/RegenerateFM/releases/latest";

        private static readonly HttpClient Client = ImageApi.NewClient(TimeSpan.FromSeconds(20));

        // Directory.Build.props' version.
        private static Version Current => typeof(UpdateCheck).Assembly.GetName().Version;

        // The newer release's version ("1.0.2"); null when this is the latest,
        // or GitHub can't be asked, which nobody needs to hear about.
        public static async Task<string> NewerRelease()
        {
            try
            {
                return Newer(await Client.GetStringAsync(LatestRelease).ConfigureAwait(false), Current);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                return null;
            }
        }

        // The version in GitHub's answer for its latest release, if above ``current``.
        public static string Newer(string json, Version current)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                string tag = document.RootElement.GetProperty("tag_name").GetString();
                string version = tag?.TrimStart('v');
                return Version.TryParse(version, out var latest) && Numbered(latest) > Numbered(current) ? version : null;
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException ||
                                          error is System.Collections.Generic.KeyNotFoundException)
            {
                return null;
            }
        }

        // Three numbers: a tag has three, and an assembly's version four.
        private static Version Numbered(Version version) => new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
