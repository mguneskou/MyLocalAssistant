using System.Text.Json;

namespace MyLocalAssistant.Server.ClientBridge;

/// <summary>
/// Typed view over a client bridge for the fs.* method family.
/// All paths are resolved on the client; the client enforces its own root and access policy.
/// Methods throw <see cref="ClientBridgeException"/> on remote errors and
/// <see cref="TimeoutException"/> if the client doesn't respond in time.
/// </summary>
public interface IClientFs
{
    Task<FsStat> StatAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<FsEntry>> ListAsync(string path, string? pattern = null, bool recursive = false, CancellationToken ct = default);
    Task<FsReadResult> ReadAsync(string path, long offset = 0, int length = 256 * 1024, CancellationToken ct = default);
    Task<int> WriteAsync(string path, byte[] bytes, bool append = false, CancellationToken ct = default);
    Task<bool> MkdirAsync(string path, CancellationToken ct = default);
    Task MoveAsync(string from, string to, bool overwrite = false, CancellationToken ct = default);
    Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default);
    /// <summary>Returns a per-conversation scratch directory under the client's configured root.</summary>
    Task<string> TempPathAsync(Guid conversationId, CancellationToken ct = default);
}

public sealed record FsStat(bool Exists, bool IsDir, long Size, DateTimeOffset Mtime);
public sealed record FsEntry(string Name, bool IsDir, long Size, DateTimeOffset Mtime);
public sealed record FsReadResult(byte[] Bytes, bool Eof);

internal sealed class ClientFsFacade : IClientFs
{
    private readonly IClientBridge _bridge;
    public ClientFsFacade(IClientBridge bridge) { _bridge = bridge; }

    public async Task<FsStat> StatAsync(string path, CancellationToken ct)
    {
        var r = await Invoke("fs.stat", new { path }, ct);
        return new FsStat(
            r.GetProperty("exists").GetBoolean(),
            r.TryGetProperty("isDir", out var d) && d.GetBoolean(),
            r.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
            r.TryGetProperty("mtime", out var m) && m.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(m.GetString()!) : default);
    }

    public async Task<IReadOnlyList<FsEntry>> ListAsync(string path, string? pattern, bool recursive, CancellationToken ct)
    {
        var r = await Invoke("fs.list", new { path, pattern, recursive }, ct);
        var arr = r.GetProperty("entries");
        var list = new List<FsEntry>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new FsEntry(
                e.GetProperty("name").GetString() ?? "",
                e.TryGetProperty("isDir", out var d) && d.GetBoolean(),
                e.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                e.TryGetProperty("mtime", out var m) && m.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(m.GetString()!) : default));
        }
        return list;
    }

    public async Task<FsReadResult> ReadAsync(string path, long offset, int length, CancellationToken ct)
    {
        var r = await Invoke("fs.read", new { path, offset, length }, ct);
        var b64 = r.GetProperty("bytesB64").GetString() ?? "";
        var eof = r.TryGetProperty("eof", out var e) && e.GetBoolean();
        return new FsReadResult(Convert.FromBase64String(b64), eof);
    }

    public async Task<int> WriteAsync(string path, byte[] bytes, bool append, CancellationToken ct)
    {
        var r = await Invoke("fs.write", new { path, bytesB64 = Convert.ToBase64String(bytes), append }, ct);
        return r.GetProperty("bytesWritten").GetInt32();
    }

    public async Task<bool> MkdirAsync(string path, CancellationToken ct)
    {
        var r = await Invoke("fs.mkdir", new { path }, ct);
        return r.TryGetProperty("created", out var c) && c.GetBoolean();
    }

    public async Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
        => await Invoke("fs.move", new { from, to, overwrite }, ct);

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        => await Invoke("fs.delete", new { path, recursive }, ct);

    public async Task<string> TempPathAsync(Guid conversationId, CancellationToken ct)
    {
        var r = await Invoke("fs.tempPath", new { conversationId = conversationId.ToString("N") }, ct);
        return r.GetProperty("path").GetString() ?? throw new ClientBridgeException("fs.bad", "Client returned no path.");
    }

    private async Task<JsonElement> Invoke(string method, object @params, CancellationToken ct)
    {
        var raw = await _bridge.InvokeAsync(method, @params, timeout: null, ct)
            ?? throw new ClientBridgeException("fs.bad", $"Client returned no result for '{method}'.");
        return raw;
    }
}
