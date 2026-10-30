using System.Security.Cryptography;
using ArrisControl;

if (args is ["--probe", var address])
{
    var results = await RouterDiscovery.DiscoverApisAsync([address], null, CancellationToken.None);
    foreach (var endpoint in results) Console.WriteLine($"ARRIS API responds: {endpoint.Address}:50011 · {endpoint.Status}");
    if (results.Count != 1) throw new InvalidOperationException("No matching ARRIS API responded.");
    return;
}

if (args is ["--export-protocol", var reference, var output])
{
    // For personal builds only: writes the SURFboard reference as plaintext JSON.
    // Keep the output private; Android/protocol.local.json is gitignored and CI refuses to build with it.
    File.WriteAllText(output, CloudProtocol.ToJson(CloudProtocol.ReadReference(reference)));
    Console.WriteLine($"Wrote {output}. Keep this file private; never commit or share it.");
    return;
}

await SelfTests.RunAsync();
// Exercise authenticated envelope rejection independently of a router/cloud.
var cipher = CredentialProtection.Protect([1, 2, 3], [4]);
cipher[^1] ^= 1;
try { CredentialProtection.Unprotect(cipher, [4]); throw new Exception("Tampered credential was accepted."); }
catch (CryptographicException) { }
Console.WriteLine("PASS: shared mobile protocol/settings tests and authenticated envelope tamper rejection.");

namespace ArrisControl
{
    // In-memory fictional key ONLY in the offline test executable. Android uses its Keystore.
    internal static class CredentialProtection
    {
        public const string Description = "Fictional in-memory test key";
        static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        public static byte[] Protect(byte[] data, byte[]? entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var encrypted = new byte[data.Length];
            using var aes = new AesGcm(Key, 16); aes.Encrypt(nonce, data, encrypted, tag, entropy);
            return nonce.Concat(tag).Concat(encrypted).ToArray();
        }
        public static byte[] Unprotect(byte[] data, byte[]? entropy)
        {
            var plain = new byte[data.Length - 28]; using var aes = new AesGcm(Key, 16);
            aes.Decrypt(data.AsSpan(0,12), data.AsSpan(28), data.AsSpan(12,16), plain, entropy); return plain;
        }
    }
}
