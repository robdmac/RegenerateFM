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
    public sealed class ImageSettings
    {
        public string ApiKey;
        public string Model = "gpt-image-2.5-sunburst";
        public string Quality = "medium";
        public string BaseUrl = "https://api.openai.com/v1";
    }

    // OpenAI's image API (or the RegenerateFM credits service, which speaks it).
    public static class ImageApi
    {
        public const int PortraitSize = 256;
        public const int MaxReference = 8 << 20;
        public static readonly string[] Qualities = { "low", "medium", "high", "xhigh", "max", "auto" };

        private static readonly HttpClient Client = NewClient(TimeSpan.FromSeconds(180));

        // What CheckKey says when OpenAI, or the credits service, refuses the key.
        public static string KeyRejected => Text.T("OpenAI didn't accept this key. Check that you copied all of it.");
        public static string SignedOut => Text.T("This RegenerateFM sign-in isn't valid any more. Choose Buy credits / Sign in.");

        // Cloudflare, in front of the credits service, turns away some programs'
        // default user agents, so every request names RegenerateFM.
        internal static HttpClient NewClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RegenerateFM");
            return client;
        }

        public static void CheckSettings(ImageSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new MissingApiKeyException();
            if (Array.IndexOf(Qualities, settings.Quality) < 0)
                throw new ArgumentException(Text.T("Quality in {0} must be low, medium, high, xhigh, max, or auto.", Settings.FileName));
            if (string.IsNullOrWhiteSpace(settings.Model))
                throw new ArgumentException(Text.T("Model in {0} is empty.", Settings.FileName));
        }

        // Asks OpenAI whether the key works, with a free request that lists
        // models. Returns null when it does, else a message for the player.
        public static TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

        public static async Task<string> CheckKey(ImageSettings settings, string key, CancellationToken cancel = default)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl.TrimEnd('/') + "/models"))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                // A quick check, not the image requests' long wait.
                timeout.CancelAfter(CheckTimeout);
                try
                {
                    using (var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                            return null;
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            return Credits.IsToken(key)
                                ? SignedOut
                                : KeyRejected;
                        return Text.T("OpenAI couldn't check the key (HTTP {0}). Try again.", (int)response.StatusCode);
                    }
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
                {
                    return Text.T("Could not reach OpenAI to check the key: {0}", error.Message.Replace(key, "[REDACTED]"));
                }
            }
        }

        private static List<KeyValuePair<string, string>> Fields(ImageSettings settings, string prompt)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("model", settings.Model),
                new KeyValuePair<string, string>("size", "1024x1024"),
                new KeyValuePair<string, string>("quality", settings.Quality),
                new KeyValuePair<string, string>("background", "transparent"),
                new KeyValuePair<string, string>("output_format", "png"),
                new KeyValuePair<string, string>("prompt", prompt),
            };
        }

        public static Task<byte[]> GeneratePng(ImageSettings settings, string prompt, CancellationToken cancel = default)
        {
            CheckSettings(settings);
            var fields = Fields(settings, prompt);
            var content = new StringContent(Json.Text(json =>
            {
                json.WriteStartObject();
                foreach (var field in fields)
                    json.WriteString(field.Key, field.Value);
                json.WriteEndObject();
            }), Encoding.UTF8, "application/json");
            return ImageRequest(settings, "generations", content, cancel);
        }

        public static Task<byte[]> EditPng(ImageSettings settings, string prompt, byte[] reference, CancellationToken cancel = default)
        {
            if (!Png.IsPng(reference))
                throw new ArgumentException("The existing portrait must be a PNG image.");
            if (reference.Length > MaxReference)
                throw new ArgumentException("The existing portrait is too large to send.");
            CheckSettings(settings);
            var content = new MultipartFormDataContent("regeneratefm-" + Guid.NewGuid().ToString("N"));
            foreach (var field in Fields(settings, prompt))
                content.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
            var image = new ByteArrayContent(reference);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(image, "image[]", "portrait.png");
            return ImageRequest(settings, "edits", content, cancel);
        }

        private static async Task<byte[]> ImageRequest(ImageSettings settings, string endpoint, HttpContent content, CancellationToken cancel)
        {
            string key = settings.ApiKey.Trim();
            using (content)
            using (var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/images/" + endpoint) { Content = content })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, cancel).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
                {
                    throw new InvalidOperationException(Text.T("Could not reach the OpenAI image API: {0}", error.Message.Replace(key, "[REDACTED]")));
                }
                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = "";
                        try
                        {
                            using (var json = JsonDocument.Parse(body))
                                detail = json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
                        }
                        catch (Exception) { }
                        if (detail.Length == 0)
                            detail = response.ReasonPhrase ?? "";
                        // RegenerateFM credits' messages are written for the player already.
                        string message = Credits.IsToken(key)
                            ? detail.Replace(key, "[REDACTED]")
                            : Text.T("OpenAI image API returned HTTP {0}: {1}", (int)response.StatusCode, detail.Replace(key, "[REDACTED]"));
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            throw new ApiKeyRejectedException(message);
                        if ((int)response.StatusCode == 402)
                            throw new OutOfCreditsException(message);
                        throw new InvalidOperationException(message);
                    }
                    string image;
                    try
                    {
                        using (var json = JsonDocument.Parse(body))
                            image = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
                    }
                    catch (Exception)
                    {
                        throw new InvalidOperationException("Image API response did not contain an image.");
                    }
                    byte[] png;
                    try
                    {
                        png = Convert.FromBase64String(image ?? "");
                    }
                    catch (FormatException)
                    {
                        throw new InvalidOperationException("Image API returned invalid image data.");
                    }
                    if (png.Length == 0)
                        throw new InvalidOperationException("Image API response did not contain an image.");
                    if (!Png.IsPng(png))
                        throw new InvalidOperationException("Image API returned data that is not a PNG.");
                    return FitPortrait(png);
                }
            }
        }

        // FM draws portraits at 256×256; the image API's smallest square output
        // is 1024×1024, so faces are scaled down, keeping transparency.
        public static byte[] FitPortrait(byte[] png)
        {
            Png.Image image;
            try
            {
                image = Png.Decode(png);
            }
            catch (Exception error) when (error is InvalidDataException || error is NotSupportedException)
            {
                throw new InvalidOperationException("Image API returned a PNG that could not be read: " + error.Message);
            }
            if (image.Width == PortraitSize && image.Height == PortraitSize)
                return png;
            var scaled = Png.Resize(image, PortraitSize, PortraitSize);
            return Png.Encode(scaled.Rgba, scaled.Width, scaled.Height, bottomUp: false);
        }
    }

    public sealed class MissingApiKeyException : ArgumentException
    {
        public MissingApiKeyException() : base(Text.T("No OpenAI API key is saved yet.")) { }
    }

    public sealed class ApiKeyRejectedException : InvalidOperationException
    {
        public ApiKeyRejectedException(string message) : base(message) { }
    }

    // RegenerateFM credits ran out (HTTP 402 from the credits service).
    public sealed class OutOfCreditsException : InvalidOperationException
    {
        public OutOfCreditsException(string message) : base(message) { }
    }
}
