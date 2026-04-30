using System.Text.Json;
using MyLocalAssistant.Shared.Plugins;

// Echo reference plug-in. Speaks JSON-RPC 2.0 (LSP-style framing) over stdin/stdout.
// Supported methods:
//   initialize  -> { ok: true }
//   invoke      -> { isError, content }   where invoke.params = { tool, arguments, context }
//   shutdown    -> { ok: true }   (server then closes stdin to terminate the process)
// All other methods return JSON-RPC error -32601 (method not found).
//
// This file is intentionally self-contained and only references the Shared framing types.

await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
var ct = CancellationToken.None;

while (true)
{
    byte[]? frame;
    try { frame = await JsonRpcFraming.ReadFrameAsync(stdin, ct); }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync("[echo] read error: " + ex.Message);
        return 1;
    }
    if (frame is null) return 0; // clean EOF

    RpcRequest? req;
    try { req = JsonSerializer.Deserialize<RpcRequest>(frame, JsonRpcFraming.Json); }
    catch (Exception ex)
    {
        await WriteErrorAsync(stdout, null, -32700, "Parse error: " + ex.Message);
        continue;
    }
    if (req is null || string.IsNullOrEmpty(req.Method))
    {
        await WriteErrorAsync(stdout, req?.Id, -32600, "Invalid Request");
        continue;
    }

    try
    {
        switch (req.Method)
        {
            case "initialize":
                await WriteResultAsync(stdout, req.Id, new { ok = true, name = "echo" });
                break;
            case "invoke":
                await HandleInvokeAsync(stdout, req);
                break;
            case "shutdown":
                await WriteResultAsync(stdout, req.Id, new { ok = true });
                return 0;
            default:
                await WriteErrorAsync(stdout, req.Id, -32601, $"Method not found: {req.Method}");
                break;
        }
    }
    catch (Exception ex)
    {
        await WriteErrorAsync(stdout, req.Id, -32603, "Internal error: " + ex.Message);
    }
}

static async Task HandleInvokeAsync(Stream stdout, RpcRequest req)
{
    var p = req.Params;
    if (p is null || p.Value.ValueKind != JsonValueKind.Object)
    {
        await WriteErrorAsync(stdout, req.Id, -32602, "Invalid params: expected object.");
        return;
    }
    var tool = p.Value.TryGetProperty("tool", out var tEl) ? tEl.GetString() ?? "" : "";
    var args = p.Value.TryGetProperty("arguments", out var aEl) ? aEl : default;
    if (tool != "echo.say")
    {
        await WriteResultAsync(stdout, req.Id, new { isError = true, content = $"Unknown tool '{tool}'." });
        return;
    }
    var text = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
        ? txt.GetString() ?? ""
        : "";
    await WriteResultAsync(stdout, req.Id, new { isError = false, content = text });
}

static async Task WriteResultAsync(Stream stdout, long? id, object result)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonRpcFraming.Json);
    using var doc = JsonDocument.Parse(bytes);
    var resp = new RpcResponse { Id = id, Result = doc.RootElement.Clone() };
    await JsonRpcFraming.WriteFrameAsync(stdout, resp, CancellationToken.None);
}

static async Task WriteErrorAsync(Stream stdout, long? id, int code, string message)
{
    var resp = new RpcResponse { Id = id, Error = new RpcError { Code = code, Message = message } };
    await JsonRpcFraming.WriteFrameAsync(stdout, resp, CancellationToken.None);
}