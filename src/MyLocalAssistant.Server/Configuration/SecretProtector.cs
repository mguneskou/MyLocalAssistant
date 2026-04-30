using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MyLocalAssistant.Server.Configuration;

/// <summary>
/// Encrypts short secrets (cloud API keys, etc.) with Windows DPAPI under the
/// machine scope so the server account can read them but other users on the
/// same box can't. Falls back to a plain marker on non-Windows hosts so the
/// config file remains portable for tests.
/// </summary>
internal static class SecretProtector
{
    private const string ProtectedPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    // Application-scoped entropy so a stolen settings file can't be decrypted on
    // another machine merely because it shares the same DPAPI key.
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("MyLocalAssistant.Server.CloudKeys.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PlainPrefix + plaintext;
        var data = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(data, s_entropy, DataProtectionScope.LocalMachine);
        return ProtectedPrefix + Convert.ToBase64String(enc);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored.Substring(PlainPrefix.Length);
        if (stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            try
            {
                var bytes = Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length));
                var dec = ProtectedData.Unprotect(bytes, s_entropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(dec);
            }
            catch (CryptographicException) { return null; }
        }
        // Legacy/manual edits: treat as already-plain.
        return stored;
    }
}
