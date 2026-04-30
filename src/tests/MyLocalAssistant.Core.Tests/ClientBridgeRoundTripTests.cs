using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Server.ClientBridge;

namespace MyLocalAssistant.Core.Tests;

/// <summary>
/// Round-trip the v2.2 client bridge against an in-memory fake "client".
/// Proves: request/response correlation by id, fs.* facade payload shapes,
/// timeout, error propagation, multiple concurrent calls.
/// </summary>
public class ClientBridgeRoundTripTests
{
    [Fact]
    public async Task Stat_round_trips_through_typed_facade()
    {
        var (server, client) = MakePair();
        await using var session = new ClientBridgeSession(server, Guid.NewGuid(), NullLogger.Instance);
        session.Start();
        var fs = ClientBridgeHub.CreateFs(new HubBridge(session));

        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            Assert.NotNull(frame);
            using var doc = JsonDocument.Parse(frame!);
            Assert.Equal("req", doc.RootElement.GetProperty("t").GetString());
            Assert.Equal("fs.stat", doc.RootElement.GetProperty("method").GetString());
            Assert.Equal("C:\\x.txt", doc.RootElement.GetProperty("params").GetProperty("path").GetString());
            var id = doc.RootElement.GetProperty("id").GetString()!;
            await client.SendAsync(Reply(id, new { exists = true, isDir = false, size = 1234, mtime = "2026-01-02T03:04:05+00:00" }), default);
        });

        var stat = await fs.StatAsync("C:\\x.txt");
        Assert.True(stat.Exists);
        Assert.False(stat.IsDir);
        Assert.Equal(1234, stat.Size);
        await fake;
    }

    [Fact]
    public async Task Read_decodes_base64_bytes()
    {
        var (server, client) = MakePair();
        await using var session = new ClientBridgeSession(server, Guid.NewGuid(), NullLogger.Instance);
        session.Start();
        var fs = ClientBridgeHub.CreateFs(new HubBridge(session));

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            using var doc = JsonDocument.Parse(frame!);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            await client.SendAsync(Reply(id, new { bytesB64 = Convert.ToBase64String(payload), eof = true }), default);
        });

        var read = await fs.ReadAsync("C:\\x.bin", 0, 4096);
        Assert.Equal(payload, read.Bytes);
        Assert.True(read.Eof);
        await fake;
    }

    [Fact]
    public async Task Error_response_throws_ClientBridgeException_with_code()
    {
        var (server, client) = MakePair();
        await using var session = new ClientBridgeSession(server, Guid.NewGuid(), NullLogger.Instance);
        session.Start();
        var fs = ClientBridgeHub.CreateFs(new HubBridge(session));

        var fake = Task.Run(async () =>
        {
            var frame = await client.ReceiveAsync(default);
            using var doc = JsonDocument.Parse(frame!);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            await client.SendAsync(ReplyError(id, "fs.notFound", "no such file"), default);
        });

        var ex = await Assert.ThrowsAsync<ClientBridgeException>(() => fs.StatAsync("C:\\nope"));
        Assert.Equal("fs.notFound", ex.Code);
        await fake;
    }

    [Fact]
    public async Task Concurrent_calls_correlate_by_id_with_out_of_order_responses()
    {
        var (server, client) = MakePair();
        await using var session = new ClientBridgeSession(server, Guid.NewGuid(), NullLogger.Instance);
        session.Start();
        var fs = ClientBridgeHub.CreateFs(new HubBridge(session));

        var paths = new[] { "a", "ab", "abc", "abcd", "abcde" };
        var fake = Task.Run(async () =>
        {
            var pending = new List<(string id, string path)>();
            for (int i = 0; i < paths.Length; i++)
            {
                var frame = await client.ReceiveAsync(default);
                using var doc = JsonDocument.Parse(frame!);
                pending.Add((doc.RootElement.GetProperty("id").GetString()!,
                             doc.RootElement.GetProperty("params").GetProperty("path").GetString()!));
            }
            // Reply in reverse order to prove correlation isn't FIFO.
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (id, path) = pending[i];
                await client.SendAsync(Reply(id, new { exists = true, isDir = false, size = path.Length, mtime = (string?)null }), default);
            }
        });

        var tasks = paths.Select(p => fs.StatAsync(p)).ToArray();
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(paths[i].Length, results[i].Size);
        await fake;
    }

    [Fact]
    public async Task Timeout_propagates_when_client_silent()
    {
        var (server, client) = MakePair();
        await using var session = new ClientBridgeSession(server, Guid.NewGuid(), NullLogger.Instance);
        session.Start();

        var sink = Task.Run(async () => await client.ReceiveAsync(default));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            session.InvokeAsync("fs.stat", new { path = "x" }, TimeSpan.FromMilliseconds(150), default));
        await sink;
    }

    private static (IBridgeTransport server, IBridgeTransport client) MakePair()
    {
        var s2c = Channel.CreateUnbounded<byte[]>();
        var c2s = Channel.CreateUnbounded<byte[]>();
        return (new ChannelTransport(s2c.Writer, c2s.Reader),
                new ChannelTransport(c2s.Writer, s2c.Reader));
    }

    private static byte[] Reply(string id, object result)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = "res", id, ok = true, result }));

    private static byte[] ReplyError(string id, string code, string message)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = "res", id, ok = false, error = new { code, message } }));

    private sealed class ChannelTransport : IBridgeTransport
    {
        private readonly ChannelWriter<byte[]> _out;
        private readonly ChannelReader<byte[]> _in;
        public ChannelTransport(ChannelWriter<byte[]> @out, ChannelReader<byte[]> @in) { _out = @out; _in = @in; }
        public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            _out.TryWrite(frame.ToArray());
            return Task.CompletedTask;
        }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            try { return await _in.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { _out.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class HubBridge : IClientBridge
    {
        private readonly ClientBridgeSession _s;
        public HubBridge(ClientBridgeSession s) { _s = s; }
        public Task<JsonElement?> InvokeAsync(string method, object? @params, TimeSpan? timeout, CancellationToken ct)
            => _s.InvokeAsync(method, @params, timeout, ct);
    }
}
