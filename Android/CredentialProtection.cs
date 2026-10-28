using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Security.Cryptography;

namespace ArrisControl;

internal static class CredentialProtection
{
    public const string Description = "Android Keystore AES-256-GCM (this app installation)";
    const string Alias = "ArrisControl.Credentials.v1";
    static readonly object Gate = new();
    static IKey Key()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (!store.ContainsAlias(Alias))
        {
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            using var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm!)!.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)!
                .SetKeySize(256)!.SetRandomizedEncryptionRequired(true)!.Build();
            generator.Init(spec); generator.GenerateKey();
        }
        return store.GetKey(Alias, null)!;
    }
    public static byte[] Protect(byte[] data, byte[]? entropy)
    {
        lock (Gate)
        {
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                using var key = Key(); cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
                if (entropy is not null) cipher.UpdateAAD(entropy);
                var nonce = cipher.GetIV()!; var encrypted = cipher.DoFinal(data)!;
                return new byte[] { 1, (byte)nonce.Length }.Concat(nonce).Concat(encrypted).ToArray();
            }
            catch (Java.Lang.Exception) { throw new CryptographicException("Android Keystore could not encrypt this credential."); }
        }
    }
    public static byte[] Unprotect(byte[] data, byte[]? entropy)
    {
        lock (Gate)
        {
            if (data.Length < 30 || data[0] != 1 || data[1] != 12) throw new CryptographicException("Not an Android credential envelope.");
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                using var key = Key(); using var spec = new GCMParameterSpec(128, data[2..14]);
                cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, spec);
                if (entropy is not null) cipher.UpdateAAD(entropy);
                return cipher.DoFinal(data[14..])!;
            }
            catch (Java.Lang.Exception) { throw new CryptographicException("The credential cannot be decrypted by this installation."); }
        }
    }
}
