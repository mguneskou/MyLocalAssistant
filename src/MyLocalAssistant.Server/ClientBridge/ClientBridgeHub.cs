using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace MyLocalAssistant.Server.ClientBridge;

/// <summary>
/// Tracks the active reverse-RPC bridge for each user.
/// One bridge per user — opening a second Client evicts the older session.
/// </summary>
public sealed class ClientBridgeHub
{
    private readonly ConcurrentDictionary<Guid, ClientBridgeSession> _sessions = new();
    private readonly ILogger<ClientBridgeHub> _log;

    public ClientBridgeHub(ILogger<ClientBridgeHub> log) { _log = log; }

    /// <summary>
    /// Test/inspection seam. Most callers should use <see cref="TryGet"/> or <see cref="TryGetFs"/>.
    /// </summary>
    public void Register(Guid userId, ClientBridgeSession session)
    {
        if (_sessions.TryRemove(userId, out var existing))
        {
            _log.LogInformation("Evicting older client bridge for user {UserId}.", userId);
            _ = existing.DisposeAsync();
        }
        _sessions[userId] = session;
    }

    public bool Unregister(Guid userId, ClientBridgeSession session)
    {
        if (_sessions.TryGetValue(userId, out var current) && ReferenceEquals(current, session))
            return _sessions.TryRemove(userId, out _);
        return false;
    }

    internal async Task RunWebSocketAsync(WebSocket ws, Guid userId, ILogger log, CancellationToken ct)
    {
        var transport = new WebSocketBridgeTransport(ws);
        var session = new ClientBridgeSession(transport, userId, log);
        Register(userId, session);
        session.Start();
        try
        {
            using var reg = ct.Register(() => _ = session.DisposeAsync().AsTask());
            await session.Completion;
        }
        finally
        {
            Unregister(userId, session);
            await session.DisposeAsync();
        }
    }

    public IClientBridge? TryGet(Guid userId)
        => _sessions.TryGetValue(userId, out var s) ? new BridgeFacade(s) : null;

    public IClientFs? TryGetFs(Guid userId)
    {
        var b = TryGet(userId);
        return b is null ? null : new ClientFsFacade(b);
    }

    /// <summary>Wrap an arbitrary bridge with the typed fs.* facade. Used by tests.</summary>
    public static IClientFs CreateFs(IClientBridge bridge) => new ClientFsFacade(bridge);

    public bool IsConnected(Guid userId) => _sessions.ContainsKey(userId);

    private sealed class BridgeFacade : IClientBridge
    {
        private readonly ClientBridgeSession _session;
        public BridgeFacade(ClientBridgeSession s) { _session = s; }
        public Task<JsonElement?> InvokeAsync(string method, object? @params, TimeSpan? timeout, CancellationToken ct)
            => _session.InvokeAsync(method, @params, timeout, ct);
    }
}

public interface IClientBridge
{
    Task<JsonElement?> InvokeAsync(string method, object? @params, TimeSpan? timeout, CancellationToken ct);
}
