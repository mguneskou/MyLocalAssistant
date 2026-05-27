using System.Text;
using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

internal sealed class WorkDirectoryTool : ITool
{
    public string Id => "workdir";
    public string Name => "Work Directory Tool";
    public string Description => "Inspect and copy files inside the current conversation work directory. Use this to discover user-supplied templates, SQL scripts, examples, and other repeatable workflow assets.";
    public string Category => "Productivity";
    public string Source => ToolSources.BuiltIn;
    public string? Version => null;
    public string? Publisher => "MyLocalAssistant";
    public string? KeyId => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "workdir.list_files",
            Description: "List files and folders inside the conversation work directory. Use this before starting office work so you can find user-provided templates, SQL files, prior examples, and output folders.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"path":{"type":"string","description":"Optional relative subfolder inside the work directory. Defaults to the root."},"pattern":{"type":"string","description":"Optional filename pattern such as *.docx, *.xlsx, *.pptx, *.sql, or *.*. Defaults to *."},"recursive":{"type":"boolean","description":"Search subfolders recursively. Default false."},"includeDirectories":{"type":"boolean","description":"Include folders in the results. Default false."},"maxResults":{"type":"integer","description":"Maximum items to return (default 200, max 1000)."}},"additionalProperties":false}"""),
        new ToolFunctionDto(
            Name: "workdir.read_text",
            Description: "Read a UTF-8 text file from the conversation work directory. Use this for SQL scripts, JSON metadata, markdown instructions, CSV samples, or plain-text templates.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to a text file inside the work directory."},"maxChars":{"type":"integer","description":"Maximum characters to return (default 16000, max 50000)."}},"required":["path"],"additionalProperties":false}"""),
        new ToolFunctionDto(
            Name: "workdir.copy_file",
            Description: "Copy a file within the conversation work directory. Use this to duplicate a template into a new working file before editing it with word.*, excel.*, or powerpoint.* tools.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"source":{"type":"string","description":"Existing source file path relative to the work directory."},"destination":{"type":"string","description":"Destination file path relative to the work directory."},"overwrite":{"type":"boolean","description":"Overwrite the destination if it already exists. Default false."}},"required":["source","destination"],"additionalProperties":false}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 4);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public void Configure(string? configJson) { }

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args = doc.RootElement.Clone();

        var result = call.ToolName switch
        {
            "workdir.list_files" => ListFiles(args, ctx),
            "workdir.read_text" => ReadText(args, ctx),
            "workdir.copy_file" => CopyFile(args, ctx),
            _ => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
        };
        return Task.FromResult(result);
    }

    private static ToolResult ListFiles(JsonElement args, ToolContext ctx)
    {
        var relativeRoot = GetString(args, "path") ?? ".";
        var pattern = GetString(args, "pattern") ?? "*";
        var recursive = GetBool(args, "recursive");
        var includeDirectories = GetBool(args, "includeDirectories");
        var maxResults = Math.Clamp(GetInt(args, "maxResults") ?? 200, 1, 1000);

        if (pattern.Contains(Path.DirectorySeparatorChar) || pattern.Contains(Path.AltDirectorySeparatorChar))
            return ToolResult.Error("pattern must be a filename pattern only. Use 'path' for subfolders.");

        var root = ResolvePath(ctx.WorkDirectory, relativeRoot, allowMissing: false);
        if (root is null) return ToolResult.Error("path must stay within the work directory.");
        if (!Directory.Exists(root)) return ToolResult.Error($"Folder not found: {relativeRoot}");

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var results = new List<object>(Math.Min(maxResults, 64));
        var truncated = false;

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, pattern, searchOption))
        {
            var isDirectory = Directory.Exists(entry);
            if (isDirectory && !includeDirectories) continue;

            if (results.Count == maxResults)
            {
                truncated = true;
                break;
            }

            var relativePath = ToRelativePath(ctx.WorkDirectory, entry);
            if (isDirectory)
            {
                var dirInfo = new DirectoryInfo(entry);
                results.Add(new
                {
                    path = relativePath,
                    name = dirInfo.Name,
                    isDirectory = true,
                    size = (long?)null,
                    lastWriteUtc = dirInfo.LastWriteTimeUtc,
                });
            }
            else
            {
                var fileInfo = new FileInfo(entry);
                results.Add(new
                {
                    path = relativePath,
                    name = fileInfo.Name,
                    isDirectory = false,
                    size = fileInfo.Length,
                    lastWriteUtc = fileInfo.LastWriteTimeUtc,
                });
            }
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            root = ToRelativePath(ctx.WorkDirectory, root),
            pattern,
            recursive,
            truncated,
            items = results,
        }, s_json));
    }

    private static ToolResult ReadText(JsonElement args, ToolContext ctx)
    {
        var relativePath = GetRequiredString(args, "path");
        var maxChars = Math.Clamp(GetInt(args, "maxChars") ?? 16000, 1, 50000);
        var path = ResolvePath(ctx.WorkDirectory, relativePath, allowMissing: false);
        if (path is null) return ToolResult.Error("path must stay within the work directory.");
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {relativePath}");

        string content;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            content = reader.ReadToEnd();

        var truncated = false;
        if (content.Length > maxChars)
        {
            content = content[..maxChars];
            truncated = true;
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            path = ToRelativePath(ctx.WorkDirectory, path),
            truncated,
            content,
        }, s_json));
    }

    private static ToolResult CopyFile(JsonElement args, ToolContext ctx)
    {
        var sourceRelative = GetRequiredString(args, "source");
        var destinationRelative = GetRequiredString(args, "destination");
        var overwrite = GetBool(args, "overwrite");

        var source = ResolvePath(ctx.WorkDirectory, sourceRelative, allowMissing: false);
        if (source is null) return ToolResult.Error("source must stay within the work directory.");
        if (!File.Exists(source)) return ToolResult.Error($"Source file not found: {sourceRelative}");

        var destination = ResolvePath(ctx.WorkDirectory, destinationRelative, allowMissing: true);
        if (destination is null) return ToolResult.Error("destination must stay within the work directory.");
        if (Directory.Exists(destination)) return ToolResult.Error("destination must be a file path, not a folder.");
        if (!overwrite && File.Exists(destination)) return ToolResult.Error($"Destination already exists: {destinationRelative}");

        var destinationDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDir)) Directory.CreateDirectory(destinationDir);
        File.Copy(source, destination, overwrite);

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            source = ToRelativePath(ctx.WorkDirectory, source),
            destination = ToRelativePath(ctx.WorkDirectory, destination),
            success = true,
        }, s_json));
    }

    private static string? ResolvePath(string workDirectory, string relativePath, bool allowMissing)
    {
        Directory.CreateDirectory(workDirectory);
        var root = Path.GetFullPath(workDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath));
        if (!IsWithinRoot(root, candidate)) return null;
        if (!allowMissing && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        return candidate;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelativePath(string workDirectory, string fullPath)
        => Path.GetRelativePath(workDirectory, fullPath).Replace('\\', '/');

    private static string GetRequiredString(JsonElement args, string name)
        => GetString(args, name) ?? throw new ArgumentException($"{name} is required.");

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True
            ? true
            : false;

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
}