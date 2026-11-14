using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RegenerateFM.Tests
{
    // SelfUpdate: the signed manifest, which BepInEx it runs on, and swapping
    // the plugin in beside the running one.
    public class SelfUpdateTests
    {
        // A manifest made by tools/make_release.py, signed by openssl with the
        // release key whose public half SelfUpdate checks against.
        private static byte[] ReleaseSigned() =>
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "self-update-9.9.9.json"));

        private static (byte[] Signed, string PublicKey) Sign(string payloadJson, ECDsa key = null)
        {
            using var made = key == null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
            var signer = key ?? made;
            byte[] payload = Encoding.UTF8.GetBytes(payloadJson);
            byte[] signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            string signed = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(signature),
            });
            return (Encoding.UTF8.GetBytes(signed), Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        }

        private const string Payload =
            "{\"version\":\"1.0.3\",\"bepinex\":[\"6.0.0-be.760\",\"6.0.0-be.789\"]," +
            "\"files\":[{\"path\":\"plugins/RegenerateFM/RegenerateFM.dll\",\"asset\":\"RegenerateFM.dll\"," +
            "\"sha256\":\"d8ac59f06671a5738c5b493c432fef3659231a61faef954eb3d31b219b5bf313\"}]}";

        [Fact]
        public void TheReleaseKeyChecksOpensslsSignature()
        {
            var manifest = SelfUpdate.Verify(ReleaseSigned());
            Assert.Equal("9.9.9", manifest.Version);
            Assert.Equal(new[] { "6.0.0-be.760", "6.0.0-be.789" }, manifest.BepInEx);
            var file = Assert.Single(manifest.Files);
            Assert.Equal(SelfUpdate.PluginPath, file.Path);
            Assert.Equal("RegenerateFM.dll", file.Asset);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        }

        [Fact]
        public void AManifestSignedWithAnotherKeyIsRefused()
        {
            var (signed, _) = Sign(Payload);
            var refused = Assert.Throws<SelfUpdate.UpdateException>(() => SelfUpdate.Verify(signed));
            Assert.Contains("release key", refused.Message);
        }

        [Fact]
        public void AChangedManifestIsRefused()
        {
            var (signed, publicKey) = Sign(Payload);
            Assert.Equal("1.0.3", SelfUpdate.Verify(signed, publicKey).Version);
            using var document = JsonDocument.Parse(signed);
            string changed = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload.Replace("1.0.3", "1.0.4"))),
                ["signature"] = document.RootElement.GetProperty("signature").GetString(),
            });
            Assert.Throws<SelfUpdate.UpdateException>(() => SelfUpdate.Verify(Encoding.UTF8.GetBytes(changed), publicKey));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("{\"payload\":\"!!\",\"signature\":\"AA==\"}")]
        public void AManifestThatCantBeReadIsRefused(string json)
        {
            Assert.Throws<SelfUpdate.UpdateException>(() => SelfUpdate.Verify(Encoding.UTF8.GetBytes(json)));
        }

        [Theory]
        [InlineData("../BepInEx.Core.dll")]
        [InlineData("plugins/../../winhttp.dll")]
        [InlineData("/etc/passwd")]
        [InlineData("C:/Windows/evil.dll")]
        [InlineData("plugins\\\\RegenerateFM\\\\RegenerateFM.dll")]
        [InlineData("")]
        public void AFileOutsideBepInExIsRefused(string path)
        {
            var (signed, publicKey) = Sign(Payload.Replace("plugins/RegenerateFM/RegenerateFM.dll", path));
            Assert.Throws<SelfUpdate.UpdateException>(() => SelfUpdate.Verify(signed, publicKey));
        }

        [Theory]
        [InlineData("6.0.0-be.760+a1afbfb169610ffbc1813d83e946c9e2cc0d2d63", true)]
        [InlineData("6.0.0-be.789+6cea286", true)]
        [InlineData("6.0.0-be.760", true)]
        [InlineData("6.0.0-be.770+abc", false)]
        [InlineData("6.0.0-be.76", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ItRunsOnlyOnTheBepInExItWasMadeFor(string running, bool runs)
        {
            var (signed, publicKey) = Sign(Payload);
            Assert.Equal(runs, SelfUpdate.RunsOn(SelfUpdate.Verify(signed, publicKey), running));
        }

        [Fact]
        public void ThePluginIsSwappedInAndTheOldOneClearedLater()
        {
            string folder = Directory.CreateTempSubdirectory("regeneratefm-update").FullName;
            try
            {
                string plugin = Path.Combine(folder, "RegenerateFM.dll");
                File.WriteAllText(plugin, "old");
                SelfUpdate.Replace(new[] { (plugin, Encoding.UTF8.GetBytes("new")) });
                Assert.Equal("new", File.ReadAllText(plugin));
                Assert.Equal("old", File.ReadAllText(plugin + SelfUpdate.OldSuffix));
                SelfUpdate.CleanUp(folder);
                Assert.Equal(new[] { plugin }, Directory.GetFiles(folder));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void AFailedSwapPutsEveryFileBack()
        {
            string folder = Directory.CreateTempSubdirectory("regeneratefm-update").FullName;
            try
            {
                string plugin = Path.Combine(folder, "RegenerateFM.dll");
                File.WriteAllText(plugin, "old");
                // A folder where the second file's new copy should go: the
                // swap can't make it, so the first is undone.
                string blocked = Path.Combine(folder, "blocked.dll");
                Directory.CreateDirectory(blocked + ".regeneratefm-new");
                Assert.Throws<SelfUpdate.UpdateException>(() => SelfUpdate.Replace(new[]
                {
                    (plugin, Encoding.UTF8.GetBytes("new")),
                    (blocked, Encoding.UTF8.GetBytes("new")),
                }));
                Assert.Equal("old", File.ReadAllText(plugin));
                Assert.False(File.Exists(plugin + SelfUpdate.OldSuffix));
                Assert.False(File.Exists(plugin + ".regeneratefm-new"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
