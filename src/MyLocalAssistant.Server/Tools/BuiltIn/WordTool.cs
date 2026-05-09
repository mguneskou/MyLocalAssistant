using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Word document (.docx) tool. Reads existing documents and creates new ones
/// in the conversation work directory using DocumentFormat.OpenXml.
/// </summary>
internal sealed class WordTool : ITool
{
    public string  Id          => "word";
    public string  Name        => "Word Document Tool";
    public string  Description => "Read and write Microsoft Word (.docx) documents in the conversation work directory.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 8);

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "word.read",
            Description: "Read the full text content of a .docx file from the conversation work directory. Returns the document text with paragraph breaks.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Filename of the Word document (e.g. 'report.docx')."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "word.write",
            Description: "Create or overwrite a .docx file in the conversation work directory. Accepts an array of paragraphs; the first paragraph can optionally be a title (heading level 1). Returns the filename.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Output filename (e.g. 'summary.docx'). Extension added if missing."},"paragraphs":{"type":"array","description":"List of paragraph objects to write.","items":{"type":"object","properties":{"text":{"type":"string","description":"Paragraph text content."},"style":{"type":"string","description":"Paragraph style: 'normal' (default), 'heading1', 'heading2', 'heading3', 'bold'.","enum":["normal","heading1","heading2","heading3","bold"]}},"required":["text"]}}},"required":["filename","paragraphs"]}"""),
    };

    public void Configure(string? configJson) { /* no configuration needed */ }

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        return call.ToolName switch
        {
            "word.read"  => ReadAsync(call, ctx),
            "word.write" => WriteAsync(call, ctx),
            _ => Task.FromResult(ToolResult.Error($"Unknown word tool: {call.ToolName}")),
        };
    }

    // ── word.read ─────────────────────────────────────────────────────────────

    private static Task<ToolResult> ReadAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParseArgs(call.ArgumentsJson, out var doc)) return Task.FromResult(ToolResult.Error("Arguments must be a JSON object with 'filename'."));

        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename || string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(ToolResult.Error("'filename' is required."));

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Task.FromResult(ToolResult.Error("Invalid filename — path traversal is not allowed."));
        if (!File.Exists(path)) return Task.FromResult(ToolResult.Error($"File not found: {filename}"));

        try
        {
            using var stream = File.OpenRead(path);
            var pages = DocumentParsers.Parse(stream, filename);
            var text = string.Join("\n\n", pages.Select(p => p.Text.TrimEnd()));
            var result = JsonSerializer.Serialize(new { filename, charCount = text.Length, text });
            return Task.FromResult(ToolResult.Ok(result, text));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Failed to read Word document: {ex.Message}"));
        }
    }

    // ── word.write ────────────────────────────────────────────────────────────

    private static Task<ToolResult> WriteAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParseArgs(call.ArgumentsJson, out var doc)) return Task.FromResult(ToolResult.Error("Arguments must be a JSON object."));

        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename || string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(ToolResult.Error("'filename' is required."));
        if (!doc.TryGetProperty("paragraphs", out var parasEl) || parasEl.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.Error("'paragraphs' must be a non-empty array."));

        // Ensure .docx extension.
        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            filename += ".docx";

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Task.FromResult(ToolResult.Error("Invalid filename — path traversal is not allowed."));

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            using var docStream = File.Create(path);
            using var wordDoc = WordprocessingDocument.Create(docStream, WordprocessingDocumentType.Document);
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            foreach (var paraEl in parasEl.EnumerateArray())
            {
                var text  = paraEl.TryGetProperty("text",  out var tEl) ? tEl.GetString() ?? "" : "";
                var style = paraEl.TryGetProperty("style", out var sEl) ? sEl.GetString() ?? "normal" : "normal";

                var para = new Paragraph();
                var run  = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

                switch (style.ToLowerInvariant())
                {
                    case "heading1":
                        para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" });
                        break;
                    case "heading2":
                        para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" });
                        break;
                    case "heading3":
                        para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading3" });
                        break;
                    case "bold":
                        run.RunProperties = new RunProperties(new Bold());
                        break;
                }

                para.Append(run);
                body.Append(para);
            }

            mainPart.Document.Save();
            var result = JsonSerializer.Serialize(new { filename, path = Path.GetFileName(path), success = true });
            return Task.FromResult(ToolResult.Ok(result, $"Word document '{filename}' written successfully."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Failed to write Word document: {ex.Message}"));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseArgs(string? json, out JsonElement el)
    {
        el = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { el = JsonDocument.Parse(json).RootElement; return el.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    /// <summary>
    /// Returns the full path under <paramref name="workDir"/> or null if the filename
    /// would escape the work directory (path traversal guard).
    /// </summary>
    private static string? SafePath(string workDir, string filename)
    {
        // Strip any directory separators — filenames only, no sub-paths.
        var name = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var full = Path.GetFullPath(Path.Combine(workDir, name));
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
