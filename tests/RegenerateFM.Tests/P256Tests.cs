using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RegenerateFM.Tests
{
    // P256, SelfUpdate's signature check in plain C# (Wine has no ECDSA),
    // against .NET's own ECDSA and openssl's signatures.
    public class P256Tests
    {
        private static byte[] Sign(ECDsa key, byte[] data) =>
            key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        [Fact]
        public void ItAgreesWithDotNetsEcdsa()
        {
            var random = new Random(4530370);
            for (int i = 0; i < 40; i++)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                byte[] spki = key.ExportSubjectPublicKeyInfo();
                var data = new byte[random.Next(0, 300)];
                random.NextBytes(data);
                byte[] signature = Sign(key, data);
                Assert.True(P256.Verify(spki, data, signature));

                var changed = (byte[])data.Clone();
                if (changed.Length == 0) changed = new byte[] { 1 }; else changed[random.Next(changed.Length)] ^= 0x01;
                Assert.False(P256.Verify(spki, changed, signature));

                using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Assert.False(P256.Verify(other.ExportSubjectPublicKeyInfo(), data, signature));
            }
        }

        [Fact]
        public void TheReleasesOwnSignatureChecksOut()
        {
            // The fixture make_release.py made and openssl signed with the release key.
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "self-update-9.9.9.json")));
            byte[] payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString());
            byte[] signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString());
            byte[] releaseKey = Convert.FromBase64String((string)typeof(SelfUpdate)
                .GetField("ReleaseKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null));
            Assert.True(P256.Verify(releaseKey, payload, signature));
            Assert.False(P256.Verify(releaseKey, payload.Concat(new byte[] { 32 }).ToArray(), signature));
        }

        [Fact]
        public void AnythingMalformedIsRefused()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            byte[] data = Encoding.UTF8.GetBytes("RegenerateFM");
            byte[] signature = Sign(key, data);

            Assert.False(P256.Verify(spki, data, null));
            Assert.False(P256.Verify(spki, data, Array.Empty<byte>()));
            Assert.False(P256.Verify(spki, data, signature.Concat(new byte[] { 0 }).ToArray()));    // trailing byte
            Assert.False(P256.Verify(spki, data, signature.Take(signature.Length - 1).ToArray())); // cut short
            Assert.False(P256.Verify(null, data, signature));
            Assert.False(P256.Verify(spki.Take(spki.Length - 1).ToArray(), data, signature));

            // A point off the curve.
            var offCurve = (byte[])spki.Clone();
            offCurve[^1] ^= 0x01;
            Assert.False(P256.Verify(offCurve, data, signature));

            // Another curve's key.
            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            Assert.False(P256.Verify(p384.ExportSubjectPublicKeyInfo(), data, signature));

            // r = 0 and s = 0.
            Assert.False(P256.Verify(spki, data, new byte[] { 0x30, 0x06, 0x02, 0x01, 0x00, 0x02, 0x01, 0x00 }));
            // r and s as n (out of range).
            byte[] n = Convert.FromHexString("00ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
            var big = new byte[] { 0x30, 0x46, 0x02, 0x21 }.Concat(n).Concat(new byte[] { 0x02, 0x21 }).Concat(n).ToArray();
            Assert.False(P256.Verify(spki, data, big));
        }

        [Fact]
        public void AHighSSignatureIsAlsoAccepted()
        {
            // ECDSA allows either s or n - s (openssl and .NET don't normalise),
            // so both must check out, as they do with .NET's ECDsa.
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] data = Encoding.UTF8.GetBytes("1.0.5");
            byte[] p1363 = key.SignData(data, HashAlgorithmName.SHA256); // r || s, 32 bytes each
            var n = new System.Numerics.BigInteger(Convert.FromHexString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551"), true, true);
            var r = new System.Numerics.BigInteger(p1363.AsSpan(0, 32), true, true);
            var s = new System.Numerics.BigInteger(p1363.AsSpan(32), true, true);
            var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            writer.PushSequence();
            writer.WriteInteger(r);
            writer.WriteInteger(n - s);
            writer.PopSequence();
            byte[] flipped = writer.Encode();
            Assert.True(key.VerifyData(data, flipped, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.True(P256.Verify(key.ExportSubjectPublicKeyInfo(), data, flipped));
        }
    }
}
