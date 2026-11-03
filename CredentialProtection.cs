using System.Security.Cryptography;

namespace ArrisControl;

// Android provides a hardware/OS backed implementation in Android/CredentialProtection.cs.
#if !ANDROID && !PORTABLE_TEST
internal static class CredentialProtection
{
    public const string Description = "Windows DPAPI CurrentUser";
    public static byte[] Protect(byte[] data, byte[]? entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data, byte[]? entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
}
#endif
