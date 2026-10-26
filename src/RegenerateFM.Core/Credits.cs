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
    // RegenerateFM credits (the regeneratefm-credits Worker): a token bought with credits
    // instead of an OpenAI key. The game gets one by device sign-in: it starts
    // a sign-in, opens the browser at the returned link, and polls until the
    // player has signed in and confirmed the code, then saves the token where
    // an OpenAI key goes. Image requests with the token go to the service,
    // which speaks OpenAI's image API.
    public static class Credits
    {
        public const string Prefix = "rfm_";
        public const string DefaultServiceUrl = "https://credits.regenerate.fm";
        public const string BuyText =
            "Buy RegenerateFM credits: one credit makes or updates one face. Packs go from 75 credits for " +
            "£2 / €2.50 / $2.50 to 1000 for £20 / €24 / $24. You sign in with your email in your browser; " +
            "there's nothing to paste.";

        public static bool IsToken(string key) => key != null && key.StartsWith(Prefix, StringComparison.Ordinal);

        // The image API address for a key: the credits service's for a token.
        public static string ApiBase(string serviceUrl) => serviceUrl.TrimEnd('/') + "/v1";

        public sealed class SignIn
        {
            public string DeviceCode;
            public string UserCode;
            public string Link;
            public int Interval;
            public System.DateTime ExpiresUtc;
        }

        public sealed class Poll
        {
            public string Status;   // pending, slow_down, approved, expired
            public string Token;
            public string Email;
            public int Credits;
        }

        private static readonly HttpClient Client = ImageApi.NewClient(TimeSpan.FromSeconds(20));

        // How Wait waits between polls (replaced by tests).
        public static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;

        private static async Task<HttpResponseMessage> Post(string url, string json)
        {
            try
            {
                return await Client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException)
            {
                throw new InvalidOperationException("Couldn't reach RegenerateFM credits: " + error.Message);
            }
        }

        public static async Task<SignIn> Start(string serviceUrl)
        {
            using (var response = await Post(serviceUrl.TrimEnd('/') + "/device/start", "{}").ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("RegenerateFM credits couldn't start a sign-in (HTTP " + (int)response.StatusCode + ").");
                SignIn signIn;
                try
                {
                    using (var json = JsonDocument.Parse(body))
                    {
                        var root = json.RootElement;
                        signIn = new SignIn
                        {
                            DeviceCode = root.GetProperty("device_code").GetString(),
                            UserCode = root.GetProperty("user_code").GetString(),
                            Link = root.GetProperty("verification_uri_complete").GetString(),
                            Interval = Math.Max(2, Whole(root.GetProperty("interval"))),
                            ExpiresUtc = System.DateTime.UtcNow.AddSeconds(Whole(root.GetProperty("expires_in")))
                        };
                    }
                }
                catch (Exception error) when (Malformed(error))
                {
                    throw new InvalidOperationException(Unexpected);
                }
                if (signIn.DeviceCode == null || signIn.UserCode == null)
                    throw new InvalidOperationException(Unexpected);
                // Only the service's own pages are opened in the browser.
                if (signIn.Link == null || !signIn.Link.StartsWith(serviceUrl.TrimEnd('/') + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("RegenerateFM credits returned an unexpected sign-in link.");
                return signIn;
            }
        }

        private const string Unexpected = "RegenerateFM credits sent an unexpected answer.";

        // An answer that isn't the JSON the service sends.
        private static bool Malformed(Exception error) =>
            error is JsonException || error is KeyNotFoundException || error is InvalidOperationException ||
            error is FormatException || error is OverflowException || error is ArgumentException;

        // A JSON number as a whole number (5.0 included).
        private static int Whole(JsonElement value)
        {
            double number = value.GetDouble();
            if (double.IsNaN(number) || number < int.MinValue || number > int.MaxValue)
                throw new FormatException();
            return (int)number;
        }

        public static async Task<Poll> Check(string serviceUrl, string deviceCode)
        {
            string request = Json.Text(json =>
            {
                json.WriteStartObject();
                json.WriteString("device_code", deviceCode);
                json.WriteEndObject();
            });
            using (var response = await Post(serviceUrl.TrimEnd('/') + "/device/poll", request).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 410)
                    return new Poll { Status = "expired" };
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("RegenerateFM credits answered HTTP " + (int)response.StatusCode + ".");
                Poll poll;
                try
                {
                    using (var json = JsonDocument.Parse(body))
                    {
                        var root = json.RootElement;
                        poll = new Poll { Status = root.GetProperty("status").GetString() };
                        if (poll.Status == "approved")
                        {
                            poll.Token = root.GetProperty("token").GetString();
                            poll.Email = root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                                ? email.GetString() : "";
                            poll.Credits = root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Number
                                ? Whole(credits) : 0;
                        }
                    }
                }
                catch (Exception error) when (Malformed(error))
                {
                    throw new InvalidOperationException(Unexpected);
                }
                if (poll.Status != "pending" && poll.Status != "slow_down" && poll.Status != "approved" && poll.Status != "expired")
                    throw new InvalidOperationException(Unexpected);
                if (poll.Status == "approved" && !IsToken(poll.Token))
                    throw new InvalidOperationException(Unexpected);
                return poll;
            }
        }

        // Polls until the player has signed in: the approved answer, or null if
        // the sign-in expired or was cancelled. The FM26 plugin polls from its
        // frames instead.
        public static async Task<Poll> Wait(string serviceUrl, SignIn signIn, CancellationToken cancel = default)
        {
            int interval = signIn.Interval;
            while (System.DateTime.UtcNow < signIn.ExpiresUtc && !cancel.IsCancellationRequested)
            {
                try
                {
                    await Delay(TimeSpan.FromSeconds(interval), cancel).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return null;
                }
                var poll = await Check(serviceUrl, signIn.DeviceCode).ConfigureAwait(false);
                if (poll.Status == "approved")
                    return poll;
                if (poll.Status == "expired")
                    return null;
                if (poll.Status == "slow_down")
                    interval += 2;
            }
            return null;
        }
    }
}
