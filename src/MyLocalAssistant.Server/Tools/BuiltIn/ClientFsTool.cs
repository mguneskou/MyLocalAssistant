using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using MyLocalAssistant.Server.ClientBridge;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Exposes the v2.2 reverse-RPC <c>fs.*</c> family to the LLM as ordinary tools.
/// All requests are forwarded to the user's currently-connected Client process,
/// which enforces its own root-folder policy. If no Client is online for the user
/// the call returns a clear error so the LLM can pick a different strategy.
/// </summary>
internal sealed class ClientFsTool : ITool
{
    private readonly ClientBridgeHub _hub;

    public ClientFsTool(ClientBridgeHub hub) { _hub = hub; }

    public string Id => "client.fs";
    public string Name => "Client filesystem";
    public string Description => "Read and write files inside the folder the user shared from their PC.";
    public string Category => "Built-in";
    public string Source => ToolSources.BuiltIn;
    public string? Version => null;
    public string? Publisher => "MyLocalAssistant";
    public string? KeyId => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto("client.fs.stat",
            "Check whether a file or directory exists in the user's shared folder. Returns size and last-modified time when present.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string", "description": "Path relative to the user's shared root, or absolute path inside it." }
              }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.list",
            "List files and subfolders in a directory inside the user's shared folder.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string", "description": "Directory path. Use '.' for the shared root." },
                "pattern": { "type": "string", "description": "Optional glob like '*.xlsx'." },
                "recursive": { "type": "boolean", "default": false }
              }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.read",
            "Read up to 4 MiB of a file inside the user's shared folder. Returns base64 bytes and eof flag for chunked reads. Excel files (.xlsx/.xls) are automatically parsed and returned as readable tab-separated text — do NOT use this for xlsx; use excel.get_sheet_names + excel.read_range instead when possible, or rely on the auto-parse.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string" },
                "offset": { "type": "integer", "minimum": 0, "default": 0 },
                "length": { "type": "integer", "minimum": 1, "maximum": 4194304, "default": 262144 }
              }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.write",
            "Write bytes (base64) to a file inside the user's shared folder. Creates parent directories. Refuses executable extensions like .exe/.dll/.bat.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string" },
                "bytesB64": { "type": "string", "description": "Base64-encoded file content." },
                "append": { "type": "boolean", "default": false }
              }, "required": ["path", "bytesB64"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.mkdir",
            "Create a directory inside the user's shared folder. Idempotent.",
            """
            { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.move",
            "Move or rename a file/directory inside the user's shared folder.",
            """
            { "type": "object", "properties": {
                "from": { "type": "string" },
                "to":   { "type": "string" },
                "overwrite": { "type": "boolean", "default": false }
              }, "required": ["from", "to"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.delete",
            "Delete a file or directory inside the user's shared folder. Use recursive=true for non-empty folders.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string" },
                "recursive": { "type": "boolean", "default": false }
              }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.tempPath",
            "Get a per-conversation scratch directory inside the user's shared folder. Use this when you need a place to put intermediate files.",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 4);

    public void Configure(string? configJson) { /* no per-instance config */ }

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        var bridge = _hub.TryGet(ctx.UserId);
        if (bridge is null)
            return ToolResult.Error("No Client app is currently connected for this user. Ask the user to open the MyLocalAssistant Client and pick a shared folder.");

        // Re-shape arguments where needed (e.g. inject conversationId for tempPath).
        object? @params;
        try
        {
            @params = call.ToolName switch
            {
                "client.fs.tempPath" => new { conversationId = ctx.ConversationId.ToString("N") },
                _ => string.IsNullOrWhiteSpace(call.ArgumentsJson)
                    ? (object)new { }
                    : JsonDocument.Parse(call.ArgumentsJson).RootElement.Clone(),
            };
        }
        catch (JsonException ex)
        {
            return ToolResult.Error("Arguments must be a JSON object: " + ex.Message);
        }

        // Intercept Excel reads: parse .xlsx/.xls and return readable text instead of raw binary.
        if (call.ToolName == "client.fs.read" && @params is JsonElement fsEl &&
            fsEl.TryGetProperty("path", out var fsPathEl) && fsPathEl.GetString() is string fsFilePath &&
            (fsFilePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
             fsFilePath.EndsWith(".xls",  StringComparison.OrdinalIgnoreCase)))
        {
            return await ReadExcelFromClientAsync(bridge, fsFilePath, ctx.CancellationToken);
        }

        // Map LLM-facing tool name to wire method ("client.fs.read" -> "fs.read").
        var method = call.ToolName.StartsWith("client.fs.", StringComparison.Ordinal)
            ? "fs." + call.ToolName.Substring("client.fs.".Length)
            : call.ToolName;

        try
        {
            var result = await bridge.InvokeAsync(method, @params, timeout: TimeSpan.FromSeconds(60), ctx.CancellationToken);
            var json = result is null ? "{}" : result.Value.GetRawText();
            return ToolResult.Ok(json, json);
        }
        catch (ClientBridgeException ex)
        {
            return ToolResult.Error($"{ex.Code}: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return ToolResult.Error("Client did not respond in time. The Client app may be unresponsive or disconnected.");
        }
    }

    /// <summary>
    /// Reads an Excel file from the client's shared folder via the bridge and returns
    /// the cell data as readable tab-separated text, sheet by sheet.
    /// </summary>
    private static async Task<ToolResult> ReadExcelFromClientAsync(
        IClientBridge bridge, string path, CancellationToken ct)
    {
        const int MaxBytes = 4 * 1024 * 1024;
        JsonElement? raw;
        try
        {
            raw = await bridge.InvokeAsync(
                "fs.read", new { path, offset = 0, length = MaxBytes },
                TimeSpan.FromSeconds(60), ct);
        }
        catch (ClientBridgeException ex) { return ToolResult.Error($"{ex.Code}: {ex.Message}"); }
        catch (TimeoutException) { return ToolResult.Error("Client did not respond in time."); }

        if (raw is null || !raw.Value.TryGetProperty("bytesB64", out var b64Prop))
            return ToolResult.Error("Unexpected response from client bridge.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64Prop.GetString() ?? ""); }
        catch { return ToolResult.Error("Invalid base64 data from client bridge."); }

        if (bytes.Length >= MaxBytes &&
            raw.Value.TryGetProperty("eof", out var eofProp) && !eofProp.GetBoolean())
            return ToolResult.Error("Excel file exceeds 4 MB and cannot be read in one call.");

        try
        {
            using var ms = new MemoryStream(bytes);
            using var wb = new XLWorkbook(ms);
            var sb = new StringBuilder();
            foreach (var ws in wb.Worksheets)
            {
                sb.AppendLine($"## Sheet: {ws.Name}");
                var range = ws.RangeUsed();
                if (range is null) continue;
                foreach (var row in range.Rows())
                    sb.AppendLine(string.Join("\t", row.Cells().Select(c => c.GetFormattedString())));
            }
            var text = sb.ToString();
            return ToolResult.Ok(JsonSerializer.Serialize(new { text, eof = true }), text);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to parse Excel file: {ex.Message}");
        }
    }
}
