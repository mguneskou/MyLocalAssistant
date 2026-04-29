using System.Security.Cryptography;

namespace MyLocalAssistant.Core.Download;

public static class Sha256Verifier
{
    public static async Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> VerifyAsync(string filePath, string expectedHex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true; // no hash provided -> skip
        var actual = await ComputeAsync(filePath, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }
}
