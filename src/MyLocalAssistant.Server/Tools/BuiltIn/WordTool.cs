using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Comprehensive Word (.docx) tool. Supports structured reading, rich writing,
/// appending, tables, find/replace, lists, and document properties.
/// All files are scoped to the conversation WorkDirectory.
/// </summary>
internal sealed class WordTool : ITool
{
    public string  Id          => "word";
    public string  Name        => "Word Document Tool";
    public string  Description => "Read and write Microsoft Word (.docx) documents with full formatting: paragraphs, headings, bold/italic/underline, bullet and numbered lists, tables, find/replace, and document properties.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 8);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Tool definitions ──────────────────────────────────────────────────────

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "word.read",
            Description: "Read a .docx file and return structured content: paragraphs (with style), tables (as row/cell arrays), and plain text.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Word document filename in the work directory."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "word.write",
            Description: "Create or overwrite a .docx file. Accepts an array of content blocks: paragraphs, headings, bullet/numbered lists, tables, and horizontal rules. Supports per-run inline formatting (bold, italic, underline, color, fontSize).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"blocks":{"type":"array","description":"Content blocks.","items":{"type":"object","properties":{"type":{"type":"string","enum":["paragraph","heading1","heading2","heading3","heading4","bullet","numbered","table","hr"]},"text":{"type":"string","description":"Text for paragraph/heading/bullet/numbered blocks."},"runs":{"type":"array","description":"Fine-grained runs with individual formatting.","items":{"type":"object","properties":{"text":{"type":"string"},"bold":{"type":"boolean"},"italic":{"type":"boolean"},"underline":{"type":"boolean"},"color":{"type":"string","description":"HTML hex color e.g. #FF0000"},"fontSize":{"type":"integer","description":"Font size in half-points (24=12pt, 28=14pt)"}},"required":["text"]}},"rows":{"type":"array","description":"For type=table: array of rows, each an array of cell text strings.","items":{"type":"array","items":{"type":"string"}}},"headerRow":{"type":"boolean","description":"For type=table: style first row as header."}},"required":["type"]}}},"required":["filename","blocks"]}"""),

