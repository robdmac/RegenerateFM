using System;
using System.Numerics;
using System.Security.Cryptography;

namespace RegenerateFM
{
    // ECDSA P-256 / SHA-256 signature checking in plain C#, for SelfUpdate.
    // .NET's ECDsa hands the work to the system's cryptography, and Wine's
    // (FM under Proton on Linux) doesn't implement ECDSA: it fails with
    // NTE_NOT_SUPPORTED (0x80090029). Only public values are involved (the
    // release key, the manifest and its signature), so nothing here needs to
    // be secret or constant-time. FIPS 186-4's curve and SEC 1's verification.
    public static class P256
    {
        private static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        private static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
        private static readonly BigInteger B = Hex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
        private static readonly Point G = new Point(Hex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"),
                                                    Hex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"));

        // A P-256 public key's SubjectPublicKeyInfo up to its uncompressed
        // point: SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1 }, BIT STRING }.
        private static readonly byte[] SpkiPrefix = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE");

        private sealed class Point
        {
            public readonly BigInteger X, Y;
            public Point(BigInteger x, BigInteger y) { X = x; Y = y; }
        }

        // Whether ``signature`` (DER, as openssl writes it) signs ``data`` for
        // ``subjectPublicKeyInfo`` (DER). Anything malformed is false.
        public static bool Verify(byte[] subjectPublicKeyInfo, byte[] data, byte[] signature)
        {
            var q = PublicKey(subjectPublicKeyInfo);
            if (q == null || !ReadSignature(signature, out var r, out var s))
                return false;
            if (r <= 0 || r >= N || s <= 0 || s >= N)
                return false;
            var e = Unsigned(SHA256.HashData(data));
            var w = BigInteger.ModPow(s, N - 2, N);
            var point = Add(Multiply(G, e * w % N), Multiply(q, r * w % N));
            return point != null && point.X % N == r;
        }

        private static Point PublicKey(byte[] spki)
        {
            if (spki == null || spki.Length != SpkiPrefix.Length + 64)
                return null;
            for (int i = 0; i < SpkiPrefix.Length; i++)
                if (spki[i] != SpkiPrefix[i])
                    return null;
            var x = Unsigned(spki.AsSpan(SpkiPrefix.Length, 32));
            var y = Unsigned(spki.AsSpan(SpkiPrefix.Length + 32, 32));
            if (x >= P || y >= P || Mod(y * y - (x * x * x - 3 * x + B)) != 0)
                return null; // not on the curve
            return new Point(x, y);
        }

        // SEQUENCE { INTEGER r, INTEGER s }, with short lengths (a P-256
        // signature is at most 72 bytes) and nothing after it.
        private static bool ReadSignature(byte[] der, out BigInteger r, out BigInteger s)
        {
            r = s = 0;
            if (der == null || der.Length < 8 || der[0] != 0x30 || der[1] != der.Length - 2 || der[1] >= 0x80)
                return false;
            int at = 2;
            return ReadInteger(der, ref at, out r) && ReadInteger(der, ref at, out s) && at == der.Length;
        }

        private static bool ReadInteger(byte[] der, ref int at, out BigInteger value)
        {
            value = 0;
            if (at + 2 > der.Length || der[at] != 0x02)
                return false;
            int length = der[at + 1];
            if (length == 0 || length > 33 || at + 2 + length > der.Length)
                return false;
            var bytes = der.AsSpan(at + 2, length);
            if ((bytes[0] & 0x80) != 0)
                return false; // negative
            if (length > 1 && bytes[0] == 0 && (bytes[1] & 0x80) == 0)
                return false; // not minimal
            value = Unsigned(bytes);
            at += 2 + length;
            return true;
        }

        // Affine arithmetic; null is the point at infinity.
        private static Point Add(Point a, Point b)
        {
            if (a == null) return b;
            if (b == null) return a;
            BigInteger slope;
            if (a.X == b.X)
            {
                if (Mod(a.Y + b.Y) == 0)
                    return null;
                slope = Mod(3 * (a.X * a.X - 1) * Inverse(2 * a.Y));
            }
            else
                slope = Mod((b.Y - a.Y) * Inverse(b.X - a.X));
            var x = Mod(slope * slope - a.X - b.X);
            return new Point(x, Mod(slope * (a.X - x) - a.Y));
        }

        private static Point Multiply(Point point, BigInteger k)
        {
            Point result = null;
            for (int bit = (int)k.GetBitLength() - 1; bit >= 0; bit--)
            {
                result = Add(result, result);
                if (!(k >> bit).IsEven)
                    result = Add(result, point);
            }
            return result;
        }

        private static BigInteger Mod(BigInteger value)
        {
            var m = value % P;
            return m.Sign < 0 ? m + P : m;
        }

        private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

        private static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) => new BigInteger(bigEndian, isUnsigned: true, isBigEndian: true);

        private static BigInteger Hex(string hex) => Unsigned(Convert.FromHexString(hex));
    }
}
