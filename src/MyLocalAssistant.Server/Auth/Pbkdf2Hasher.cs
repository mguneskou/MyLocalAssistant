using System.Security.Cryptography;

namespace MyLocalAssistant.Server.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashing. Output format:
///   pbkdf2$sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;
/// New hashes use <see cref="DefaultIterations"/> (OWASP 2025 guidance for PBKDF2-SHA256).
/// <see cref="Verify"/> honours whatever iteration count is stored in the hash, so
/// hashes created by earlier versions (e.g. 210 000 iter) continue to verify
/// transparently. No migration step required.
/// </summary>
public static class Pbkdf2Hasher
{
    /// <summary>
    /// OWASP 2025 guidance for PBKDF2-SHA256. Old hashes stored with fewer iterations
    /// still verify because the iteration count is baked into the stored string.
    /// </summary>
    private const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2$sha256${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256") return false;
        if (!int.TryParse(parts[2], out var iters)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