        new ToolFunctionDto(
            Name: "word.append",
            Description: "Append content blocks to an existing .docx without overwriting it. Uses the same block format as word.write.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"blocks":{"type":"array","items":{"type":"object","properties":{"type":{"type":"string","enum":["paragraph","heading1","heading2","heading3","heading4","bullet","numbered","table","hr"]},"text":{"type":"string"},"runs":{"type":"array","items":{"type":"object","properties":{"text":{"type":"string"},"bold":{"type":"boolean"},"italic":{"type":"boolean"},"underline":{"type":"boolean"},"color":{"type":"string"},"fontSize":{"type":"integer"}},"required":["text"]}},"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}},"headerRow":{"type":"boolean"}},"required":["type"]}}},"required":["filename","blocks"]}"""),

        new ToolFunctionDto(
            Name: "word.find_replace",
            Description: "Find all occurrences of a text string in a .docx and replace them. Returns the count of replacements made.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"find":{"type":"string"},"replace":{"type":"string"},"matchCase":{"type":"boolean","description":"Case-sensitive. Default false."}},"required":["filename","find","replace"]}"""),

        new ToolFunctionDto(
            Name: "word.set_properties",
            Description: "Set document metadata: title, author, subject, description, keywords, company.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"title":{"type":"string"},"author":{"type":"string"},"subject":{"type":"string"},"description":{"type":"string"},"keywords":{"type":"string"},"company":{"type":"string"}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "word.get_properties",
            Description: "Get document metadata (title, author, subject, keywords, word count, paragraph count, creation and modification dates).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),
    };

    public void Configure(string? configJson) { /* no per-instance config */ }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        return call.ToolName switch
        {
            "word.read"           => ReadAsync(call, ctx),
            "word.write"          => WriteAsync(call, ctx, overwrite: true),
            "word.append"         => WriteAsync(call, ctx, overwrite: false),
            "word.find_replace"   => FindReplaceAsync(call, ctx),
            "word.set_properties" => SetPropertiesAsync(call, ctx),
            "word.get_properties" => GetPropertiesAsync(call, ctx),
            _ => Task.FromResult(ToolResult.Error($"Unknown word tool: {call.ToolName}")),
        };
    }

    // ── word.read ─────────────────────────────────────────────────────────────

    private static Task<ToolResult> ReadAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename)
            return Err("'filename' is required.");
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, false);
            var body = wdoc.MainDocumentPart?.Document.Body;
            if (body is null) return Err("Document has no body.");

            var blocks = new List<object>();
            var plainSb = new StringBuilder();
            int tableIdx = 0;

            foreach (var element in body.ChildElements)
            {
                if (element is Paragraph para)
                {
                    var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
                    var text = para.InnerText;
                    blocks.Add(new { type = "paragraph", style = styleId, text });
                    if (!string.IsNullOrWhiteSpace(text)) plainSb.AppendLine(text);
                }
                else if (element is Table table)
                {
                    tableIdx++;
                    var rows = table.Elements<TableRow>()
                        .Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList())
                        .ToList();
                    foreach (var r in rows) plainSb.AppendLine(string.Join(" | ", r));
                    blocks.Add(new { type = "table", tableIndex = tableIdx, rows });
                }
            }

            var result = JsonSerializer.Serialize(new { filename, blocks, plainText = plainSb.ToString() }, s_json);
            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex) { return Err($"Failed to read: {ex.Message}"); }
    }

    // ── word.write / word.append ──────────────────────────────────────────────

    private static Task<ToolResult> WriteAsync(ToolInvocation call, ToolContext ctx, bool overwrite)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename)
            return Err("'filename' is required.");
        if (!doc.TryGetProperty("blocks", out var blocksEl) || blocksEl.ValueKind != JsonValueKind.Array)
            return Err("'blocks' must be an array.");

        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) filename += ".docx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            if (!overwrite && File.Exists(path))
            {
                using var existing = WordprocessingDocument.Open(path, true);
                var existingBody = existing.MainDocumentPart?.Document.Body
                    ?? throw new InvalidOperationException("Document has no body.");
                AppendBlocks(existingBody, blocksEl);
                existing.MainDocumentPart!.Document.Save();
            }
            else
            {
                using var stream = File.Create(path);
                using var wordDoc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document(new Body());
                AddStyles(mainPart);
                AppendBlocks(mainPart.Document.Body!, blocksEl);
                mainPart.Document.Save();
            }
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { filename, success = true }, s_json),
                $"Word document '{filename}' saved."));
        }
        catch (Exception ex) { return Err($"Failed to write: {ex.Message}"); }
    }

    // ── word.find_replace ────────────────────────────────────────────────────

    private static Task<ToolResult> FindReplaceAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        var find    = doc.TryGetProperty("find",    out var fEl) ? fEl.GetString() ?? "" : "";
        var replace = doc.TryGetProperty("replace", out var rEl) ? rEl.GetString() ?? "" : "";
        var matchCase = doc.TryGetProperty("matchCase", out var mcEl) && mcEl.GetBoolean();

        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) filename += ".docx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var body = wdoc.MainDocumentPart?.Document.Body;
            if (body is null) return Err("Document has no body.");
            var comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int count = 0;
            foreach (var text in body.Descendants<Text>())
            {
                if (text.Text.Contains(find, comp))
                {
                    text.Text = text.Text.Replace(find, replace, comp);
                    count++;
                }
            }
            wdoc.MainDocumentPart!.Document.Save();
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { find, replace, replacements = count }, s_json)));
        }
        catch (Exception ex) { return Err($"Find/replace failed: {ex.Message}"); }
    }

    // ── word.set_properties ──────────────────────────────────────────────────

    private static Task<ToolResult> SetPropertiesAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) filename += ".docx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var props = wdoc.PackageProperties;
            if (doc.TryGetProperty("title",       out var t)) props.Title       = t.GetString();
            if (doc.TryGetProperty("author",      out var a)) props.Creator     = a.GetString();
            if (doc.TryGetProperty("subject",     out var s)) props.Subject     = s.GetString();
            if (doc.TryGetProperty("description", out var d)) props.Description = d.GetString();
            if (doc.TryGetProperty("keywords",    out var k)) props.Keywords    = k.GetString();
            if (doc.TryGetProperty("company",     out var c))
            {
                var extPart = wdoc.ExtendedFilePropertiesPart
                    ?? wdoc.AddNewPart<ExtendedFilePropertiesPart>();
                extPart.Properties ??= new DocumentFormat.OpenXml.ExtendedProperties.Properties();
                extPart.Properties.Company =
                    new DocumentFormat.OpenXml.ExtendedProperties.Company(c.GetString() ?? "");
                extPart.Properties.Save();
            }
            return Task.FromResult(ToolResult.Ok($"Properties updated for '{filename}'."));
        }
        catch (Exception ex) { return Err($"Set properties failed: {ex.Message}"); }
    }

    // ── word.get_properties ──────────────────────────────────────────────────

    private static Task<ToolResult> GetPropertiesAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) filename += ".docx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, false);
            var props = wdoc.PackageProperties;
            var extProps = wdoc.ExtendedFilePropertiesPart?.Properties;
            var body = wdoc.MainDocumentPart?.Document.Body;
            int wordCount = body?.Descendants<Text>()
                .Sum(t => t.Text.Split(new[] { ' ', '\t', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries).Length) ?? 0;
            int paraCount = body?.Elements<Paragraph>().Count() ?? 0;
            var result = JsonSerializer.Serialize(new
            {
                title       = props.Title,
                author      = props.Creator,
                subject     = props.Subject,
                description = props.Description,
                keywords    = props.Keywords,
                company     = extProps?.Company?.Text,
                created     = props.Created?.ToString("o"),
                modified    = props.Modified?.ToString("o"),
                wordCount,
                paragraphCount = paraCount,
            }, s_json);
            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex) { return Err($"Get properties failed: {ex.Message}"); }
    }

    // ── Block rendering ───────────────────────────────────────────────────────

    private static void AppendBlocks(Body body, JsonElement blocksEl)
    {
        foreach (var block in blocksEl.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "paragraph" : "paragraph";
            switch (type.ToLowerInvariant())
            {
                case "hr":
                    body.Append(BuildHorizontalRule());
                    break;
                case "table":
                    var rowsEl = block.TryGetProperty("rows", out var re) ? re : default;
                    var hasHeader = block.TryGetProperty("headerRow", out var hrEl) && hrEl.GetBoolean();
                    body.Append(BuildTable(rowsEl, hasHeader));
                    break;
                case "bullet":
                case "numbered":
                    body.Append(BuildListParagraph(block, type == "numbered"));
                    break;
                default:
                    body.Append(BuildParagraph(block, type));
                    break;
            }
        }
    }

    private static Paragraph BuildParagraph(JsonElement block, string type)
    {
        var para = new Paragraph();
        var styleId = type.ToLowerInvariant() switch
        {
            "heading1" => "Heading1",
            "heading2" => "Heading2",
            "heading3" => "Heading3",
            "heading4" => "Heading4",
            _          => null,
        };
        if (styleId is not null)
            para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = styleId });

        if (block.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var runEl in runsEl.EnumerateArray())
                para.Append(BuildRun(runEl));
        }
        else
        {
            var text = block.TryGetProperty("text", out var tEl2) ? tEl2.GetString() ?? "" : "";
            para.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return para;
    }

    private static Paragraph BuildListParagraph(JsonElement block, bool numbered)
    {
        var para = new Paragraph();
        var pPr = new ParagraphProperties(
            new ParagraphStyleId { Val = numbered ? "ListNumber" : "ListBullet" });
        para.ParagraphProperties = pPr;
        if (block.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var runEl in runsEl.EnumerateArray())
                para.Append(BuildRun(runEl));
        }
        else
        {
            var text = block.TryGetProperty("text", out var tEl2) ? tEl2.GetString() ?? "" : "";
            para.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return para;
    }

    private static Run BuildRun(JsonElement runEl)
    {
        var text = runEl.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var rPr = new RunProperties();
        bool hasProps = false;
        if (runEl.TryGetProperty("bold",      out var boldEl)   && boldEl.GetBoolean())
        { rPr.Append(new Bold()); hasProps = true; }
        if (runEl.TryGetProperty("italic",    out var italicEl) && italicEl.GetBoolean())
        { rPr.Append(new Italic()); hasProps = true; }
        if (runEl.TryGetProperty("underline", out var ulEl)     && ulEl.GetBoolean())
        { rPr.Append(new Underline { Val = UnderlineValues.Single }); hasProps = true; }
        if (runEl.TryGetProperty("color", out var colorEl) && colorEl.GetString() is { Length: > 0 } color)
        { rPr.Append(new Color { Val = color.TrimStart('#') }); hasProps = true; }
        if (runEl.TryGetProperty("fontSize", out var fsEl) && fsEl.TryGetInt32(out var fs))
        { rPr.Append(new FontSize { Val = fs.ToString() }); hasProps = true; }
        if (hasProps) run.RunProperties = rPr;
        return run;
    }

    private static Table BuildTable(JsonElement rowsEl, bool hasHeader)
    {
        var table = new Table();
        table.Append(new TableProperties(
            new TableBorders(
                new TopBorder    { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder   { Val = BorderValues.Single, Size = 4 },
                new RightBorder  { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder   { Val = BorderValues.Single, Size = 4 })));

        if (rowsEl.ValueKind != JsonValueKind.Array) return table;
        int rowIdx = 0;
        foreach (var rowEl in rowsEl.EnumerateArray())
        {
            var row = new TableRow();
            bool isHeader = hasHeader && rowIdx == 0;
            if (isHeader) row.Append(new TableRowProperties(new TableHeader()));
            if (rowEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var cellEl in rowEl.EnumerateArray())
                {
                    var cellText = cellEl.GetString() ?? "";
                    var cell = new TableCell();
                    var run = new Run(new Text(cellText) { Space = SpaceProcessingModeValues.Preserve });
                    if (isHeader) run.RunProperties = new RunProperties(new Bold());
                    cell.Append(new Paragraph(run));
                    row.Append(cell);
                }
            }
            table.Append(row);
            rowIdx++;
        }
        return table;
    }

    private static Paragraph BuildHorizontalRule()
    {
        return new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(
                    new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "auto" })));
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            BuildStyle("Normal",     null,          24, false),
            BuildStyle("Heading1",   "Heading 1",   32, true),
            BuildStyle("Heading2",   "Heading 2",   28, true),
            BuildStyle("Heading3",   "Heading 3",   24, true),
            BuildStyle("Heading4",   "Heading 4",   22, true),
            BuildStyle("ListBullet", "List Bullet",  24, false),
            BuildStyle("ListNumber", "List Number",  24, false));
        stylesPart.Styles.Save();
    }

    private static Style BuildStyle(string styleId, string? name, int halfPoints, bool bold)
    {
        var style = new Style { Type = StyleValues.Paragraph, StyleId = styleId };
        style.Append(new StyleName { Val = name ?? styleId });
        var rPr = new StyleRunProperties();
        rPr.Append(new FontSize { Val = halfPoints.ToString() });
        if (bold) rPr.Append(new Bold());
        style.Append(rPr);
        if (styleId.StartsWith("Heading"))
            style.Append(new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "120" }));
        return style;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParse(string? json, out JsonElement el)
    {
        el = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { el = JsonDocument.Parse(json).RootElement; return el.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    private static string? SafePath(string workDir, string filename)
    {
        var name = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var full = Path.GetFullPath(Path.Combine(workDir, name));
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static Task<ToolResult> Err(string msg) =>
        Task.FromResult(ToolResult.Error(msg));
}

