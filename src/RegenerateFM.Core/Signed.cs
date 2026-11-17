using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RegenerateFM
{
    // A file signed with the release key: {"payload": base64 of the bytes
    // signed, "signature": base64 of an ECDSA P-256 / SHA-256 signature of
    // them, as openssl writes it}. The self-update's manifest (SelfUpdate) and
    // the list of checked game builds (CheckedBuilds) are both in this form.
    public static class Signed
    {
        public enum Problem { None, Unreadable, NotSigned }

        // The payload, if the file is signed with ``publicKey`` (base64
        // SubjectPublicKeyInfo); otherwise null, and why.
        public static byte[] Payload(byte[] signedJson, string publicKey, out Problem problem)
        {
            byte[] payload, signature;
            try
            {
                using var document = JsonDocument.Parse(signedJson);
                payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString() ?? "");
                signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString() ?? "");
            }
            catch (Exception error) when (error is JsonException || error is FormatException || error is KeyNotFoundException ||
                                          error is InvalidOperationException || error is ArgumentException)
            {
                problem = Problem.Unreadable;
                return null;
            }
            // Checked in plain C# (P256), not by .NET's ECDsa: Wine, which runs FM
            // under Proton on Linux, doesn't implement ECDSA.
            if (!P256.Verify(Convert.FromBase64String(publicKey), payload, signature))
            {
                problem = Problem.NotSigned;
                return null;
            }
            problem = Problem.None;
            return payload;
        }
    }
}
