using System.Text.Json;
using MyLocalAssistant.Shared.Plugins;

namespace MyLocalAssistant.Plugin.Shared;

/// <summary>
/// JSON-RPC 2.0 server-side host for plugin processes.
/// Reads framed requests from stdin, dispatches to registered tool handlers,
/// writes framed responses to stdout.
/// Usage: new PluginHost().Register("tool.name", handler).RunAsync()
/// </summary>
public sealed class PluginHost
{
    private readonly Dictionary<string, IPluginTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private string? _configJson;

    public PluginHost Register(string toolName, IPluginTool handler)
    {
        _tools[toolName] = handler;
        return this;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Binary stdio for LSP-style Content-Length framing.
        var stdin  = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (!ct.IsCancellationRequested)
        {
            byte[]? bytes;
            try { bytes = await JsonRpcFraming.ReadFrameAsync(stdin, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { break; } // EOF or pipe broken

            if (bytes is null) break;

            RpcRequest? req;
            try { req = JsonSerializer.Deserialize<RpcRequest>(bytes, JsonRpcFraming.Json); }
            catch { continue; }
            if (req is null) continue;

            JsonElement? result = null;
            RpcError?    error  = null;

            try
            {
                result = req.Method switch
                {
                    "initialize" => HandleInitialize(req.Params),
                    "invoke"     => await HandleInvokeAsync(req.Params, ct).ConfigureAwait(false),
                    _            => throw new InvalidOperationException($"Unknown method '{req.Method}'"),
                };
            }
            catch (Exception ex)
            {
                error = new RpcError { Code = -32000, Message = ex.Message };
            }

            var resp = new RpcResponse { Id = req.Id, Result = result, Error = error };
            try { await JsonRpcFraming.WriteFrameAsync(stdout, resp, ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private JsonElement? HandleInitialize(JsonElement? p)
    {
        if (p?.TryGetProperty("configJson", out var cfgEl) == true && cfgEl.ValueKind != JsonValueKind.Null)
            _configJson = cfgEl.GetString();

        foreach (var tool in _tools.Values)
            tool.Configure(_configJson);

        return Serialize(new { initialized = true });
    }

    private async Task<JsonElement?> HandleInvokeAsync(JsonElement? p, CancellationToken ct)
    {
        if (p is null)
            throw new ArgumentException("invoke params missing");

        var toolName  = p.Value.TryGetProperty("tool",      out var tn)  ? tn.GetString() ?? "" : "";
        var arguments = p.Value.TryGetProperty("arguments", out var args) ? args : default;
        var ctx       = p.Value.TryGetProperty("context",   out var c)   ? c    : default;

        var context = new PluginContext(
            UserId:         ctx.TryGetProperty("userId",         out var uid)  ? uid.GetString()  ?? "" : "",
            Username:       ctx.TryGetProperty("username",       out var un)   ? un.GetString()   ?? "" : "",
            IsAdmin:        ctx.TryGetProperty("isAdmin",        out var ia)   && ia.ValueKind == JsonValueKind.True,
            AgentId:        ctx.TryGetProperty("agentId",        out var aid)  ? aid.GetString()  ?? "" : "",
            ConversationId: ctx.TryGetProperty("conversationId", out var cid)  ? cid.GetString()  ?? "" : "",
            WorkDirectory:  ctx.TryGetProperty("workDirectory",  out var wd)   ? wd.GetString()   ?? "" : "");

        if (!_tools.TryGetValue(toolName, out var handler))
            throw new InvalidOperationException($"No handler registered for tool '{toolName}'");

        var r = await handler.InvokeAsync(toolName, arguments, context, ct).ConfigureAwait(false);

        object payload = r.StructuredJson is not null
            ? new
            {
                isError    = r.IsError,
                content    = r.Content,
                structured = JsonSerializer.Deserialize<JsonElement>(r.StructuredJson, JsonRpcFraming.Json)
            }
            : new { isError = r.IsError, content = r.Content };

        return Serialize(payload);
    }

    private static JsonElement? Serialize(object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonRpcFraming.Json);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}
