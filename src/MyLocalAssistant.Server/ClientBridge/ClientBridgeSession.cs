using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLocalAssistant.Server.ClientBridge;

/// <summary>
/// One live reverse-RPC channel from a single Client process to the server.
/// Server uses <see cref="InvokeAsync"/> to call methods that execute on the client.
/// Wire format is JSON, one frame per message; see docs/v2.2.0-client-fs-bridge.md.
/// </summary>
public sealed class ClientBridgeSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IBridgeTransport _transport;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new(StringComparer.Ordinal);
    private Task? _receiveLoop;

    public Guid UserId { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Task Completion => _receiveLoop ?? Task.CompletedTask;

    public ClientBridgeSession(IBridgeTransport transport, Guid userId, ILogger log)
    {
        _transport = transport;
        UserId = userId;
        _log = log;
    }

    public void Start() => _receiveLoop = Task.Run(ReceiveLoopAsync);

    public async Task<JsonElement?> InvokeAsync(string method, object? @params, TimeSpan? timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var frame = new BridgeRequest("req", id, method, @params);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, s_json);
            await SendAsync(bytes, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            cts.CancelAfter(effectiveTimeout);

            using var reg = cts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"Client bridge call '{method}' timed out after {effectiveTimeout.TotalSeconds:0}s.")));

            var response = await tcs.Task.ConfigureAwait(false);
            if (!response.Ok)
                throw new ClientBridgeException(response.Error?.Code ?? "bridge.unknown", response.Error?.Message ?? "Client bridge returned an error.");
            return response.Result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _transport.SendAsync(bytes, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var payload = await _transport.ReceiveAsync(_cts.Token);
                if (payload is null) return;
                HandleIncoming(payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogInformation("Client bridge for user {UserId} closed: {Msg}", UserId, ex.Message);
        }
        finally
        {
            FaultPending(new InvalidOperationException("Client bridge disconnected."));
        }
    }

    private void HandleIncoming(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var t = root.TryGetProperty("t", out var tProp) ? tProp.GetString() : null;
            if (t == "res")
            {
                var id = root.GetProperty("id").GetString();
                if (id is null || !_pending.TryGetValue(id, out var tcs)) return;
                var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                JsonElement? resultClone = null;
                BridgeError? error = null;
                if (ok && root.TryGetProperty("result", out var r))
                    resultClone = r.Clone();
                if (!ok && root.TryGetProperty("error", out var e))
                    error = JsonSerializer.Deserialize<BridgeError>(e.GetRawText(), s_json);
                tcs.TrySetResult(new BridgeResponse(ok, resultClone, error));
            }
            else if (t == "ping")
            {
                _ = SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"pong\"}"), _cts.Token);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Client bridge: bad frame from user {UserId}.", UserId);
        }
    }

    private void FaultPending(Exception ex)
    {
        foreach (var kv in _pending) kv.Value.TrySetException(ex);
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _transport.DisposeAsync();
        FaultPending(new ObjectDisposedException(nameof(ClientBridgeSession)));
        _sendLock.Dispose();
        _cts.Dispose();
    }

    private sealed record BridgeRequest(string t, string id, string method, object? @params);
    private sealed record BridgeResponse(bool Ok, JsonElement? Result, BridgeError? Error);
    private sealed record BridgeError(string Code, string Message);
}

public sealed class ClientBridgeException : Exception
{
    public string Code { get; }
    public ClientBridgeException(string code, string message) : base(message) { Code = code; }
}
