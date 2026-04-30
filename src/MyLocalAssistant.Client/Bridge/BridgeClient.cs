using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MyLocalAssistant.Client.Services;

namespace MyLocalAssistant.Client.Bridge;

/// <summary>
/// Client-side end of the v2.2 reverse-RPC bridge.
/// Connects to /api/client/bridge over WebSocket, listens for "req" frames from the
/// server, dispatches them to <see cref="LocalFsHandler"/>, and writes back "res" frames.
/// Auto-reconnects with exponential backoff while signed in.
/// </summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly ChatApiClient _api;
    private readonly LocalFsHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runner;

    public event Action<string>? StatusChanged;

    public string? Root
    {
        get => _handler.Root;
        set => _handler.Root = LocalFsHandler.NormalizeRoot(value);
    }

    public BridgeClient(ChatApiClient api, string? initialRoot)
    {
        _api = api;
        _handler = new LocalFsHandler(initialRoot);
    }

    public void Start()
    {
        if (_runner is not null) return;
        _runner = Task.Run(RunForeverAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runner is not null) { try { await _runner; } catch { } }
        _cts.Dispose();
    }

    private async Task RunForeverAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Notify("connecting");
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var token = await _api.GetAccessTokenAsync(_cts.Token);
                if (string.IsNullOrEmpty(token))
                {
                    Notify("waiting for sign-in");
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                    continue;
                }
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

                var uri = BuildWsUri(_api.BaseUrl);
                await ws.ConnectAsync(uri, _cts.Token);
                Notify(_handler.Root is null ? "connected (no folder)" : "connected");
                backoff = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(ws, _cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Notify($"disconnected: {ex.Message}");
            }
            try { await Task.Delay(backoff, _cts.Token); } catch { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    private static Uri BuildWsUri(string baseUrl)
    {
        var b = new UriBuilder(baseUrl);
        b.Scheme = b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        b.Path = (b.Path.TrimEnd('/') + "/api/client/bridge").TrimStart('/') is { Length: > 0 } p
            ? "/" + p
            : "/api/client/bridge";
        return b.Uri;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                _ = HandleFrameAsync(ws, ms.ToArray(), ct);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            var t = root.TryGetProperty("t", out var tProp) ? tProp.GetString() : null;
            if (t != "req") return; // ignore everything else for now
            id = root.GetProperty("id").GetString();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pp) ? pp : default;

            object? result;
            try
            {
                result = await _handler.InvokeAsync(method, p, ct);
            }
            catch (BridgeMethodException ex)
            {
                await SendAsync(ws, JsonSerializer.SerializeToUtf8Bytes(
                    new { t = "res", id, ok = false, error = new { code = ex.Code, message = ex.Message } }, s_json), ct);
                return;
            }
            catch (Exception ex)
            {
                await SendAsync(ws, JsonSerializer.SerializeToUtf8Bytes(
                    new { t = "res", id, ok = false, error = new { code = "fs.io", message = ex.Message } }, s_json), ct);
                return;
            }
            await SendAsync(ws, JsonSerializer.SerializeToUtf8Bytes(
                new { t = "res", id, ok = true, result }, s_json), ct);
        }
        catch
        {
            if (id is not null)
            {
                try
                {
                    await SendAsync(ws, JsonSerializer.SerializeToUtf8Bytes(
                        new { t = "res", id, ok = false, error = new { code = "bridge.badFrame", message = "Could not parse request." } }, s_json), ct);
                }
                catch { }
            }
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private async Task SendAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct); }
        finally { _sendLock.Release(); }
    }

    private void Notify(string status) => StatusChanged?.Invoke(status);
}
