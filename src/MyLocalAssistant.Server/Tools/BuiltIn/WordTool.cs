using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using DocumentFormat.OpenXml.Packaging;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
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
            Name: "word.replace_tokens",
            Description: "Replace multiple placeholder tokens throughout a .docx template, including the main document, tables, headers, and footers. Use this after copying a customer-owned template into the work directory.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"replacements":{"type":"array","items":{"type":"object","properties":{"find":{"type":"string","description":"Placeholder text to replace, e.g. '{{CandidateName}}'."},"replace":{"type":"string","description":"Replacement text."}},"required":["find","replace"]}},"matchCase":{"type":"boolean","description":"Case-sensitive token matching. Default false."}},"required":["filename","replacements"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "word.insert_image",
            Description: "Insert an image into the document body, header, or footer. Supports replacing a placeholder paragraph in a template and writing outputs inside work-directory subfolders.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"imagePath":{"type":"string","description":"Relative path to the image inside the work directory."},"location":{"type":"string","enum":["body","header","footer"],"description":"Target document area. Default 'body'."},"sectionIndex":{"type":"integer","description":"1-based section index for header/footer placement. Defaults to the last section."},"headerType":{"type":"string","enum":["default","first"],"description":"Header/footer variant when location is header or footer. Default 'default'."},"replaceToken":{"type":"string","description":"Optional placeholder paragraph text to replace with the image."},"caption":{"type":"string","description":"Optional caption paragraph inserted after the image."},"alignment":{"type":"string","enum":["left","center","right"],"description":"Paragraph alignment. Default 'center'."},"widthInches":{"type":"number","description":"Image width in inches. Default 4.5."},"heightInches":{"type":"number","description":"Image height in inches. Default 3.0."}},"required":["filename","imagePath"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "word.set_header_footer",
            Description: "Write professional header and footer text for a section, including first-page variants.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sectionIndex":{"type":"integer","description":"1-based section index. Defaults to the last section."},"headerType":{"type":"string","enum":["default","first"],"description":"Header/footer variant. Default 'default'."},"headerText":{"type":"string","description":"Header text. Newlines create additional paragraphs. Pass an empty string to clear it."},"footerText":{"type":"string","description":"Footer text. Newlines create additional paragraphs. Pass an empty string to clear it."},"alignment":{"type":"string","enum":["left","center","right"],"description":"Paragraph alignment. Default 'left'."}},"required":["filename"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "word.set_section_layout",
            Description: "Control section-level page layout: paper size, orientation, margins, columns, and section start type.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sectionIndex":{"type":"integer","description":"1-based section index. Defaults to the last section."},"orientation":{"type":"string","enum":["portrait","landscape"]},"paperSize":{"type":"string","enum":["A3","A4","A5","Letter","Legal"]},"margins":{"type":"object","properties":{"top":{"type":"number"},"right":{"type":"number"},"bottom":{"type":"number"},"left":{"type":"number"},"header":{"type":"number"},"footer":{"type":"number"},"gutter":{"type":"number"}},"additionalProperties":false},"columns":{"type":"integer","description":"Number of newspaper-style columns for the section."},"columnSpacingInches":{"type":"number","description":"Spacing between columns in inches."},"startType":{"type":"string","enum":["nextPage","continuous","evenPage","oddPage"]}},"required":["filename"],"additionalProperties":false}"""),

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
            "word.replace_tokens" => ReplaceTokensAsync(call, ctx),
            "word.insert_image"   => InsertImageAsync(call, ctx),
            "word.set_header_footer" => SetHeaderFooterAsync(call, ctx),
            "word.set_section_layout" => SetSectionLayoutAsync(call, ctx),
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

            var headers = wdoc.MainDocumentPart?.HeaderParts
                .Select(h => h.Header?.InnerText ?? string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            var footers = wdoc.MainDocumentPart?.FooterParts
                .Select(f => f.Footer?.InnerText ?? string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            var result = JsonSerializer.Serialize(new { filename, blocks, plainText = plainSb.ToString(), headers, footers }, s_json);
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
            var comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var replacements = ReplaceTokensInDocument(wdoc, [(find, replace)], comp);
            SaveDocumentTextParts(wdoc);
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { find, replace, replacements = replacements[0].Count }, s_json)));
        }
        catch (Exception ex) { return Err($"Find/replace failed: {ex.Message}"); }
    }

    // ── word.replace_tokens ─────────────────────────────────────────────────

    private static Task<ToolResult> ReplaceTokensAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!doc.TryGetProperty("replacements", out var replacementsEl) || replacementsEl.ValueKind != JsonValueKind.Array)
            return Err("'replacements' must be an array.");

        if (!filename.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) filename += ".docx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        var replacements = new List<(string Find, string Replace)>();
        foreach (var repl in replacementsEl.EnumerateArray())
        {
            var find = repl.TryGetProperty("find", out var fEl) ? fEl.GetString() ?? string.Empty : string.Empty;
            var replace = repl.TryGetProperty("replace", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(find)) return Err("Each replacement requires a non-empty 'find' value.");
            replacements.Add((find, replace));
        }
        if (replacements.Count == 0) return Err("At least one replacement is required.");

        var comp = doc.TryGetProperty("matchCase", out var mcEl) && mcEl.GetBoolean()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var counts = ReplaceTokensInDocument(wdoc, replacements, comp);
            SaveDocumentTextParts(wdoc);

            var result = counts.Select(c => new { find = c.Find, replace = c.Replace, replacements = c.Count }).ToList();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename,
                replacements = result,
                totalReplacements = counts.Sum(c => c.Count),
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Replace tokens failed: {ex.Message}"); }
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

    // ── word.insert_image ───────────────────────────────────────────────────

    private static Task<ToolResult> InsertImageAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!doc.TryGetProperty("imagePath", out var imageEl) || imageEl.GetString() is not string imagePath) return Err("'imagePath' is required.");

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        string assetPath;
        try { assetPath = OfficeToolSupport.ResolveExistingWorkAsset(ctx.WorkDirectory, imagePath); }
        catch { return Err("Invalid imagePath."); }
        if (!File.Exists(assetPath)) return Err($"Image not found: {imagePath}");
        if (!OfficeToolSupport.TryGetImagePartType(assetPath, out var imagePartType))
            return Err("Unsupported image format. Use PNG, JPG, GIF, BMP, or TIFF.");

        var location = doc.TryGetProperty("location", out var locationEl) ? (locationEl.GetString() ?? "body").Trim().ToLowerInvariant() : "body";
        if (location is not ("body" or "header" or "footer")) return Err("location must be 'body', 'header', or 'footer'.");

        var alignment = ParseAlignment(doc, fallback: JustificationValues.Center);
        var headerType = doc.TryGetProperty("headerType", out var headerTypeEl) ? (headerTypeEl.GetString() ?? "default").Trim().ToLowerInvariant() : "default";
        if (headerType is not ("default" or "first")) return Err("headerType must be 'default' or 'first'.");

        var widthInches = doc.TryGetProperty("widthInches", out var widthEl) && widthEl.TryGetDouble(out var widthValue) && widthValue > 0
            ? widthValue
            : 4.5;
        var heightInches = doc.TryGetProperty("heightInches", out var heightEl) && heightEl.TryGetDouble(out var heightValue) && heightValue > 0
            ? heightValue
            : 3.0;

        var replaceToken = doc.TryGetProperty("replaceToken", out var tokenEl) ? tokenEl.GetString() : null;
        var caption = doc.TryGetProperty("caption", out var captionEl) ? captionEl.GetString() : null;
        var sectionIndex = doc.TryGetProperty("sectionIndex", out var sectionEl) && sectionEl.TryGetInt32(out var parsedSectionIndex)
            ? parsedSectionIndex
            : (int?)null;

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var mainPart = wdoc.MainDocumentPart ?? throw new InvalidOperationException("Document is missing a main part.");
            var body = mainPart.Document.Body ?? throw new InvalidOperationException("Document has no body.");

            OpenXmlCompositeElement container;
            OpenXmlPart ownerPart;
            switch (location)
            {
                case "body":
                    container = body;
                    ownerPart = mainPart;
                    break;
                case "header":
                    {
                        var sectionProps = GetOrCreateSectionProperties(body, sectionIndex);
                        var headerPart = GetOrCreateHeaderPart(mainPart, sectionProps, headerType == "first");
                        container = headerPart.Header ??= new Header(new Paragraph());
                        ownerPart = headerPart;
                        break;
                    }
                default:
                    {
                        var sectionProps = GetOrCreateSectionProperties(body, sectionIndex);
                        var footerPart = GetOrCreateFooterPart(mainPart, sectionProps, headerType == "first");
                        container = footerPart.Footer ??= new Footer(new Paragraph());
                        ownerPart = footerPart;
                        break;
                    }
            }

            var imagePart = AddImagePart(ownerPart, imagePartType);
            using (var stream = File.OpenRead(assetPath)) imagePart.FeedData(stream);
            var relationshipId = GetPartRelationshipId(ownerPart, imagePart);
            var drawingId = (uint)Math.Max(1, container.Descendants<Drawing>().Count() + 1);

            var imageParagraph = BuildImageParagraph(
                relationshipId,
                drawingId,
                Path.GetFileName(assetPath),
                OfficeToolSupport.InchesToEmu(widthInches),
                OfficeToolSupport.InchesToEmu(heightInches),
                alignment);

            var replaced = false;
            if (!string.IsNullOrWhiteSpace(replaceToken))
            {
                var targetParagraph = container.Descendants<Paragraph>()
                    .FirstOrDefault(p => p.InnerText.Contains(replaceToken, StringComparison.OrdinalIgnoreCase));
                if (targetParagraph is not null)
                {
                    targetParagraph.InsertAfterSelf(imageParagraph);
                    targetParagraph.Remove();
                    replaced = true;
                }
            }

            if (!replaced) container.Append(imageParagraph);
            if (!string.IsNullOrWhiteSpace(caption))
                imageParagraph.InsertAfterSelf(BuildTextParagraph(caption!, alignment));

            SaveDocumentTextParts(wdoc);
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename,
                imagePath = OfficeToolSupport.ToRelativeDisplayPath(ctx.WorkDirectory, assetPath),
                location,
                replaced,
                sectionIndex = sectionIndex ?? GetSectionProperties(body).Count,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Insert image failed: {ex.Message}"); }
    }

    // ── word.set_header_footer ──────────────────────────────────────────────

    private static Task<ToolResult> SetHeaderFooterAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        var hasHeader = doc.TryGetProperty("headerText", out var headerEl);
        var hasFooter = doc.TryGetProperty("footerText", out var footerEl);
        if (!hasHeader && !hasFooter) return Err("At least one of 'headerText' or 'footerText' is required.");

        var sectionIndex = doc.TryGetProperty("sectionIndex", out var sectionEl) && sectionEl.TryGetInt32(out var parsedSectionIndex)
            ? parsedSectionIndex
            : (int?)null;
        var headerType = doc.TryGetProperty("headerType", out var headerTypeEl) ? (headerTypeEl.GetString() ?? "default").Trim().ToLowerInvariant() : "default";
        if (headerType is not ("default" or "first")) return Err("headerType must be 'default' or 'first'.");
        var alignment = ParseAlignment(doc, fallback: JustificationValues.Left);

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var mainPart = wdoc.MainDocumentPart ?? throw new InvalidOperationException("Document is missing a main part.");
            var body = mainPart.Document.Body ?? throw new InvalidOperationException("Document has no body.");
            var sectionProps = GetOrCreateSectionProperties(body, sectionIndex);

            if (hasHeader)
            {
                var headerPart = GetOrCreateHeaderPart(mainPart, sectionProps, headerType == "first");
                headerPart.Header ??= new Header(new Paragraph());
                ReplaceContainerText(headerPart.Header, headerEl.GetString() ?? string.Empty, alignment);
                headerPart.Header.Save();
            }

            if (hasFooter)
            {
                var footerPart = GetOrCreateFooterPart(mainPart, sectionProps, headerType == "first");
                footerPart.Footer ??= new Footer(new Paragraph());
                ReplaceContainerText(footerPart.Footer, footerEl.GetString() ?? string.Empty, alignment);
                footerPart.Footer.Save();
            }

            mainPart.Document.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename,
                sectionIndex = sectionIndex ?? GetSectionProperties(body).Count,
                headerType,
                updatedHeader = hasHeader,
                updatedFooter = hasFooter,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Set header/footer failed: {ex.Message}"); }
    }

    // ── word.set_section_layout ─────────────────────────────────────────────

    private static Task<ToolResult> SetSectionLayoutAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        var sectionIndex = doc.TryGetProperty("sectionIndex", out var sectionEl) && sectionEl.TryGetInt32(out var parsedSectionIndex)
            ? parsedSectionIndex
            : (int?)null;

        try
        {
            using var wdoc = WordprocessingDocument.Open(path, true);
            var body = wdoc.MainDocumentPart?.Document.Body ?? throw new InvalidOperationException("Document has no body.");
            var sectionProps = GetOrCreateSectionProperties(body, sectionIndex);
            EnsureSectionDefaults(sectionProps);

            ApplySectionPageSize(sectionProps,
                doc.TryGetProperty("paperSize", out var paperSizeEl) ? paperSizeEl.GetString() : null,
                doc.TryGetProperty("orientation", out var orientationEl) ? orientationEl.GetString() : null);

            if (doc.TryGetProperty("margins", out var marginsEl) && marginsEl.ValueKind == JsonValueKind.Object)
                ApplySectionMargins(sectionProps, marginsEl);

            if (doc.TryGetProperty("columns", out var columnsEl) && columnsEl.TryGetInt32(out var columns) && columns > 0)
            {
                var sectionColumns = sectionProps.GetFirstChild<Columns>() ?? sectionProps.AppendChild(new Columns());
                sectionColumns.ColumnCount = (Int16Value)(short)columns;
                if (doc.TryGetProperty("columnSpacingInches", out var spacingEl) && spacingEl.TryGetDouble(out var spacingValue) && spacingValue >= 0)
                    sectionColumns.Space = (StringValue)OfficeToolSupport.InchesToTwips(spacingValue).ToString();
            }

            if (doc.TryGetProperty("startType", out var startTypeEl) && startTypeEl.GetString() is { Length: > 0 } startType)
            {
                var sectionType = sectionProps.GetFirstChild<SectionType>() ?? sectionProps.AppendChild(new SectionType());
                sectionType.Val = startType.Trim().ToLowerInvariant() switch
                {
                    "continuous" => SectionMarkValues.Continuous,
                    "evenpage" => SectionMarkValues.EvenPage,
                    "oddpage" => SectionMarkValues.OddPage,
                    _ => SectionMarkValues.NextPage,
                };
            }

            wdoc.MainDocumentPart!.Document.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename,
                sectionIndex = sectionIndex ?? GetSectionProperties(body).Count,
                orientation = sectionProps.GetFirstChild<PageSize>() is { } pageSize && pageSize.Orient is { } orient ? orient.ToString() : null,
                columns = sectionProps.GetFirstChild<Columns>()?.ColumnCount?.Value,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Set section layout failed: {ex.Message}"); }
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

    private static List<(string Find, string Replace, int Count)> ReplaceTokensInDocument(
        WordprocessingDocument wdoc,
        IReadOnlyList<(string Find, string Replace)> replacements,
        StringComparison comp)
    {
        var counts = replacements
            .Select(r => (r.Find, r.Replace, Count: 0))
            .ToList();

        foreach (var root in EnumerateTextRoots(wdoc))
        {
            foreach (var text in root.Descendants<Text>())
            {
                var current = text.Text;
                if (string.IsNullOrEmpty(current)) continue;

                for (var i = 0; i < counts.Count; i++)
                {
                    var repl = counts[i];
                    if (!current.Contains(repl.Find, comp)) continue;
                    current = current.Replace(repl.Find, repl.Replace, comp);
                    counts[i] = (repl.Find, repl.Replace, repl.Count + 1);
                }

                text.Text = current;
            }
        }

        return counts;
    }

    private static IEnumerable<OpenXmlPartRootElement> EnumerateTextRoots(WordprocessingDocument wdoc)
    {
        if (wdoc.MainDocumentPart?.Document is { } document)
            yield return document;

        if (wdoc.MainDocumentPart is null) yield break;

        foreach (var headerPart in wdoc.MainDocumentPart.HeaderParts)
            if (headerPart.Header is not null)
                yield return headerPart.Header;

        foreach (var footerPart in wdoc.MainDocumentPart.FooterParts)
            if (footerPart.Footer is not null)
                yield return footerPart.Footer;

        if (wdoc.MainDocumentPart.FootnotesPart?.Footnotes is { } footnotes)
            yield return footnotes;

        if (wdoc.MainDocumentPart.EndnotesPart?.Endnotes is { } endnotes)
            yield return endnotes;
    }

    private static void SaveDocumentTextParts(WordprocessingDocument wdoc)
    {
        wdoc.MainDocumentPart?.Document.Save();
        if (wdoc.MainDocumentPart is null) return;

        foreach (var headerPart in wdoc.MainDocumentPart.HeaderParts)
            headerPart.Header?.Save();

        foreach (var footerPart in wdoc.MainDocumentPart.FooterParts)
            footerPart.Footer?.Save();

        wdoc.MainDocumentPart.FootnotesPart?.Footnotes?.Save();
        wdoc.MainDocumentPart.EndnotesPart?.Endnotes?.Save();
    }

    private static List<SectionProperties> GetSectionProperties(Body body)
    {
        var sections = body.Elements<Paragraph>()
            .Select(p => p.ParagraphProperties?.SectionProperties)
            .Where(s => s is not null)
            .Cast<SectionProperties>()
            .ToList();

        if (body.GetFirstChild<SectionProperties>() is { } trailingSection)
            sections.Add(trailingSection);

        return sections;
    }

    private static SectionProperties GetOrCreateSectionProperties(Body body, int? sectionIndex)
    {
        var sections = GetSectionProperties(body);
        if (sections.Count == 0)
        {
            var sectionProps = new SectionProperties();
            EnsureSectionDefaults(sectionProps);
            body.Append(sectionProps);
            sections.Add(sectionProps);
        }

        var resolvedIndex = sectionIndex ?? sections.Count;
        if (resolvedIndex < 1 || resolvedIndex > sections.Count)
            throw new ArgumentOutOfRangeException(nameof(sectionIndex), $"sectionIndex must be between 1 and {sections.Count}.");

        return sections[resolvedIndex - 1];
    }

    private static void EnsureSectionDefaults(SectionProperties sectionProps)
    {
        if (sectionProps.GetFirstChild<PageSize>() is null)
        {
            sectionProps.AppendChild(new PageSize
            {
                Width = 12240U,
                Height = 15840U,
                Orient = PageOrientationValues.Portrait,
            });
        }

        if (sectionProps.GetFirstChild<PageMargin>() is null)
        {
            sectionProps.AppendChild(new PageMargin
            {
                Top = 1440,
                Right = (UInt32Value)1440U,
                Bottom = 1440,
                Left = (UInt32Value)1440U,
                Header = (UInt32Value)720U,
                Footer = (UInt32Value)720U,
                Gutter = (UInt32Value)0U,
            });
        }
    }

    private static void ApplySectionPageSize(SectionProperties sectionProps, string? paperSizeName, string? orientationName)
    {
        var pageSize = sectionProps.GetFirstChild<PageSize>() ?? sectionProps.AppendChild(new PageSize());
        var (portraitWidth, portraitHeight) = ResolvePaperSize(paperSizeName);
        var landscape = string.Equals(orientationName, "landscape", StringComparison.OrdinalIgnoreCase);

        pageSize.Width = landscape ? portraitHeight : portraitWidth;
        pageSize.Height = landscape ? portraitWidth : portraitHeight;
        pageSize.Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait;
    }

    private static void ApplySectionMargins(SectionProperties sectionProps, JsonElement marginsEl)
    {
        var pageMargin = sectionProps.GetFirstChild<PageMargin>() ?? sectionProps.AppendChild(new PageMargin());

        ApplyMargin(marginsEl, "top", value => pageMargin.Top = value);
        ApplyMargin(marginsEl, "bottom", value => pageMargin.Bottom = value);
        ApplyMargin(marginsEl, "left", value => pageMargin.Left = (UInt32Value)(uint)value);
        ApplyMargin(marginsEl, "right", value => pageMargin.Right = (UInt32Value)(uint)value);
        ApplyMargin(marginsEl, "header", value => pageMargin.Header = (UInt32Value)(uint)value);
        ApplyMargin(marginsEl, "footer", value => pageMargin.Footer = (UInt32Value)(uint)value);
        ApplyMargin(marginsEl, "gutter", value => pageMargin.Gutter = (UInt32Value)(uint)value);
    }

    private static void ApplyMargin(JsonElement marginsEl, string propertyName, Action<int> apply)
    {
        if (!marginsEl.TryGetProperty(propertyName, out var marginEl) || !marginEl.TryGetDouble(out var inches) || inches < 0)
            return;

        apply(OfficeToolSupport.InchesToTwips(inches));
    }

    private static (UInt32Value Width, UInt32Value Height) ResolvePaperSize(string? paperSizeName)
        => (paperSizeName ?? "Letter").Trim().ToUpperInvariant() switch
        {
            "A3" => (16839U, 23811U),
            "A4" => (11907U, 16839U),
            "A5" => (8391U, 11907U),
            "LEGAL" => (12240U, 20160U),
            _ => (12240U, 15840U),
        };

    private static HeaderPart GetOrCreateHeaderPart(MainDocumentPart mainPart, SectionProperties sectionProps, bool firstPage)
    {
        var targetType = firstPage ? HeaderFooterValues.First : HeaderFooterValues.Default;
        var existingReference = sectionProps.Elements<HeaderReference>().FirstOrDefault(r => r.Type?.Value == targetType);
        if (existingReference?.Id is { Value: { Length: > 0 } relationshipId } && mainPart.GetPartById(relationshipId) is HeaderPart existingPart)
            return existingPart;

        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph());
        headerPart.Header.Save();

        foreach (var staleReference in sectionProps.Elements<HeaderReference>().Where(r => r.Type?.Value == targetType).ToList())
            staleReference.Remove();

        sectionProps.PrependChild(new HeaderReference { Type = targetType, Id = mainPart.GetIdOfPart(headerPart) });
        if (firstPage && !sectionProps.Elements<TitlePage>().Any()) sectionProps.Append(new TitlePage());
        return headerPart;
    }

    private static FooterPart GetOrCreateFooterPart(MainDocumentPart mainPart, SectionProperties sectionProps, bool firstPage)
    {
        var targetType = firstPage ? HeaderFooterValues.First : HeaderFooterValues.Default;
        var existingReference = sectionProps.Elements<FooterReference>().FirstOrDefault(r => r.Type?.Value == targetType);
        if (existingReference?.Id is { Value: { Length: > 0 } relationshipId } && mainPart.GetPartById(relationshipId) is FooterPart existingPart)
            return existingPart;

        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph());
        footerPart.Footer.Save();

        foreach (var staleReference in sectionProps.Elements<FooterReference>().Where(r => r.Type?.Value == targetType).ToList())
            staleReference.Remove();

        sectionProps.PrependChild(new FooterReference { Type = targetType, Id = mainPart.GetIdOfPart(footerPart) });
        if (firstPage && !sectionProps.Elements<TitlePage>().Any()) sectionProps.Append(new TitlePage());
        return footerPart;
    }

    private static void ReplaceContainerText(OpenXmlCompositeElement container, string text, JustificationValues alignment)
    {
        foreach (var paragraph in container.Elements<Paragraph>().ToList())
            paragraph.Remove();

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
            container.Append(BuildTextParagraph(line, alignment));

        if (!container.Elements<Paragraph>().Any())
            container.Append(BuildTextParagraph(string.Empty, alignment));
    }

    private static Paragraph BuildTextParagraph(string text, JustificationValues alignment)
        => new(
            new ParagraphProperties(new Justification { Val = alignment }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph BuildImageParagraph(string relationshipId, uint drawingId, string name, long widthEmus, long heightEmus, JustificationValues alignment)
        => new(
            new ParagraphProperties(new Justification { Val = alignment }),
            new Run(CreateImageDrawing(relationshipId, drawingId, name, widthEmus, heightEmus)));

    private static Drawing CreateImageDrawing(string relationshipId, uint drawingId, string name, long widthEmus, long heightEmus)
        => new(
            new DW.Inline(
                new DW.Extent { Cx = widthEmus, Cy = heightEmus },
                new DW.EffectExtent
                {
                    LeftEdge = 0L,
                    TopEdge = 0L,
                    RightEdge = 0L,
                    BottomEdge = 0L,
                },
                new DW.DocProperties { Id = drawingId, Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = drawingId, Name = name },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmus, Cy = heightEmus }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            });

    private static ImagePart AddImagePart(OpenXmlPart ownerPart, ImagePartType imagePartType)
        => ownerPart switch
        {
            MainDocumentPart part => part.AddImagePart(imagePartType),
            HeaderPart part => part.AddImagePart(imagePartType),
            FooterPart part => part.AddImagePart(imagePartType),
            _ => throw new InvalidOperationException("Images are only supported in the main document, headers, and footers."),
        };

    private static string GetPartRelationshipId(OpenXmlPart ownerPart, OpenXmlPart childPart)
        => ownerPart switch
        {
            MainDocumentPart part => part.GetIdOfPart(childPart),
            HeaderPart part => part.GetIdOfPart(childPart),
            FooterPart part => part.GetIdOfPart(childPart),
            _ => throw new InvalidOperationException("Unable to resolve the image relationship id."),
        };

    private static JustificationValues ParseAlignment(JsonElement doc, JustificationValues fallback)
    {
        if (!doc.TryGetProperty("alignment", out var alignmentEl) || alignmentEl.GetString() is not { Length: > 0 } alignment)
            return fallback;

        return alignment.Trim().ToLowerInvariant() switch
        {
            "center" => JustificationValues.Center,
            "right" => JustificationValues.Right,
            _ => JustificationValues.Left,
        };
    }

    private static string? SafePath(string workDir, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        try { return OfficeToolSupport.ResolveWorkFile(workDir, filename, ".docx"); }
        catch { return null; }
    }

    private static Task<ToolResult> Err(string msg) =>
        Task.FromResult(ToolResult.Error(msg));
}

