using System.Text.Json;

namespace MyLocalAssistant.Client.Bridge;

/// <summary>
/// Executes fs.* methods on the local disk, confined to the user-chosen root folder.
/// Every incoming path is canonicalized and rejected if it escapes the root, contains
/// '..' segments, hits a reparse point (symlink/junction), or has wildcard chars.
/// Executable extensions are read-only by default.
/// </summary>
internal sealed class LocalFsHandler
{
    private static readonly char[] s_invalidPathChars = { '*', '?', '"', '<', '>', '|' };
    private static readonly HashSet<string> s_blockedWriteExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com",
    };

    public string? Root { get; set; }

    public LocalFsHandler(string? root) { Root = NormalizeRoot(root); }

    public static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var full = Path.GetFullPath(root.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }
        catch { return null; }
    }

    /// <summary>
    /// Dispatch a single bridge call. Returns the JSON value (will be serialized as the
    /// "result" field) or throws <see cref="BridgeMethodException"/> with a stable code.
    /// </summary>
    public async Task<object?> InvokeAsync(string method, JsonElement @params, CancellationToken ct)
    {
        if (Root is null) throw new BridgeMethodException("fs.notConfigured", "Client has no shared folder configured.");

        return method switch
        {
            "fs.stat"     => Stat(GetPath(@params)),
            "fs.list"     => List(GetPath(@params), TryGetString(@params, "pattern"), TryGetBool(@params, "recursive")),
            "fs.read"     => await ReadAsync(GetPath(@params), TryGetLong(@params, "offset"), TryGetInt(@params, "length") ?? 256 * 1024, ct),
            "fs.write"    => await WriteAsync(GetPath(@params), TryGetString(@params, "bytesB64") ?? "", TryGetBool(@params, "append"), ct),
            "fs.mkdir"    => Mkdir(GetPath(@params)),
            "fs.move"     => Move(GetPath(@params, "from"), GetPath(@params, "to"), TryGetBool(@params, "overwrite")),
            "fs.delete"   => Delete(GetPath(@params), TryGetBool(@params, "recursive")),
            "fs.tempPath" => TempPath(TryGetString(@params, "conversationId") ?? throw new BridgeMethodException("fs.bad", "conversationId required.")),
            _             => throw new BridgeMethodException("bridge.unknownMethod", $"Method '{method}' is not supported."),
        };
    }

    // -------- methods --------

    private object Stat(string path)
    {
        var resolved = Resolve(path, mustExist: false);
        if (Directory.Exists(resolved))
        {
            var di = new DirectoryInfo(resolved);
            return new { exists = true, isDir = true, size = 0L, mtime = di.LastWriteTimeUtc.ToString("O") };
        }
        if (File.Exists(resolved))
        {
            var fi = new FileInfo(resolved);
            return new { exists = true, isDir = false, size = fi.Length, mtime = fi.LastWriteTimeUtc.ToString("O") };
        }
        return new { exists = false, isDir = false, size = 0L, mtime = (string?)null };
    }

    private object List(string path, string? pattern, bool recursive)
    {
        var resolved = Resolve(path, mustExist: true);
        if (!Directory.Exists(resolved))
            throw new BridgeMethodException("fs.notDirectory", "Path is not a directory.");
        var di = new DirectoryInfo(resolved);
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = di.EnumerateFileSystemInfos(string.IsNullOrEmpty(pattern) ? "*" : pattern, opt)
            .Where(fi => (fi.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(fi => new
            {
                name = recursive ? Path.GetRelativePath(resolved, fi.FullName) : fi.Name,
                isDir = (fi.Attributes & FileAttributes.Directory) != 0,
                size = fi is FileInfo f ? f.Length : 0L,
                mtime = fi.LastWriteTimeUtc.ToString("O"),
            })
            .ToList();
        return new { entries };
    }

    private async Task<object> ReadAsync(string path, long offset, int length, CancellationToken ct)
    {
        var resolved = Resolve(path, mustExist: true);
        EnsureNotReparsePoint(resolved);
        if (!File.Exists(resolved)) throw new BridgeMethodException("fs.notFound", "File not found.");
        var max = Math.Clamp(length, 1, 4 * 1024 * 1024);
        await using var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[max];
        var read = await fs.ReadAsync(buf.AsMemory(0, max), ct);
        var bytes = read == max ? buf : buf.AsSpan(0, read).ToArray();
        var eof = fs.Position >= fs.Length;
        return new { bytesB64 = Convert.ToBase64String(bytes), eof };
    }

    private async Task<object> WriteAsync(string path, string bytesB64, bool append, CancellationToken ct)
    {
        var resolved = Resolve(path, mustExist: false);
        var ext = Path.GetExtension(resolved);
        if (s_blockedWriteExt.Contains(ext))
            throw new BridgeMethodException("fs.blockedExtension", $"Writing '{ext}' files via the bridge is disabled.");
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        EnsureNotReparsePoint(dir!);
        var bytes = Convert.FromBase64String(bytesB64);
        await using var fs = new FileStream(resolved, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await fs.WriteAsync(bytes.AsMemory(), ct);
        return new { bytesWritten = bytes.Length };
    }

    private object Mkdir(string path)
    {
        var resolved = Resolve(path, mustExist: false);
        if (Directory.Exists(resolved)) return new { created = false };
        Directory.CreateDirectory(resolved);
        return new { created = true };
    }

    private object Move(string from, string to, bool overwrite)
    {
        var src = Resolve(from, mustExist: true);
        var dst = Resolve(to, mustExist: false);
        EnsureNotReparsePoint(src);
        if (File.Exists(src))
        {
            if (File.Exists(dst))
            {
                if (!overwrite) throw new BridgeMethodException("fs.exists", "Destination exists.");
                File.Delete(dst);
            }
            File.Move(src, dst);
        }
        else if (Directory.Exists(src))
        {
            if (Directory.Exists(dst))
            {
                if (!overwrite) throw new BridgeMethodException("fs.exists", "Destination exists.");
                Directory.Delete(dst, recursive: true);
            }
            Directory.Move(src, dst);
        }
        else throw new BridgeMethodException("fs.notFound", "Source not found.");
        return new { };
    }

    private object Delete(string path, bool recursive)
    {
        var resolved = Resolve(path, mustExist: false);
        EnsureNotReparsePoint(resolved);
        if (File.Exists(resolved)) File.Delete(resolved);
        else if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive);
        // missing == success
        return new { };
    }

    private object TempPath(string conversationId)
    {
        if (!Guid.TryParseExact(conversationId, "N", out var cid) && !Guid.TryParse(conversationId, out cid))
            throw new BridgeMethodException("fs.bad", "conversationId is not a GUID.");
        var dir = Path.Combine(Root!, "conversations", cid.ToString("N"));
        Directory.CreateDirectory(dir);
        return new { path = dir };
    }

    // -------- path validation --------

    /// <summary>
    /// Canonicalize a request path and verify it stays inside the configured root.
    /// </summary>
    private string Resolve(string requested, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(requested))
            throw new BridgeMethodException("fs.bad", "Path is empty.");
        if (requested.IndexOfAny(s_invalidPathChars) >= 0)
            throw new BridgeMethodException("fs.bad", "Path contains illegal characters.");
        // Reject explicit traversal segments before canonicalization swallows them.
        foreach (var seg in requested.Split('/', '\\'))
            if (seg == "..") throw new BridgeMethodException("fs.bad", "Path traversal is not allowed.");

        // Treat requests as relative to root unless they're already a full path inside root.
        string combined;
        try
        {
            combined = Path.IsPathFullyQualified(requested)
                ? Path.GetFullPath(requested)
                : Path.GetFullPath(Path.Combine(Root!, requested));
        }
        catch { throw new BridgeMethodException("fs.bad", "Path is invalid."); }

        var rootWithSep = Root! + Path.DirectorySeparatorChar;
        if (!combined.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeMethodException("fs.outsideRoot", "Path is outside the shared folder.");
        }

        if (mustExist && !File.Exists(combined) && !Directory.Exists(combined))
            throw new BridgeMethodException("fs.notFound", "Path does not exist.");
        return combined;
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new BridgeMethodException("fs.symlink", "Symlinks/junctions are not allowed via the bridge.");
        }
        catch (BridgeMethodException) { throw; }
        catch { /* ignore — non-existence handled above */ }
    }

    // -------- json helpers --------

    private static string GetPath(JsonElement p, string field = "path")
    {
        if (!p.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String)
            throw new BridgeMethodException("fs.bad", $"'{field}' is required.");
        return v.GetString() ?? throw new BridgeMethodException("fs.bad", $"'{field}' is empty.");
    }
    private static string? TryGetString(JsonElement p, string field)
        => p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool TryGetBool(JsonElement p, string field)
        => p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
    private static long TryGetLong(JsonElement p, string field)
        => p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
    private static int? TryGetInt(JsonElement p, string field)
        => p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

internal sealed class BridgeMethodException : Exception
{
    public string Code { get; }
    public BridgeMethodException(string code, string message) : base(message) { Code = code; }
}
