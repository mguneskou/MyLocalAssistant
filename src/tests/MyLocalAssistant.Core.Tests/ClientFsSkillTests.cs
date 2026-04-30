using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Server.ClientBridge;
using MyLocalAssistant.Server.Skills;
using MyLocalAssistant.Server.Skills.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

/// <summary>
/// Tests the LLM-facing client.fs skill, including the no-client-connected error path
/// and the happy-path round-trip via a fake client transport.
/// </summary>
public class ClientFsSkillTests
{
    [Fact]
    public async Task Returns_error_when_no_client_connected()
    {
        var hub = new ClientBridgeHub(NullLogger<ClientBridgeHub>.Instance);
        var skill = MakeSkill(hub);
        var ctx = MakeContext();

        var r = await skill.InvokeAsync(new SkillInvocation("client.fs.stat", "{\"path\":\"x\"}"), ctx);

        Assert.True(r.IsError);
        Assert.Contains("Client app", r.Content);
    }

    [Fact]
    public async Task Forwards_call_to_connected_client_and_returns_json_result()
    {
        var (server, client) = MakePair();
        var hub = new ClientBridgeHub(NullLogger<ClientBridgeHub>.Instance);
        var userId = Guid.NewGuid();
        await using var session = new ClientBridgeSession(server, userId, NullLogger.Instance);
        session.Start();
        hub.Register(userId, session);

        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            using var doc = JsonDocument.Parse(frame!);
            // LLM tool name is client.fs.stat -> wire method must be fs.stat.
            Assert.Equal("fs.stat", doc.RootElement.GetProperty("method").GetString());
            Assert.Equal("report.xlsx", doc.RootElement.GetProperty("params").GetProperty("path").GetString());
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                t = "res",
                id,
                ok = true,
                result = new { exists = true, isDir = false, size = 4096, mtime = "2026-04-30T12:00:00+00:00" },
            });
            await client.SendAsync(reply, default);
        });

        var skill = MakeSkill(hub);
        var ctx = MakeContext(userId);
        var r = await skill.InvokeAsync(new SkillInvocation("client.fs.stat", "{\"path\":\"report.xlsx\"}"), ctx);

        Assert.False(r.IsError);
        using var doc = JsonDocument.Parse(r.Content);
        Assert.True(doc.RootElement.GetProperty("exists").GetBoolean());
        Assert.Equal(4096, doc.RootElement.GetProperty("size").GetInt32());

        await fake;
    }

    [Fact]
    public async Task TempPath_injects_conversation_id()
    {
        var (server, client) = MakePair();
        var hub = new ClientBridgeHub(NullLogger<ClientBridgeHub>.Instance);
        var userId = Guid.NewGuid();
        var convId = Guid.NewGuid();
        await using var session = new ClientBridgeSession(server, userId, NullLogger.Instance);
        session.Start();
        hub.Register(userId, session);

        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            using var doc = JsonDocument.Parse(frame!);
            Assert.Equal("fs.tempPath", doc.RootElement.GetProperty("method").GetString());
            // The LLM passed no args; the skill must inject conversationId from context.
            Assert.Equal(convId.ToString("N"),
                doc.RootElement.GetProperty("params").GetProperty("conversationId").GetString());
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new { t = "res", id, ok = true, result = new { path = "C:\\BobScratch\\conversations\\abc" } });
            await client.SendAsync(reply, default);
        });

        var skill = MakeSkill(hub);
        var ctx = MakeContext(userId, convId);
        var r = await skill.InvokeAsync(new SkillInvocation("client.fs.tempPath", "{}"), ctx);
        Assert.False(r.IsError);
        await fake;
    }

    [Fact]
    public async Task Bridge_error_is_surfaced_with_code()
    {
        var (server, client) = MakePair();
        var hub = new ClientBridgeHub(NullLogger<ClientBridgeHub>.Instance);
        var userId = Guid.NewGuid();
        await using var session = new ClientBridgeSession(server, userId, NullLogger.Instance);
        session.Start();
        hub.Register(userId, session);

        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            using var doc = JsonDocument.Parse(frame!);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                t = "res", id, ok = false,
                error = new { code = "fs.outsideRoot", message = "Path is outside the shared folder." },
            });
            await client.SendAsync(reply, default);
        });

        var skill = MakeSkill(hub);
        var r = await skill.InvokeAsync(new SkillInvocation("client.fs.stat", "{\"path\":\"C:\\\\Windows\"}"), MakeContext(userId));
        Assert.True(r.IsError);
        Assert.Contains("fs.outsideRoot", r.Content);
        await fake;
    }

    // -------- helpers --------

    private static ClientFsSkill MakeSkill(ClientBridgeHub hub) => new(hub);

    private static SkillContext MakeContext(Guid? userId = null, Guid? convId = null) => new(
        UserId: userId ?? Guid.NewGuid(),
        Username: "tester",
        IsAdmin: false,
        IsGlobalAdmin: false,
        AgentId: "agent-1",
        ConversationId: convId ?? Guid.NewGuid(),
        WorkDirectory: Path.GetTempPath(),
        CancellationToken: default);

    private static (IBridgeTransport server, IBridgeTransport client) MakePair()
    {
        var s2c = Channel.CreateUnbounded<byte[]>();
        var c2s = Channel.CreateUnbounded<byte[]>();
        return (new ChannelTransport(s2c.Writer, c2s.Reader),
                new ChannelTransport(c2s.Writer, s2c.Reader));
    }

    private sealed class ChannelTransport : IBridgeTransport
    {
        private readonly ChannelWriter<byte[]> _out;
        private readonly ChannelReader<byte[]> _in;
        public ChannelTransport(ChannelWriter<byte[]> @out, ChannelReader<byte[]> @in) { _out = @out; _in = @in; }
        public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) { _out.TryWrite(frame.ToArray()); return Task.CompletedTask; }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            try { return await _in.ReadAsync(ct); } catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { _out.TryComplete(); return ValueTask.CompletedTask; }
    }
}
