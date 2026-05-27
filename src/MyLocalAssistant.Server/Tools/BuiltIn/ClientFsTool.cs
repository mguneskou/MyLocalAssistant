using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using MyLocalAssistant.Server.ClientBridge;
using MyLocalAssistant.Server.Rag;
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
            "Read up to 4 MiB of a file inside the user's shared folder. Returns base64 bytes and eof flag for chunked reads. Excel files (.xlsx/.xls) are automatically parsed and returned as readable tab-separated text. Word documents (.docx) are automatically parsed and returned as plain text — do NOT try to read their raw bytes.",
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
        new ToolFunctionDto("client.fs.copyToWorkDir",
            "Copy a file from the user's client PC into the server's work directory so that tools like excel.*, word.*, pdf.* can operate on it. Returns the filename to use with those tools. ALWAYS use this before calling excel.* or word.* on a file that exists on the client.",
            """
            { "type": "object", "properties": {
                "path": { "type": "string", "description": "Path to the file on the client (as returned by client.fs.list)." },
                "saveas": { "type": "string", "description": "Optional filename to use on the server. Defaults to the source filename." }
              }, "required": ["path"], "additionalProperties": false }
            """),
        new ToolFunctionDto("client.fs.copyFromWorkDir",
            "Copy a file from the server's work directory back to the user's client PC. Use this after modifying a file with excel.*, word.*, pdf.* to save it to the client.",
            """
            { "type": "object", "properties": {
                "filename": { "type": "string", "description": "Filename in the server work directory (as returned by excel.create, excel.save_as, etc.)." },
                "path": { "type": "string", "description": "Destination path on the client. Defaults to the filename in the client shared root." }
              }, "required": ["filename"], "additionalProperties": false }
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

        // Intercept Word reads: parse .docx and return plain text instead of raw binary.
        if (call.ToolName == "client.fs.read" && @params is JsonElement docEl &&
            docEl.TryGetProperty("path", out var docPathEl) && docPathEl.GetString() is string docFilePath &&
            docFilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadWordFromClientAsync(bridge, docFilePath, ctx.CancellationToken);
        }

        // Intercept copyToWorkDir/copyFromWorkDir before forwarding to bridge.
        if (call.ToolName == "client.fs.copyToWorkDir" && @params is JsonElement cpToEl)
            return await CopyToWorkDirAsync(bridge, cpToEl, ctx);
        if (call.ToolName == "client.fs.copyFromWorkDir" && @params is JsonElement cpFromEl)
            return await CopyFromWorkDirAsync(bridge, cpFromEl, ctx);

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

    /// <summary>
    /// Reads a Word document (.docx) from the client's shared folder via the bridge
    /// and returns the extracted plain text.
    /// </summary>
    private static async Task<ToolResult> ReadWordFromClientAsync(
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
            return ToolResult.Error("Word document exceeds 4 MB and cannot be read in one call.");

        try
        {
            using var ms = new MemoryStream(bytes);
            var pages = DocumentParsers.Parse(ms, System.IO.Path.GetFileName(path));
            var text = string.Join("\n\n", pages.Select(p => p.Text.TrimEnd()));
            return ToolResult.Ok(JsonSerializer.Serialize(new { text, eof = true }), text);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to parse Word document: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads a file from the client's shared folder and saves it to the server WorkDirectory
    /// so that excel.*, word.*, pdf.* tools can operate on it.
    /// </summary>
    private static async Task<ToolResult> CopyToWorkDirAsync(
        IClientBridge bridge, JsonElement args, ToolContext ctx)
    {
        if (!args.TryGetProperty("path", out var pathEl) || pathEl.GetString() is not string clientPath)
            return ToolResult.Error("'path' is required.");

        var serverFilename = args.TryGetProperty("saveas", out var saEl) && saEl.GetString() is { Length: > 0 } sa
            ? sa
            : System.IO.Path.GetFileName(clientPath);

        // Prevent path traversal
        if (serverFilename.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return ToolResult.Error($"Invalid filename: {serverFilename}");

        const int ChunkSize = 4 * 1024 * 1024;
        var chunks = new List<byte[]>();
        int offset = 0;

        while (true)
        {
            JsonElement? raw;
            try
            {
                raw = await bridge.InvokeAsync(
                    "fs.read", new { path = clientPath, offset, length = ChunkSize },
                    TimeSpan.FromSeconds(60), ctx.CancellationToken);
            }
            catch (ClientBridgeException ex) { return ToolResult.Error($"{ex.Code}: {ex.Message}"); }
            catch (TimeoutException) { return ToolResult.Error("Client did not respond in time."); }

            if (raw is null || !raw.Value.TryGetProperty("bytesB64", out var b64))
                return ToolResult.Error("Unexpected response from client bridge.");

            byte[] chunk;
            try { chunk = Convert.FromBase64String(b64.GetString() ?? ""); }
            catch { return ToolResult.Error("Invalid base64 data from client bridge."); }

            if (chunk.Length > 0) chunks.Add(chunk);

            bool eof = !raw.Value.TryGetProperty("eof", out var eofProp) || eofProp.GetBoolean();
            if (eof || chunk.Length == 0) break;
            offset += chunk.Length;

            if (offset > 32 * 1024 * 1024)
                return ToolResult.Error("File exceeds 32 MB limit for server transfer.");
        }

        Directory.CreateDirectory(ctx.WorkDirectory);
        var serverPath = System.IO.Path.Combine(ctx.WorkDirectory, serverFilename);
        await using var fs = File.Create(serverPath);
        foreach (var chunk in chunks) await fs.WriteAsync(chunk, ctx.CancellationToken);

        var totalKb = chunks.Sum(c => c.Length) / 1024;
        return ToolResult.Ok(
            JsonSerializer.Serialize(new { filename = serverFilename, sizeKb = totalKb }),
            $"Copied '{clientPath}' to server work directory as '{serverFilename}' ({totalKb} KB). You can now use it with excel.*, word.*, or pdf.* tools.");
    }

    /// <summary>
    /// Reads a file from the server WorkDirectory and writes it back to the client's shared folder.
    /// </summary>
    private static async Task<ToolResult> CopyFromWorkDirAsync(
        IClientBridge bridge, JsonElement args, ToolContext ctx)
    {
        if (!args.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename)
            return ToolResult.Error("'filename' is required.");

        if (filename.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return ToolResult.Error($"Invalid filename: {filename}");

        var serverPath = System.IO.Path.Combine(ctx.WorkDirectory, filename);
        if (!File.Exists(serverPath))
            return ToolResult.Error($"File '{filename}' not found in server work directory.");

        var clientPath = args.TryGetProperty("path", out var cpEl) && cpEl.GetString() is { Length: > 0 } cp
            ? cp
            : filename;

        byte[] bytes = await File.ReadAllBytesAsync(serverPath, ctx.CancellationToken);
        var bytesB64 = Convert.ToBase64String(bytes);

        try
        {
            await bridge.InvokeAsync(
                "fs.write", new { path = clientPath, bytesB64, append = false },
                TimeSpan.FromSeconds(60), ctx.CancellationToken);
        }
        catch (ClientBridgeException ex) { return ToolResult.Error($"{ex.Code}: {ex.Message}"); }
        catch (TimeoutException) { return ToolResult.Error("Client did not respond in time."); }

        return ToolResult.Ok(
            JsonSerializer.Serialize(new { clientPath, sizeKb = bytes.Length / 1024 }),
            $"Saved '{filename}' ({bytes.Length / 1024} KB) to client at '{clientPath}'.");
    }
}
