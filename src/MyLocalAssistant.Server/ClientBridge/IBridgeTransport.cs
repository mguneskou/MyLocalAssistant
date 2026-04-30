using System.Net.WebSockets;

namespace MyLocalAssistant.Server.ClientBridge;

/// <summary>
/// Minimal frame-based transport abstraction used by <see cref="ClientBridgeSession"/>.
/// Production wraps a WebSocket; tests can supply an in-memory pair.
/// One frame == one logical message (a JSON object).
/// </summary>
public interface IBridgeTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);
    /// <summary>Returns the next frame, or null when the peer closed cleanly.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

internal sealed class WebSocketBridgeTransport : IBridgeTransport
{
    private readonly WebSocket _ws;
    public WebSocketBridgeTransport(WebSocket ws) { _ws = ws; }

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        => await _ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                return null;
            }
            ms.Write(buf, 0, result.Count);
        } while (!result.EndOfMessage);
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
    }
}
