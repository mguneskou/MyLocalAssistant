using DocumentFormat.OpenXml.Packaging;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

internal static class OfficeToolSupport
{
    private const long EmusPerInch = 914400L;
    private const int TwipsPerInch = 1440;

    public static string ResolveWorkFile(string workDirectory, string relativePath, string defaultExtension)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(defaultExtension) && !Path.HasExtension(normalized))
            normalized += defaultExtension;

        var root = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(root);

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsWithinRoot(root, full))
            throw new ArgumentException("Path must stay within the work directory.", nameof(relativePath));

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return full;
    }

    public static string ResolveExistingWorkAsset(string workDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(root);

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsWithinRoot(root, full))
            throw new ArgumentException("Path must stay within the work directory.", nameof(relativePath));

        return full;
    }

    public static bool TryGetImagePartType(string filePath, out ImagePartType imagePartType)
    {
        imagePartType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => ImagePartType.Png,
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            ".gif" => ImagePartType.Gif,
            ".bmp" => ImagePartType.Bmp,
            ".tif" or ".tiff" => ImagePartType.Tiff,
            _ => default,
        };
        return imagePartType != default;
    }

    public static long InchesToEmu(double inches)
        => (long)Math.Round(inches * EmusPerInch);

    public static int InchesToTwips(double inches)
        => (int)Math.Round(inches * TwipsPerInch);

    public static string ToRelativeDisplayPath(string workDirectory, string fullPath)
        => Path.GetRelativePath(workDirectory, fullPath).Replace('\\', '/');

    private static bool IsWithinRoot(string root, string fullPath)
    {
        if (string.Equals(root, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}