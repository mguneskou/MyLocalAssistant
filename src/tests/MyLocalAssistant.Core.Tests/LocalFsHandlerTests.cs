using System.Text;
using System.Text.Json;
using MyLocalAssistant.Client.Bridge;

namespace MyLocalAssistant.Core.Tests;

/// <summary>
/// Path-validation and write-policy tests for the client-side fs handler.
/// This is the security boundary: the client decides what the server is allowed to touch.
/// </summary>
public class LocalFsHandlerTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFsHandler _h;

    public LocalFsHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mla-fs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _h = new LocalFsHandler(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement P(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private static JsonElement AsJson(object? r)
        => JsonDocument.Parse(JsonSerializer.Serialize(r)).RootElement;

    [Fact]
    public async Task Stat_returns_exists_false_for_unknown_path()
    {
        var r = AsJson(await _h.InvokeAsync("fs.stat", P(new { path = "missing.txt" }), default));
        Assert.False(r.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Write_then_read_round_trips()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        await _h.InvokeAsync("fs.write", P(new { path = "a.txt", bytesB64 = Convert.ToBase64String(bytes) }), default);
        var r = AsJson(await _h.InvokeAsync("fs.read", P(new { path = "a.txt", offset = 0, length = 4096 }), default));
        var got = Convert.FromBase64String(r.GetProperty("bytesB64").GetString()!);
        Assert.Equal(bytes, got);
        Assert.True(r.GetProperty("eof").GetBoolean());
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<BridgeMethodException>(() =>
            _h.InvokeAsync("fs.stat", P(new { path = "..\\escape.txt" }), default));
        Assert.Equal("fs.bad", ex.Code);
    }

    [Fact]
    public async Task Absolute_path_outside_root_is_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "mla-other-" + Guid.NewGuid().ToString("N") + ".txt");
        var ex = await Assert.ThrowsAsync<BridgeMethodException>(() =>
            _h.InvokeAsync("fs.stat", P(new { path = outside }), default));
        Assert.Equal("fs.outsideRoot", ex.Code);
    }

    [Fact]
    public async Task Wildcards_are_rejected()
    {
        var ex = await Assert.ThrowsAsync<BridgeMethodException>(() =>
            _h.InvokeAsync("fs.stat", P(new { path = "*.txt" }), default));
        Assert.Equal("fs.bad", ex.Code);
    }

    [Fact]
    public async Task Writing_executable_extensions_is_blocked()
    {
        var ex = await Assert.ThrowsAsync<BridgeMethodException>(() =>
            _h.InvokeAsync("fs.write", P(new { path = "evil.exe", bytesB64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }), default));
        Assert.Equal("fs.blockedExtension", ex.Code);
    }

    [Fact]
    public async Task NotConfigured_when_root_is_null()
    {
        var h = new LocalFsHandler(null);
        var ex = await Assert.ThrowsAsync<BridgeMethodException>(() =>
            h.InvokeAsync("fs.stat", P(new { path = "x" }), default));
        Assert.Equal("fs.notConfigured", ex.Code);
    }

    [Fact]
    public async Task TempPath_creates_per_conversation_subdir()
    {
        var conv = Guid.NewGuid();
        var r = AsJson(await _h.InvokeAsync("fs.tempPath", P(new { conversationId = conv.ToString("N") }), default));
        var path = r.GetProperty("path").GetString()!;
        Assert.True(Directory.Exists(path));
        Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_returns_files_under_root()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "yy");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var r = AsJson(await _h.InvokeAsync("fs.list", P(new { path = "." }), default));
        var entries = r.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "a.txt");
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "sub" && e.GetProperty("isDir").GetBoolean());
    }

    [Fact]
    public async Task Mkdir_is_idempotent()
    {
        var r1 = AsJson(await _h.InvokeAsync("fs.mkdir", P(new { path = "newdir" }), default));
        var r2 = AsJson(await _h.InvokeAsync("fs.mkdir", P(new { path = "newdir" }), default));
        Assert.True(r1.GetProperty("created").GetBoolean());
        Assert.False(r2.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task Delete_missing_file_succeeds()
    {
        await _h.InvokeAsync("fs.delete", P(new { path = "ghost.txt" }), default);
    }

    [Fact]
    public async Task User_can_create_and_use_subfolder()
    {
        // Confirms the requirement: users should be able to create new folders under
        // the root for different tasks and write into them.
        await _h.InvokeAsync("fs.mkdir", P(new { path = "task1" }), default);
        var bytes = Encoding.UTF8.GetBytes("payload");
        await _h.InvokeAsync("fs.write", P(new { path = "task1/data.txt", bytesB64 = Convert.ToBase64String(bytes) }), default);
        var stat = AsJson(await _h.InvokeAsync("fs.stat", P(new { path = "task1/data.txt" }), default));
        Assert.True(stat.GetProperty("exists").GetBoolean());
        Assert.Equal(bytes.Length, stat.GetProperty("size").GetInt64());
    }
}
