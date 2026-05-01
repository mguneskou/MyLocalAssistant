using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MyLocalAssistant.Shared.Plugins;

namespace MyLocalAssistant.Server.Tools.Plugin;

/// <summary>
/// Half-duplex JSON-RPC client over the plug-in's stdio. One pending request at a time per
/// channel, identified by a monotonically increasing id. Reads happen on a background task;
/// writes are serialized through a <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class ToolRpcChannel : IAsyncDisposable
{
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StreamReader _stderr;
    private readonly ILogger _log;
    private readonly string _toolId;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>> _pending = new();
    private long _nextId;
    private volatile bool _faulted;

    public ToolRpcChannel(string toolId, Stream stdin, Stream stdout, StreamReader stderr, ILogger log)
    {
        _toolId = toolId;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _log = log;
        _readerTask = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public bool IsFaulted => _faulted;

    public async Task<JsonElement?> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (_faulted) throw new InvalidOperationException($"Plug-in '{_toolId}' channel is faulted.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            JsonElement? paramsElement = null;
            if (parameters is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters, JsonRpcFraming.Json);
                using var doc = JsonDocument.Parse(bytes);
                paramsElement = doc.RootElement.Clone();
            }
            var req = new RpcRequest { Id = id, Method = method, Params = paramsElement };
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await JsonRpcFraming.WriteFrameAsync(_stdin, req, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
            {
                var resp = await tcs.Task.ConfigureAwait(false);
                if (resp.Error is not null)
                    throw new ToolRpcException(_toolId, method, resp.Error.Code, resp.Error.Message);
                return resp.Result;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                var bytes = await JsonRpcFraming.ReadFrameAsync(_stdout, _readerCts.Token).ConfigureAwait(false);
                if (bytes is null) break; // clean EOF
                RpcResponse? resp;
                try { resp = JsonSerializer.Deserialize<RpcResponse>(bytes, JsonRpcFraming.Json); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Plug-in {Tool} sent malformed JSON-RPC frame.", _toolId);
                    continue;
                }
                if (resp?.Id is long id && _pending.TryGetValue(id, out var tcs))
                    tcs.TrySetResult(resp);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Plug-in {Tool} read loop crashed; channel faulted.", _toolId);
        }
        finally
        {
            _faulted = true;
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new IOException($"Plug-in '{_toolId}' channel closed."));
            _pending.Clear();
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _stderr.ReadLineAsync(_readerCts.Token).ConfigureAwait(false)) is not null)
                _log.LogInformation("[plugin {Skill} stderr] {Line}", _toolId, line);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug(ex, "Plug-in {Tool} stderr drain ended.", _toolId); }
    }

    public async ValueTask DisposeAsync()
    {
        _readerCts.Cancel();
        try { await _readerTask.ConfigureAwait(false); } catch { }
        _writeLock.Dispose();
        _readerCts.Dispose();
    }
}

public sealed class ToolRpcException(string toolId, string method, int code, string message)
    : Exception($"Plug-in '{toolId}'.{method} failed (code={code}): {message}")
{
    public string ToolId { get; } = toolId;
    public string Method { get; } = method;
    public int Code { get; } = code;
}
