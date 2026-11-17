using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RegenerateFM.Tests
{
    // CheckedBuilds: the signed list of checked game builds the plugin
    // downloads (builds/ in the repository, made by tools/sign_builds.py).
    public class CheckedBuildsTests
    {
        private static string BuildsFolder => Path.Combine(AppContext.BaseDirectory, "builds");

        private const string Fm27Assembly = "1152df80de0ae35bff4945cb9196c58e5ec5b53de2138a006c0597522b558303";
        private const string Fm27Plugin = "8c6971ce0b1c8afe6529ea3f46205a5175aea988a97d7421a980da42e23fe340";

        private static string Listing(string issued, params (string Name, string Os, string Assembly, string Plugin, string UserData)[] builds) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["issued"] = issued,
                ["builds"] = builds.Select(build => new Dictionary<string, string>
                {
                    ["name"] = build.Name, ["os"] = build.Os, ["game_assembly"] = build.Assembly,
                    ["game_plugin"] = build.Plugin, ["user_data"] = build.UserData,
                }).ToList(),
            });

        private static (string Name, string Os, string Assembly, string Plugin, string UserData) Fm27(string assembly = Fm27Assembly) =>
            ("FM27 27.0.1 (Steam)", CheckedBuilds.Windows, assembly, Fm27Plugin, "Football Manager 27");

        private static byte[] Sign(string payloadJson, ECDsa key)
        {
            byte[] payload = Encoding.UTF8.GetBytes(payloadJson);
            byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(signature),
            }));
        }

        private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        // The list in the repository is builds.json, signed with the release key.
        [Fact]
        public void TheRepositorysListIsSignedWithTheReleaseKey()
        {
            byte[] signed = File.ReadAllBytes(Path.Combine(BuildsFolder, "regeneratefm-builds.json"));
            var list = CheckedBuilds.Read(signed);
            Assert.NotNull(list);
            using var document = JsonDocument.Parse(signed);
            Assert.Equal(File.ReadAllBytes(Path.Combine(BuildsFolder, "builds.json")),
                         Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()));
            using var plain = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BuildsFolder, "builds.json")));
            // None is left out as unusable.
            Assert.Equal(plain.RootElement.GetProperty("builds").GetArrayLength(), list.Builds.Count);
            var fm27 = list.Find(CheckedBuilds.Windows, Fm27Assembly.ToUpperInvariant(), Fm27Plugin);
            Assert.NotNull(fm27);
            Assert.Equal(27, fm27.Game.Number);
            Assert.Equal("Football Manager 27", fm27.Game.UserDataName);
            Assert.Null(list.Find(CheckedBuilds.MacOS, Fm27Assembly, Fm27Plugin));
            var demo = list.Builds.First(build => build.Game.Demo);
            Assert.Equal("Football Manager 26 Demo", demo.Game.UserDataName);
        }

        [Fact]
        public void AListSignedWithAnotherKeyIsRefused()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] signed = Sign(Listing("2026-11-17T12:00:00Z", Fm27()), key);
            Assert.NotNull(CheckedBuilds.Read(signed, PublicKey(key)));
            Assert.Null(CheckedBuilds.Read(signed));
        }

        [Fact]
        public void AChangedListIsRefused()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] signed = Sign(Listing("2026-11-17T12:00:00Z", Fm27()), key);
            using var document = JsonDocument.Parse(signed);
            string changed = Listing("2026-11-17T12:00:00Z", Fm27(), ("Mine", CheckedBuilds.Windows, new string('a', 64), new string('b', 64), "Football Manager 27"));
            byte[] tampered = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(changed)),
                ["signature"] = document.RootElement.GetProperty("signature").GetString(),
            }));
            Assert.Null(CheckedBuilds.Read(tampered, PublicKey(key)));
            Assert.Null(CheckedBuilds.Read(Encoding.UTF8.GetBytes("not json"), PublicKey(key)));
        }

        // A build the plugin can't use is left out; the rest still count.
        [Fact]
        public void AnUnusableBuildIsLeftOut()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var list = CheckedBuilds.Read(Sign(Listing("2026-11-17T12:00:00Z", Fm27(),
                ("Linux", "linux", Fm27Assembly, Fm27Plugin, "Football Manager 27"),
                ("Short hash", CheckedBuilds.Windows, "abc", Fm27Plugin, "Football Manager 27"),
                ("FM24", CheckedBuilds.Windows, Fm27Assembly, Fm27Plugin, "Football Manager 2024"),
                ("No folder", CheckedBuilds.Windows, Fm27Assembly, Fm27Plugin, null)), key), PublicKey(key));
            Assert.Equal("FM27 27.0.1 (Steam)", Assert.Single(list.Builds).Name);
            Assert.Equal(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc), list.Issued);
            Assert.Equal(DateTimeKind.Utc, list.Issued.Kind);
        }

        // Refresh keeps a newer list, and never goes back to an older one.
        [Fact]
        public async Task OnlyANewerSignedListReplacesTheKeptOne()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string folder = Path.Combine(Path.GetTempPath(), "regeneratefm-builds-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, CheckedBuilds.FileName);
            try
            {
                using var server = new OneFile();
                // Nothing kept yet.
                server.Body = Sign(Listing("2026-11-17T12:00:00Z", Fm27()), key);
                var first = await CheckedBuilds.Refresh(path, server.Url, PublicKey(key));
                Assert.Equal(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc), first.Issued);
                Assert.Equal(server.Body, File.ReadAllBytes(path));

                // A newer one, with another build.
                server.Body = Sign(Listing("2026-11-20T09:00:00Z", Fm27(), Fm27(new string('c', 64))), key);
                var second = await CheckedBuilds.Refresh(path, server.Url, PublicKey(key));
                Assert.Equal(2, second.Builds.Count);
                byte[] kept = File.ReadAllBytes(path);
                Assert.Equal(server.Body, kept);

                // An older one, sent again: refused, the kept one stays.
                server.Body = Sign(Listing("2026-11-17T12:00:00Z", Fm27()), key);
                Assert.Equal(2, (await CheckedBuilds.Refresh(path, server.Url, PublicKey(key))).Builds.Count);
                Assert.Equal(kept, File.ReadAllBytes(path));

                // A newer one with another key: refused.
                server.Body = Sign(Listing("2026-12-01T09:00:00Z"), other);
                Assert.Equal(2, (await CheckedBuilds.Refresh(path, server.Url, PublicKey(key))).Builds.Count);
                Assert.Equal(kept, File.ReadAllBytes(path));

                // The download failing: the kept one.
                server.Status = 404;
                Assert.Equal(2, (await CheckedBuilds.Refresh(path, server.Url, PublicKey(key))).Builds.Count);
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            // Nothing kept and nothing to download.
            Assert.Null(await CheckedBuilds.Refresh(path, "http://127.0.0.1:1/builds.json", PublicKey(key)));
            Assert.Null(CheckedBuilds.Cached(path));
        }

        // One file over HTTP, or an error status.
        private sealed class OneFile : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            public volatile byte[] Body = Array.Empty<byte>();
            public volatile int Status = 200;

            public OneFile()
            {
                _listener.Start();
                new Thread(Serve) { IsBackground = true }.Start();
            }

            public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/builds.json";

            private void Serve()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is InvalidOperationException)
                    {
                        return;
                    }
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var head = new List<byte>();
                            while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
                            {
                                int next = stream.ReadByte();
                                if (next < 0)
                                    break;
                                head.Add((byte)next);
                            }
                            byte[] body = Status == 200 ? Body : Array.Empty<byte>();
                            stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 " + Status + " X\r\nContent-Length: " + body.Length +
                                                                 "\r\nConnection: close\r\n\r\n"));
                            stream.Write(body);
                        }
                        catch (Exception error) when (error is IOException || error is SocketException) { }
                    }
                }
            }

            public void Dispose() => _listener.Stop();
        }
    }
}
