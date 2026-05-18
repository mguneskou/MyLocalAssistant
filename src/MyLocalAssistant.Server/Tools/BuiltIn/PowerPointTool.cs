using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MyLocalAssistant.Shared.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using A = DocumentFormat.OpenXml.Drawing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// PowerPoint (.pptx) tool. Create, read, and manipulate presentations.
/// All files are scoped to the conversation WorkDirectory.
/// </summary>
internal sealed class PowerPointTool : ITool
{
    public string  Id          => "powerpoint";
    public string  Name        => "PowerPoint Tool";
    public string  Description => "Create and edit Microsoft PowerPoint (.pptx) presentations: create, read, add/write slides, insert tables, delete/reorder slides, and get presentation info.";
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
            Name: "powerpoint.create",
            Description: "Create a new empty .pptx presentation file with an optional title slide.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Output filename. .pptx extension added if missing."},"title":{"type":"string","description":"Title text for the first slide."},"subtitle":{"type":"string","description":"Subtitle or body text for the first slide."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.read",
            Description: "Read a .pptx file and return an array of slides with their title and body text.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.get_info",
            Description: "Return the slide count and a list of slide titles from a .pptx file.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.add_slide",
            Description: "Append a new slide to a .pptx presentation with title and optional body text.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"title":{"type":"string","description":"Slide title."},"body":{"type":"string","description":"Body or content text (newlines become bullet points)."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.write_slide",
            Description: "Set the title and/or body text of an existing slide by 1-based index.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index."},"title":{"type":"string"},"body":{"type":"string"}},"required":["filename","slideIndex"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.delete_slide",
            Description: "Delete a slide by 1-based index.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index to delete."}},"required":["filename","slideIndex"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.reorder_slide",
            Description: "Move a slide from one position to another (both 1-based).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"fromIndex":{"type":"integer","description":"Current 1-based position of the slide."},"toIndex":{"type":"integer","description":"Target 1-based position."}},"required":["filename","fromIndex","toIndex"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.duplicate_slide",
            Description: "Duplicate an existing slide so you can reuse a customer-owned template layout and then edit the copy.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index to duplicate."},"toIndex":{"type":"integer","description":"Optional 1-based insertion position for the duplicate. Omit to append after the last slide."}},"required":["filename","slideIndex"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "powerpoint.add_slide_from_template",
            Description: "Duplicate a template slide, preserve its layout/theme, and optionally replace placeholders or overwrite the title/body on the new slide.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"templateSlideIndex":{"type":"integer","description":"1-based slide index to duplicate as the template."},"toIndex":{"type":"integer","description":"Optional 1-based insertion position for the new slide."},"title":{"type":"string","description":"Optional title text for the new slide."},"body":{"type":"string","description":"Optional body text for the new slide."},"replacements":{"type":"array","description":"Optional placeholder replacements applied only to the new slide.","items":{"type":"object","properties":{"find":{"type":"string"},"replace":{"type":"string"}},"required":["find","replace"]}},"matchCase":{"type":"boolean","description":"Case-sensitive placeholder matching. Default false."}},"required":["filename","templateSlideIndex"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "powerpoint.replace_text",
            Description: "Replace multiple placeholder tokens across one or more slides in a presentation template.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"replacements":{"type":"array","items":{"type":"object","properties":{"find":{"type":"string","description":"Placeholder text to replace, e.g. '{{Quarter}}'."},"replace":{"type":"string","description":"Replacement text."}},"required":["find","replace"]}},"slideIndices":{"type":"array","description":"Optional list of 1-based slide indices to limit the replacement scope.","items":{"type":"integer"}},"matchCase":{"type":"boolean","description":"Case-sensitive token matching. Default false."}},"required":["filename","replacements"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "powerpoint.add_table",
            Description: "Insert a table onto a slide. Provide rows as an array of string arrays.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index."},"rows":{"type":"array","description":"Table data — array of rows, each an array of cell strings.","items":{"type":"array","items":{"type":"string"}}},"headerRow":{"type":"boolean","description":"Bold the first row."}},"required":["filename","slideIndex","rows"]}"""),

        new ToolFunctionDto(
            Name: "powerpoint.add_image",
            Description: "Insert an image onto a slide from the work directory, including assets stored in subfolders.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index."},"imagePath":{"type":"string","description":"Relative image path inside the work directory."},"x":{"type":"number","description":"Left position in inches. Default 0.8."},"y":{"type":"number","description":"Top position in inches. Default 1.2."},"width":{"type":"number","description":"Width in inches. Default 3.0."},"height":{"type":"number","description":"Height in inches. Default 2.25."},"altText":{"type":"string","description":"Optional alternative text/description."}},"required":["filename","slideIndex","imagePath"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "powerpoint.apply_branding",
            Description: "Apply deterministic slide branding: background color, title/body text colors, accent band, and footer text across one or more slides.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndices":{"type":"array","description":"Optional list of 1-based slide indices to brand. Omit to apply to all slides.","items":{"type":"integer"}},"backgroundColor":{"type":"string","description":"Optional hex background color, e.g. '#F5F1E8'."},"titleColor":{"type":"string","description":"Optional hex color for the primary title text."},"bodyColor":{"type":"string","description":"Optional hex color for normal body text."},"accentColor":{"type":"string","description":"Optional hex accent color for the footer band."},"footerText":{"type":"string","description":"Optional footer text. Pass an empty string to remove an existing branded footer."},"footerColor":{"type":"string","description":"Optional hex color for the footer text."}},"required":["filename"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "powerpoint.add_chart",
            Description: "Draw a presentation-grade chart graphic on a slide using native shapes and rendered chart images. Supports column, bar, stacked column, stacked bar, line, and pie charts.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index."},"chartType":{"type":"string","enum":["column","bar","stackedColumn","stackedBar","line","pie"],"description":"Chart orientation/style. Default 'column'."},"title":{"type":"string","description":"Optional chart title."},"categories":{"type":"array","items":{"type":"string"},"description":"Category labels."},"series":{"type":"array","description":"Series data.","items":{"type":"object","properties":{"name":{"type":"string"},"values":{"type":"array","items":{"type":"number"}}},"required":["name","values"]}},"x":{"type":"number","description":"Left position in inches. Default 0.8."},"y":{"type":"number","description":"Top position in inches. Default 1.0."},"width":{"type":"number","description":"Chart width in inches. Default 10.5."},"height":{"type":"number","description":"Chart height in inches. Default 5.5."}},"required":["filename","slideIndex","categories","series"],"additionalProperties":false}"""),
    };

    public void Configure(string? configJson) { /* no per-instance config */ }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        return call.ToolName switch
        {
            "powerpoint.create"      => CreateAsync(call, ctx),
            "powerpoint.read"        => ReadAsync(call, ctx),
            "powerpoint.get_info"    => GetInfoAsync(call, ctx),
            "powerpoint.add_slide"   => AddSlideAsync(call, ctx),
            "powerpoint.write_slide" => WriteSlideAsync(call, ctx),
            "powerpoint.delete_slide"=> DeleteSlideAsync(call, ctx),
            "powerpoint.reorder_slide"=> ReorderSlideAsync(call, ctx),
            "powerpoint.duplicate_slide" => DuplicateSlideAsync(call, ctx),
            "powerpoint.add_slide_from_template" => AddSlideFromTemplateAsync(call, ctx),
            "powerpoint.replace_text" => ReplaceTextAsync(call, ctx),
            "powerpoint.add_table"   => AddTableAsync(call, ctx),
            "powerpoint.add_image"   => AddImageAsync(call, ctx),
            "powerpoint.apply_branding" => ApplyBrandingAsync(call, ctx),
            "powerpoint.add_chart"   => AddChartAsync(call, ctx),
            _ => Task.FromResult(ToolResult.Error($"Unknown powerpoint tool: {call.ToolName}")),
        };
    }

    // ── powerpoint.create ────────────────────────────────────────────────────

    private static Task<ToolResult> CreateAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!filename.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) filename += ".pptx";
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");

        var title    = doc.TryGetProperty("title",    out var tEl) ? tEl.GetString() : null;
        var subtitle = doc.TryGetProperty("subtitle", out var sEl) ? sEl.GetString() : null;

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            CreateMinimalPresentation(path, title, subtitle);
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { filename, success = true }, s_json),
                $"Created '{filename}'."));
        }
        catch (Exception ex) { return Err($"Create failed: {ex.Message}"); }
    }

    // ── powerpoint.read ──────────────────────────────────────────────────────

    private static Task<ToolResult> ReadAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");

        try
        {
            using var pres = PresentationDocument.Open(path, false);
            var slides = ExtractSlides(pres);
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { slideCount = slides.Count, slides }, s_json)));
        }
        catch (Exception ex) { return Err($"Read failed: {ex.Message}"); }
    }

    // ── powerpoint.get_info ──────────────────────────────────────────────────

    private static Task<ToolResult> GetInfoAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");

        try
        {
            using var pres = PresentationDocument.Open(path, false);
            var slides = ExtractSlides(pres);
            var titles = slides.Select(s => s.title).ToList();
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { slideCount = slides.Count, titles }, s_json)));
        }
        catch (Exception ex) { return Err($"Get info failed: {ex.Message}"); }
    }

    // ── powerpoint.add_slide ─────────────────────────────────────────────────

    private static Task<ToolResult> AddSlideAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");

        var title = doc.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
        var body  = doc.TryGetProperty("body",  out var bEl) ? bEl.GetString() : null;

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideIndex = AppendSlide(pres, title, body);
            pres.PresentationPart!.Presentation.Save();
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { addedSlideIndex = slideIndex }, s_json)));
        }
        catch (Exception ex) { return Err($"Add slide failed: {ex.Message}"); }
    }

    // ── powerpoint.write_slide ───────────────────────────────────────────────

    private static Task<ToolResult> WriteSlideAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");
        if (!doc.TryGetProperty("slideIndex", out var siEl) || !siEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' (1-based integer) is required.");

        var title = doc.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
        var body  = doc.TryGetProperty("body",  out var bEl) ? bEl.GetString() : null;

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            if (slideIndex < 1 || slideIndex > slideParts.Count)
                return Err($"slideIndex {slideIndex} is out of range (1–{slideParts.Count}).");
            var slidePart = slideParts[slideIndex - 1];
            SetSlideText(slidePart, title, body);
            slidePart.Slide.Save();
            return Task.FromResult(ToolResult.Ok($"Slide {slideIndex} updated."));
        }
        catch (Exception ex) { return Err($"Write slide failed: {ex.Message}"); }
    }

    // ── powerpoint.delete_slide ──────────────────────────────────────────────

    private static Task<ToolResult> DeleteSlideAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");
        if (!doc.TryGetProperty("slideIndex", out var siEl) || !siEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' is required.");

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var presEl = pres.PresentationPart!.Presentation;
            var slideIdList = presEl.SlideIdList ?? throw new InvalidOperationException("No slides.");
            var slideIds = slideIdList.Elements<SlideId>().ToList();
            if (slideIndex < 1 || slideIndex > slideIds.Count)
                return Err($"slideIndex {slideIndex} is out of range (1–{slideIds.Count}).");

            var slideId = slideIds[slideIndex - 1];
            var slidePart = (SlidePart)pres.PresentationPart.GetPartById(slideId.RelationshipId!.Value!);
            slideId.Remove();
            pres.PresentationPart.DeletePart(slidePart);
            presEl.Save();
            return Task.FromResult(ToolResult.Ok($"Deleted slide {slideIndex}."));
        }
        catch (Exception ex) { return Err($"Delete slide failed: {ex.Message}"); }
    }

    // ── powerpoint.reorder_slide ─────────────────────────────────────────────

    private static Task<ToolResult> ReorderSlideAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");
        if (!doc.TryGetProperty("fromIndex", out var fromEl) || !fromEl.TryGetInt32(out var fromIndex)) return Err("'fromIndex' is required.");
        if (!doc.TryGetProperty("toIndex",   out var toEl)   || !toEl.TryGetInt32(out var toIndex))   return Err("'toIndex' is required.");

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var presEl = pres.PresentationPart!.Presentation;
            var slideIdList = presEl.SlideIdList ?? throw new InvalidOperationException("No slides.");
            var slideIds = slideIdList.Elements<SlideId>().ToList();
            int count = slideIds.Count;
            if (fromIndex < 1 || fromIndex > count) return Err($"fromIndex {fromIndex} out of range.");
            if (toIndex   < 1 || toIndex   > count) return Err($"toIndex {toIndex} out of range.");
            if (fromIndex == toIndex) return Task.FromResult(ToolResult.Ok("No change — source and target are the same."));

            var slideId = slideIds[fromIndex - 1];
            slideId.Remove();
            slideIds.RemoveAt(fromIndex - 1);
            int insertAt = toIndex - 1;
            if (insertAt >= slideIds.Count)
                slideIdList.Append(slideId);
            else
                slideIdList.InsertBefore(slideId, slideIds[insertAt]);
            presEl.Save();
            return Task.FromResult(ToolResult.Ok($"Moved slide from position {fromIndex} to {toIndex}."));
        }
        catch (Exception ex) { return Err($"Reorder slide failed: {ex.Message}"); }
    }

    // ── powerpoint.duplicate_slide ──────────────────────────────────────────

    private static Task<ToolResult> DuplicateSlideAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");
        if (!doc.TryGetProperty("slideIndex", out var siEl) || !siEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' is required.");

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var presPart = pres.PresentationPart!;
            var presEl = presPart.Presentation;
            var slideIdList = presEl.SlideIdList ?? throw new InvalidOperationException("No slides.");
            var slideIds = slideIdList.Elements<SlideId>().ToList();
            if (slideIndex < 1 || slideIndex > slideIds.Count)
                return Err($"slideIndex {slideIndex} is out of range (1–{slideIds.Count}).");

            int insertAt = doc.TryGetProperty("toIndex", out var toEl) && toEl.TryGetInt32(out var toIndex)
                ? toIndex - 1
                : slideIds.Count;
            if (insertAt < 0 || insertAt > slideIds.Count)
                return Err($"toIndex must be between 1 and {slideIds.Count + 1}.");

            var sourceSlidePart = (SlidePart)presPart.GetPartById(slideIds[slideIndex - 1].RelationshipId!.Value!);
            var clonedSlidePart = CloneSlidePart(presPart, sourceSlidePart);

            uint nextId = slideIds.Select(s => s.Id?.Value ?? 256U).DefaultIfEmpty(256U).Max() + 1;
            var newSlideId = new SlideId
            {
                Id = nextId,
                RelationshipId = presPart.GetIdOfPart(clonedSlidePart),
            };

            if (insertAt >= slideIds.Count)
                slideIdList.Append(newSlideId);
            else
                slideIdList.InsertBefore(newSlideId, slideIds[insertAt]);

            presEl.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                duplicatedFrom = slideIndex,
                newSlideIndex = insertAt + 1,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Duplicate slide failed: {ex.Message}"); }
    }

    // ── powerpoint.add_slide_from_template ─────────────────────────────────

    private static Task<ToolResult> AddSlideFromTemplateAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");
        if (!doc.TryGetProperty("templateSlideIndex", out var templateEl) || !templateEl.TryGetInt32(out var templateSlideIndex))
            return Err("'templateSlideIndex' is required.");

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var presPart = pres.PresentationPart!;
            var presEl = presPart.Presentation;
            var slideIdList = presEl.SlideIdList ?? throw new InvalidOperationException("No slides.");
            var slideIds = slideIdList.Elements<SlideId>().ToList();
            if (templateSlideIndex < 1 || templateSlideIndex > slideIds.Count)
                return Err($"templateSlideIndex {templateSlideIndex} is out of range (1–{slideIds.Count}).");

            int insertAt = doc.TryGetProperty("toIndex", out var toEl) && toEl.TryGetInt32(out var toIndex)
                ? toIndex - 1
                : slideIds.Count;
            if (insertAt < 0 || insertAt > slideIds.Count)
                return Err($"toIndex must be between 1 and {slideIds.Count + 1}.");

            var sourceSlidePart = (SlidePart)presPart.GetPartById(slideIds[templateSlideIndex - 1].RelationshipId!.Value!);
            var clonedSlidePart = CloneSlidePart(presPart, sourceSlidePart);
            uint nextId = slideIds.Select(s => s.Id?.Value ?? 256U).DefaultIfEmpty(256U).Max() + 1;
            var newSlideId = new SlideId
            {
                Id = nextId,
                RelationshipId = presPart.GetIdOfPart(clonedSlidePart),
            };

            if (insertAt >= slideIds.Count)
                slideIdList.Append(newSlideId);
            else
                slideIdList.InsertBefore(newSlideId, slideIds[insertAt]);

            var title = doc.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            var body = doc.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body))
                SetSlideText(clonedSlidePart, title, body);

            var replacements = ParseReplacements(doc, requireArray: false, out var replacementError);
            if (replacementError is not null) return Err(replacementError);
            var comparison = doc.TryGetProperty("matchCase", out var matchCaseEl) && matchCaseEl.GetBoolean()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var counts = ApplyTextReplacements([clonedSlidePart], replacements, comparison);

            clonedSlidePart.Slide.Save();
            presEl.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                templateSlideIndex,
                newSlideIndex = insertAt + 1,
                totalReplacements = counts.Sum(c => c.Count),
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Add slide from template failed: {ex.Message}"); }
    }

    // ── powerpoint.replace_text ─────────────────────────────────────────────

    private static Task<ToolResult> ReplaceTextAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");
        var replacements = ParseReplacements(doc, requireArray: true, out var replacementError);
        if (replacementError is not null) return Err(replacementError);

        var comp = doc.TryGetProperty("matchCase", out var mcEl) && mcEl.GetBoolean()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            var targetSlides = ResolveSlideSelection(doc, slideParts);
            var counts = ApplyTextReplacements(targetSlides, replacements, comp);

            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                slideCount = targetSlides.Count,
                replacements = counts.Select(c => new { find = c.Find, replace = c.Replace, replacements = c.Count }),
                totalReplacements = counts.Sum(c => c.Count),
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Replace text failed: {ex.Message}"); }
    }

    // ── powerpoint.add_table ─────────────────────────────────────────────────

    private static Task<ToolResult> AddTableAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err($"File not found.");
        if (!doc.TryGetProperty("slideIndex", out var siEl) || !siEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' is required.");
        if (!doc.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            return Err("'rows' must be an array.");
        var hasHeader = doc.TryGetProperty("headerRow", out var hrEl) && hrEl.GetBoolean();

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            if (slideIndex < 1 || slideIndex > slideParts.Count)
                return Err($"slideIndex {slideIndex} out of range.");
            var slidePart = slideParts[slideIndex - 1];
            AddTableToSlide(slidePart, rowsEl, hasHeader);
            slidePart.Slide.Save();
            return Task.FromResult(ToolResult.Ok($"Table added to slide {slideIndex}."));
        }
        catch (Exception ex) { return Err($"Add table failed: {ex.Message}"); }
    }

    // ── powerpoint.add_image ────────────────────────────────────────────────

    private static Task<ToolResult> AddImageAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");
        if (!doc.TryGetProperty("slideIndex", out var slideIndexEl) || !slideIndexEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' is required.");
        if (!doc.TryGetProperty("imagePath", out var imagePathEl) || imagePathEl.GetString() is not string imagePath)
            return Err("'imagePath' is required.");

        string assetPath;
        try { assetPath = OfficeToolSupport.ResolveExistingWorkAsset(ctx.WorkDirectory, imagePath); }
        catch { return Err("Invalid imagePath."); }
        if (!File.Exists(assetPath)) return Err("Image not found.");
        if (!OfficeToolSupport.TryGetImagePartType(assetPath, out var imagePartType))
            return Err("Unsupported image format. Use PNG, JPG, GIF, BMP, or TIFF.");

        var x = doc.TryGetProperty("x", out var xEl) && xEl.TryGetDouble(out var xValue) ? xValue : 0.8;
        var y = doc.TryGetProperty("y", out var yEl) && yEl.TryGetDouble(out var yValue) ? yValue : 1.2;
        var width = doc.TryGetProperty("width", out var widthEl) && widthEl.TryGetDouble(out var widthValue) ? widthValue : 3.0;
        var height = doc.TryGetProperty("height", out var heightEl) && heightEl.TryGetDouble(out var heightValue) ? heightValue : 2.25;
        var altText = doc.TryGetProperty("altText", out var altTextEl) ? altTextEl.GetString() : null;

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            if (slideIndex < 1 || slideIndex > slideParts.Count)
                return Err($"slideIndex {slideIndex} is out of range (1–{slideParts.Count}).");
            var slidePart = slideParts[slideIndex - 1];

            var imagePart = slidePart.AddImagePart(imagePartType);
            using (var stream = File.OpenRead(assetPath)) imagePart.FeedData(stream);
            var relationshipId = slidePart.GetIdOfPart(imagePart);
            var picture = CreatePicture(
                GetNextShapeId(slidePart),
                Path.GetFileName(assetPath),
                altText,
                relationshipId,
                OfficeToolSupport.InchesToEmu(x),
                OfficeToolSupport.InchesToEmu(y),
                OfficeToolSupport.InchesToEmu(width),
                OfficeToolSupport.InchesToEmu(height));

            slidePart.Slide.CommonSlideData!.ShapeTree!.Append(picture);
            slidePart.Slide.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                slideIndex,
                imagePath = OfficeToolSupport.ToRelativeDisplayPath(ctx.WorkDirectory, assetPath),
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Add image failed: {ex.Message}"); }
    }

    // ── powerpoint.apply_branding ───────────────────────────────────────────

    private static Task<ToolResult> ApplyBrandingAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");

        var hasBackground = TryGetOptionalHexColor(doc, "backgroundColor", out var backgroundColor, out var backgroundError);
        if (backgroundError is not null) return Err(backgroundError);
        var hasTitle = TryGetOptionalHexColor(doc, "titleColor", out var titleColor, out var titleError);
        if (titleError is not null) return Err(titleError);
        var hasBody = TryGetOptionalHexColor(doc, "bodyColor", out var bodyColor, out var bodyError);
        if (bodyError is not null) return Err(bodyError);
        var hasAccent = TryGetOptionalHexColor(doc, "accentColor", out var accentColor, out var accentError);
        if (accentError is not null) return Err(accentError);
        var hasFooterColor = TryGetOptionalHexColor(doc, "footerColor", out var footerColor, out var footerColorError);
        if (footerColorError is not null) return Err(footerColorError);

        var managesFooter = doc.TryGetProperty("footerText", out var footerEl);
        var footerText = managesFooter ? footerEl.GetString() ?? string.Empty : null;

        if (!hasBackground && !hasTitle && !hasBody && !hasAccent && !hasFooterColor && !managesFooter)
            return Err("Provide at least one branding property to apply.");

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            var targetSlides = ResolveSlideSelection(doc, slideParts);
            var slideSize = pres.PresentationPart?.Presentation.SlideSize;
            var slideWidth = slideSize?.Cx?.Value ?? 12192000L;
            var slideHeight = slideSize?.Cy?.Value ?? 6858000L;

            foreach (var slidePart in targetSlides)
            {
                ApplyBrandingToSlide(slidePart, slideWidth, slideHeight, backgroundColor, titleColor, bodyColor, accentColor, managesFooter, footerText, footerColor);
                slidePart.Slide.Save();
            }

            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                slideCount = targetSlides.Count,
                backgroundApplied = hasBackground,
                textColorsApplied = hasTitle || hasBody,
                accentApplied = hasAccent,
                footerManaged = managesFooter,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Apply branding failed: {ex.Message}"); }
    }

    // ── powerpoint.add_chart ────────────────────────────────────────────────

    private static Task<ToolResult> AddChartAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        var path = ResolveFile(doc, ctx);
        if (path is null) return Err("Invalid or missing filename.");
        if (!File.Exists(path)) return Err("File not found.");
        if (!doc.TryGetProperty("slideIndex", out var slideIndexEl) || !slideIndexEl.TryGetInt32(out var slideIndex))
            return Err("'slideIndex' is required.");
        if (!doc.TryGetProperty("categories", out var categoriesEl) || categoriesEl.ValueKind != JsonValueKind.Array)
            return Err("'categories' must be an array.");
        if (!doc.TryGetProperty("series", out var seriesEl) || seriesEl.ValueKind != JsonValueKind.Array)
            return Err("'series' must be an array.");

        var categories = categoriesEl.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
        if (categories.Count == 0) return Err("At least one category is required.");

        var series = new List<SlideChartSeries>();
        foreach (var seriesSpec in seriesEl.EnumerateArray())
        {
            var name = seriesSpec.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return Err("Each series requires a name.");
            if (!seriesSpec.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
                return Err($"Series '{name}' must include a values array.");
            var values = valuesEl.EnumerateArray().Select(v => v.GetDouble()).ToList();
            if (values.Count != categories.Count)
                return Err($"Series '{name}' has {values.Count} values but there are {categories.Count} categories.");
            if (values.Any(v => v < 0)) return Err("powerpoint.add_chart currently supports non-negative values only.");
            series.Add(new SlideChartSeries(name!, values));
        }
        if (series.Count == 0) return Err("At least one series is required.");

        var chartType = doc.TryGetProperty("chartType", out var chartTypeEl) ? (chartTypeEl.GetString() ?? "column").Trim().ToLowerInvariant() : "column";
        if (chartType is not ("column" or "bar" or "stackedcolumn" or "stackedbar" or "line" or "pie"))
            return Err("chartType must be 'column', 'bar', 'stackedColumn', 'stackedBar', 'line', or 'pie'.");
        if (chartType == "pie" && series.Count != 1)
            return Err("pie charts require exactly one series.");

        var title = doc.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var x = doc.TryGetProperty("x", out var xEl) && xEl.TryGetDouble(out var xValue) ? xValue : 0.8;
        var y = doc.TryGetProperty("y", out var yEl) && yEl.TryGetDouble(out var yValue) ? yValue : 1.0;
        var width = doc.TryGetProperty("width", out var widthEl) && widthEl.TryGetDouble(out var widthValue) ? widthValue : 10.5;
        var height = doc.TryGetProperty("height", out var heightEl) && heightEl.TryGetDouble(out var heightValue) ? heightValue : 5.5;

        try
        {
            using var pres = PresentationDocument.Open(path, true);
            var slideParts = GetSlideParts(pres);
            if (slideIndex < 1 || slideIndex > slideParts.Count)
                return Err($"slideIndex {slideIndex} is out of range (1–{slideParts.Count}).");
            var slidePart = slideParts[slideIndex - 1];
            AddChartToSlide(slidePart, chartType, title, categories, series, x, y, width, height);
            slidePart.Slide.Save();
            return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(new
            {
                slideIndex,
                chartType,
                categoryCount = categories.Count,
                seriesCount = series.Count,
            }, s_json)));
        }
        catch (Exception ex) { return Err($"Add chart failed: {ex.Message}"); }
    }

    // ── OpenXml helpers ───────────────────────────────────────────────────────

    /// <summary>Creates a minimal single-slide .pptx from scratch.</summary>
    private static void CreateMinimalPresentation(string path, string? title, string? subtitle)
    {
        using var pres = PresentationDocument.Create(path, PresentationDocumentType.Presentation);

        // Presentation part
        var presPart = pres.AddPresentationPart();
        presPart.Presentation = new Presentation();

        // Slide size (widescreen 13.33" × 7.5" in EMUs)
        presPart.Presentation.SlideSize = new SlideSize
        {
            Cx = 12192000,
            Cy = 6858000,
            Type = SlideSizeValues.Screen16x9,
        };
        presPart.Presentation.NotesSize = new NotesSize { Cx = 6858000, Cy = 9144000 };
        presPart.Presentation.SlideIdList = new SlideIdList();
        presPart.Presentation.SlideMasterIdList = new SlideMasterIdList();

        // Slide master (minimal)
        var masterPart = presPart.AddNewPart<SlideMasterPart>("rId1");
        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = "" },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1       = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2       = A.ColorSchemeIndexValues.Dark2,
                Accent1     = A.ColorSchemeIndexValues.Accent1,
                Accent2     = A.ColorSchemeIndexValues.Accent2,
                Accent3     = A.ColorSchemeIndexValues.Accent3,
                Accent4     = A.ColorSchemeIndexValues.Accent4,
                Accent5     = A.ColorSchemeIndexValues.Accent5,
                Accent6     = A.ColorSchemeIndexValues.Accent6,
                Hyperlink   = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList());

        // Slide layout (minimal)
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rId1");
        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = "" },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))));
        layoutPart.SlideLayout.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

        masterPart.SlideMaster.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

        var masterSlideId = new SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" };
        presPart.Presentation.SlideMasterIdList.Append(masterSlideId);

        // First slide
        AppendSlide(pres, title ?? "Presentation", subtitle);
        presPart.Presentation.Save();
    }

    /// <summary>Appends a new slide to an open presentation and returns its 1-based index.</summary>
    private static int AppendSlide(PresentationDocument pres, string? title, string? body)
    {
        var presPart = pres.PresentationPart!;
        var slidePart = presPart.AddNewPart<SlidePart>();

        slidePart.Slide = BuildSlide(title, body);
        slidePart.Slide.AddNamespaceDeclaration("a",   "http://schemas.openxmlformats.org/drawingml/2006/main");
        slidePart.Slide.AddNamespaceDeclaration("p",   "http://schemas.openxmlformats.org/presentationml/2006/main");
        slidePart.Slide.AddNamespaceDeclaration("r",   "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

        // Link to layout (first available)
        var layoutPart = presPart.SlideMasterParts.FirstOrDefault()?.SlideLayoutParts.FirstOrDefault();
        if (layoutPart is not null)
            slidePart.AddPart(layoutPart);

        // Register in SlideIdList
        var slideIdList = presPart.Presentation.SlideIdList ??= new SlideIdList();
        uint maxId = slideIdList.Elements<SlideId>().Select(s => s.Id?.Value ?? 256U).DefaultIfEmpty(256U).Max();
        var newId = new SlideId
        {
            Id = maxId + 1,
            RelationshipId = presPart.GetIdOfPart(slidePart),
        };
        slideIdList.Append(newId);
        return slideIdList.Elements<SlideId>().Count();
    }

    private static Slide BuildSlide(string? title, string? body)
    {
        var spTree = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = "" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));

        if (title is not null)
            spTree.Append(MakeTextBox(2, "Title", title, 457200, 274638, 8229600, 1143000, bold: true, fontSize: 3600, placeholderType: PlaceholderValues.Title));
        if (body is not null)
            spTree.Append(MakeTextBox(3, "Content", body, 457200, 1600200, 8229600, 4525963, bold: false, fontSize: 2400, placeholderType: PlaceholderValues.Body));

        return new Slide(new CommonSlideData(spTree), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static Shape MakeTextBox(uint id, string name, string text, long x, long y, long cx, long cy, bool bold, int fontSize, PlaceholderValues? placeholderType = null)
    {
        var lines = text.Split('\n');
        var txBody = new TextBody(
            new A.BodyProperties(),
            new A.ListStyle());
        foreach (var line in lines)
        {
            var run = new A.Run(new A.Text(line));
            run.RunProperties = new A.RunProperties { Language = "en-US", FontSize = fontSize };
            if (bold) run.RunProperties.Bold = true;
            txBody.Append(new A.Paragraph(run));
        }

        var applicationProps = placeholderType.HasValue
            ? new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = placeholderType.Value })
            : new ApplicationNonVisualDrawingProperties(new PlaceholderShape());

        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                applicationProps),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy })),
            txBody);
    }

    private static void SetSlideText(SlidePart slidePart, string? title, string? body)
    {
        var titleShape = ResolveTitleShape(slidePart);
        var bodyShape = ResolveBodyShapes(slidePart).FirstOrDefault();

        if (title is not null && titleShape is not null)
            ReplaceShapeText(titleShape, title);
        if (body is not null && bodyShape is not null)
            ReplaceShapeText(bodyShape, body);
    }

    private static void ReplaceShapeText(Shape shape, string newText)
    {
        var txBody = shape.TextBody;
        if (txBody is null) return;
        // Remove all existing paragraphs
        foreach (var p in txBody.Elements<A.Paragraph>().ToList()) p.Remove();
        // Add one per line
        foreach (var line in newText.Split('\n'))
            txBody.Append(new A.Paragraph(new A.Run(new A.Text(line))));
    }

    private static void AddTableToSlide(SlidePart slidePart, JsonElement rowsEl, bool hasHeader)
    {
        // Build A.Table
        var tbl = new A.Table();
        var tblPr = new A.TableProperties { FirstRow = hasHeader };
        tbl.Append(tblPr);

        // TableGrid: infer column count from first row
        var firstRow = rowsEl.EnumerateArray().FirstOrDefault();
        int colCount = firstRow.ValueKind == JsonValueKind.Array
            ? firstRow.EnumerateArray().Count()
            : 1;
        var tblGrid = new A.TableGrid();
        for (int c = 0; c < colCount; c++)
            tblGrid.Append(new A.GridColumn { Width = 1800000L }); // ~2" per column
        tbl.Append(tblGrid);

        int rowIdx = 0;
        foreach (var rowEl in rowsEl.EnumerateArray())
        {
            bool isHeader = hasHeader && rowIdx == 0;
            var tr = new A.TableRow { Height = 370840L };
            if (rowEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var cellEl in rowEl.EnumerateArray())
                {
                    var cellText = cellEl.GetString() ?? "";
                    var run = new A.Run(new A.Text(cellText));
                    if (isHeader) run.RunProperties = new A.RunProperties { Bold = true };
                    var tc = new A.TableCell(
                        new A.TextBody(
                            new A.BodyProperties(),
                            new A.ListStyle(),
                            new A.Paragraph(run)),
                        new A.TableCellProperties());
                    tr.Append(tc);
                }
            }
            tbl.Append(tr);
            rowIdx++;
        }

        // Wrap in GraphicFrame
        uint nextId = (uint)(slidePart.Slide.CommonSlideData?.ShapeTree?.Elements<Shape>().Count() ?? 3) + 4;
        var graphicFrame = new GraphicFrame(
            new NonVisualGraphicFrameProperties(
                new NonVisualDrawingProperties { Id = nextId, Name = "Table " + nextId },
                new NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new Transform(
                new A.Offset { X = 457200L, Y = 2743200L },
                new A.Extents { Cx = 8229600L, Cy = 2057400L }),
            new A.Graphic(
                new A.GraphicData(tbl) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));

        slidePart.Slide.CommonSlideData!.ShapeTree!.Append(graphicFrame);
    }

    private static Picture CreatePicture(uint id, string name, string? description, string relationshipId, long x, long y, long cx, long cy)
        => new(
            new NonVisualPictureProperties(
                new NonVisualDrawingProperties { Id = id, Name = name, Description = description },
                new NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new BlipFill(
                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                new A.Stretch(new A.FillRectangle())),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

    private static void AddChartToSlide(SlidePart slidePart, string chartType, string? title, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, double x, double y, double width, double height)
    {
        var shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree ?? throw new InvalidOperationException("Slide has no shape tree.");
        var nextId = GetNextShapeId(slidePart);
        var xEmu = OfficeToolSupport.InchesToEmu(x);
        var yEmu = OfficeToolSupport.InchesToEmu(y);
        var widthEmu = OfficeToolSupport.InchesToEmu(width);
        var heightEmu = OfficeToolSupport.InchesToEmu(height);

        if (!string.IsNullOrWhiteSpace(title))
        {
            shapeTree.Append(MakePlainTextBox(nextId++, "Chart Title", title!, xEmu, yEmu, widthEmu, OfficeToolSupport.InchesToEmu(0.45), true, 2000));
            yEmu += OfficeToolSupport.InchesToEmu(0.55);
            heightEmu -= OfficeToolSupport.InchesToEmu(0.55);
        }

        var legendHeight = series.Count > 1 ? OfficeToolSupport.InchesToEmu(0.45) : 0L;
        var plotX = xEmu + OfficeToolSupport.InchesToEmu(0.3);
        var plotY = yEmu + OfficeToolSupport.InchesToEmu(0.15);
        var plotWidth = widthEmu - OfficeToolSupport.InchesToEmu(0.6);
        var plotHeight = heightEmu - OfficeToolSupport.InchesToEmu(0.8) - legendHeight;
        var palette = new[] { "0E5A7A", "F28F3B", "5B8E55", "C8553D", "6C5B7B", "2A9D8F" };
        var maxValue = chartType switch
        {
            "stackedcolumn" or "stackedbar" => Math.Max(1d, Enumerable.Range(0, categories.Count)
                .Select(categoryIndex => series.Sum(s => s.Values[categoryIndex]))
                .DefaultIfEmpty(0d)
                .Max()),
            _ => Math.Max(1d, series.SelectMany(s => s.Values).DefaultIfEmpty(0d).Max()),
        };

        switch (chartType)
        {
            case "line":
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis X", plotX, plotY + plotHeight, plotWidth, 18000L, "2D3142"));
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis Y", plotX, plotY, 18000L, plotHeight, "2D3142"));
                InsertRenderedChartImage(slidePart, ref nextId, RenderLineChartImage(960, 520, categories, series, palette), "Chart Surface Line", plotX, plotY, plotWidth, plotHeight);
                AppendBottomCategoryLabels(shapeTree, ref nextId, plotX, plotY + plotHeight + OfficeToolSupport.InchesToEmu(0.08), plotWidth, categories);
                break;
            case "pie":
                InsertRenderedChartImage(slidePart, ref nextId, RenderPieChartImage(720, 520, categories, series[0].Values, palette), "Chart Surface Pie", plotX, plotY, plotWidth, plotHeight);
                break;
            case "bar":
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis X", plotX, plotY + plotHeight, plotWidth, 18000L, "2D3142"));
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis Y", plotX, plotY, 18000L, plotHeight, "2D3142"));
                AppendBarChartShapes(shapeTree, ref nextId, plotX, plotY, plotWidth, plotHeight, categories, series, maxValue, palette);
                break;
            case "stackedbar":
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis X", plotX, plotY + plotHeight, plotWidth, 18000L, "2D3142"));
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis Y", plotX, plotY, 18000L, plotHeight, "2D3142"));
                AppendStackedBarChartShapes(shapeTree, ref nextId, plotX, plotY, plotWidth, plotHeight, categories, series, maxValue, palette);
                break;
            case "stackedcolumn":
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis X", plotX, plotY + plotHeight, plotWidth, 18000L, "2D3142"));
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis Y", plotX, plotY, 18000L, plotHeight, "2D3142"));
                AppendStackedColumnChartShapes(shapeTree, ref nextId, plotX, plotY, plotWidth, plotHeight, categories, series, maxValue, palette);
                break;
            default:
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis X", plotX, plotY + plotHeight, plotWidth, 18000L, "2D3142"));
                shapeTree.Append(MakeFilledShape(nextId++, "Chart Axis Y", plotX, plotY, 18000L, plotHeight, "2D3142"));
                AppendColumnChartShapes(shapeTree, ref nextId, plotX, plotY, plotWidth, plotHeight, categories, series, maxValue, palette);
                break;
        }

        if (chartType == "pie")
            AppendLegendItems(shapeTree, ref nextId, xEmu, yEmu + heightEmu - OfficeToolSupport.InchesToEmu(0.45), widthEmu, OfficeToolSupport.InchesToEmu(0.45), categories, palette);
        else if (series.Count > 1)
            AppendLegend(shapeTree, ref nextId, xEmu, yEmu + heightEmu - legendHeight, widthEmu, legendHeight, series, palette);
    }

    private static void AppendColumnChartShapes(ShapeTree shapeTree, ref uint nextId, long plotX, long plotY, long plotWidth, long plotHeight, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, double maxValue, IReadOnlyList<string> palette)
    {
        long groupWidth = plotWidth / Math.Max(1, categories.Count);
        long innerGap = Math.Max(40000L, groupWidth / 8);
        long barWidth = Math.Max(50000L, (groupWidth - innerGap * (series.Count + 1)) / Math.Max(1, series.Count));

        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            long groupStart = plotX + categoryIndex * groupWidth;
            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var value = series[seriesIndex].Values[categoryIndex];
                var barHeight = (long)Math.Round((value / maxValue) * (plotHeight - OfficeToolSupport.InchesToEmu(0.2)));
                var barX = groupStart + innerGap + seriesIndex * (barWidth + innerGap);
                var barY = plotY + plotHeight - barHeight;
                shapeTree.Append(MakeFilledShape(nextId++, $"Chart Bar {seriesIndex + 1}-{categoryIndex + 1}", barX, barY, barWidth, Math.Max(18000L, barHeight), palette[seriesIndex % palette.Count]));
            }

            shapeTree.Append(MakePlainTextBox(
                nextId++,
                $"Category Label {categoryIndex + 1}",
                categories[categoryIndex],
                groupStart,
                plotY + plotHeight + OfficeToolSupport.InchesToEmu(0.08),
                groupWidth,
                OfficeToolSupport.InchesToEmu(0.25),
                false,
                1100));
        }
    }

    private static void AppendBarChartShapes(ShapeTree shapeTree, ref uint nextId, long plotX, long plotY, long plotWidth, long plotHeight, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, double maxValue, IReadOnlyList<string> palette)
    {
        long groupHeight = plotHeight / Math.Max(1, categories.Count);
        long innerGap = Math.Max(30000L, groupHeight / 8);
        long barHeight = Math.Max(18000L, (groupHeight - innerGap * (series.Count + 1)) / Math.Max(1, series.Count));

        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            long groupStart = plotY + categoryIndex * groupHeight;
            shapeTree.Append(MakePlainTextBox(
                nextId++,
                $"Category Label {categoryIndex + 1}",
                categories[categoryIndex],
                plotX - OfficeToolSupport.InchesToEmu(0.25),
                groupStart,
                OfficeToolSupport.InchesToEmu(0.22),
                groupHeight,
                false,
                1100));

            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var value = series[seriesIndex].Values[categoryIndex];
                var barWidth = (long)Math.Round((value / maxValue) * (plotWidth - OfficeToolSupport.InchesToEmu(0.25)));
                var barY = groupStart + innerGap + seriesIndex * (barHeight + innerGap);
                shapeTree.Append(MakeFilledShape(nextId++, $"Chart Bar {seriesIndex + 1}-{categoryIndex + 1}", plotX, barY, Math.Max(18000L, barWidth), barHeight, palette[seriesIndex % palette.Count]));
            }
        }
    }

    private static void AppendStackedColumnChartShapes(ShapeTree shapeTree, ref uint nextId, long plotX, long plotY, long plotWidth, long plotHeight, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, double maxValue, IReadOnlyList<string> palette)
    {
        long groupWidth = plotWidth / Math.Max(1, categories.Count);
        long innerGap = Math.Max(40000L, groupWidth / 6);
        long barWidth = Math.Max(70000L, groupWidth - (innerGap * 2));

        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            long groupStart = plotX + categoryIndex * groupWidth;
            long stackedHeight = 0L;
            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var value = series[seriesIndex].Values[categoryIndex];
                if (value <= 0) continue;

                var segmentHeight = (long)Math.Round((value / maxValue) * (plotHeight - OfficeToolSupport.InchesToEmu(0.2)));
                var barX = groupStart + innerGap;
                var barY = plotY + plotHeight - stackedHeight - segmentHeight;
                shapeTree.Append(MakeFilledShape(nextId++, $"Chart Segment {seriesIndex + 1}-{categoryIndex + 1}", barX, barY, barWidth, Math.Max(18000L, segmentHeight), palette[seriesIndex % palette.Count]));
                stackedHeight += segmentHeight;
            }

            shapeTree.Append(MakePlainTextBox(
                nextId++,
                $"Category Label {categoryIndex + 1}",
                categories[categoryIndex],
                groupStart,
                plotY + plotHeight + OfficeToolSupport.InchesToEmu(0.08),
                groupWidth,
                OfficeToolSupport.InchesToEmu(0.25),
                false,
                1100));
        }
    }

    private static void AppendStackedBarChartShapes(ShapeTree shapeTree, ref uint nextId, long plotX, long plotY, long plotWidth, long plotHeight, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, double maxValue, IReadOnlyList<string> palette)
    {
        long groupHeight = plotHeight / Math.Max(1, categories.Count);
        long innerGap = Math.Max(30000L, groupHeight / 6);
        long barHeight = Math.Max(20000L, groupHeight - (innerGap * 2));

        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            long groupStart = plotY + categoryIndex * groupHeight;
            shapeTree.Append(MakePlainTextBox(
                nextId++,
                $"Category Label {categoryIndex + 1}",
                categories[categoryIndex],
                plotX - OfficeToolSupport.InchesToEmu(0.25),
                groupStart,
                OfficeToolSupport.InchesToEmu(0.22),
                groupHeight,
                false,
                1100));

            long stackedWidth = 0L;
            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var value = series[seriesIndex].Values[categoryIndex];
                if (value <= 0) continue;

                var segmentWidth = (long)Math.Round((value / maxValue) * (plotWidth - OfficeToolSupport.InchesToEmu(0.25)));
                var barY = groupStart + innerGap;
                var barX = plotX + stackedWidth;
                shapeTree.Append(MakeFilledShape(nextId++, $"Chart Segment {seriesIndex + 1}-{categoryIndex + 1}", barX, barY, Math.Max(18000L, segmentWidth), barHeight, palette[seriesIndex % palette.Count]));
                stackedWidth += segmentWidth;
            }
        }
    }

    private static void AppendLegend(ShapeTree shapeTree, ref uint nextId, long x, long y, long width, long height, IReadOnlyList<SlideChartSeries> series, IReadOnlyList<string> palette)
    {
        AppendLegendItems(shapeTree, ref nextId, x, y, width, height, series.Select(s => s.Name).ToList(), palette);
    }

    private static void AppendLegendItems(ShapeTree shapeTree, ref uint nextId, long x, long y, long width, long height, IReadOnlyList<string> labels, IReadOnlyList<string> palette)
    {
        if (labels.Count == 0) return;

        long itemWidth = width / Math.Max(1, labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            var itemX = x + i * itemWidth;
            shapeTree.Append(MakeFilledShape(nextId++, $"Legend Swatch {i + 1}", itemX, y + 20000L, OfficeToolSupport.InchesToEmu(0.18), OfficeToolSupport.InchesToEmu(0.18), palette[i % palette.Count]));
            shapeTree.Append(MakePlainTextBox(nextId++, $"Legend Label {i + 1}", labels[i], itemX + OfficeToolSupport.InchesToEmu(0.24), y, itemWidth - OfficeToolSupport.InchesToEmu(0.24), height, false, 1100));
        }
    }

    private static void AppendBottomCategoryLabels(ShapeTree shapeTree, ref uint nextId, long x, long y, long width, IReadOnlyList<string> categories)
    {
        long groupWidth = width / Math.Max(1, categories.Count);
        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            shapeTree.Append(MakePlainTextBox(
                nextId++,
                $"Category Label {categoryIndex + 1}",
                categories[categoryIndex],
                x + categoryIndex * groupWidth,
                y,
                groupWidth,
                OfficeToolSupport.InchesToEmu(0.25),
                false,
                1100));
        }
    }

    private static void InsertRenderedChartImage(SlidePart slidePart, ref uint nextId, byte[] imageBytes, string name, long x, long y, long cx, long cy)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using var stream = new MemoryStream(imageBytes, writable: false);
        imagePart.FeedData(stream);

        var picture = CreatePicture(nextId++, name, name, slidePart.GetIdOfPart(imagePart), x, y, cx, cy);
        slidePart.Slide.CommonSlideData!.ShapeTree!.Append(picture);
    }

    private static byte[] RenderLineChartImage(int width, int height, IReadOnlyList<string> categories, IReadOnlyList<SlideChartSeries> series, IReadOnlyList<string> palette)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 0));
        var grid = ParseColor("D1D5DB");
        var axis = ParseColor("2D3142");
        var plotLeft = 34;
        var plotTop = 18;
        var plotRight = width - 16;
        var plotBottom = height - 26;
        var plotWidth = Math.Max(1, plotRight - plotLeft);
        var plotHeight = Math.Max(1, plotBottom - plotTop);
        var maxValue = Math.Max(1d, series.SelectMany(s => s.Values).DefaultIfEmpty(0d).Max());

        for (var gridIndex = 0; gridIndex <= 4; gridIndex++)
        {
            var y = plotTop + (plotHeight * gridIndex / 4);
            DrawLine(image, plotLeft, y, plotRight, y, grid, 1);
        }

        DrawLine(image, plotLeft, plotBottom, plotRight, plotBottom, axis, 2);
        DrawLine(image, plotLeft, plotTop, plotLeft, plotBottom, axis, 2);

        for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
        {
            var color = ParseColor(palette[seriesIndex % palette.Count]);
            Point[] points = new Point[categories.Count];
            for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
            {
                var x = categories.Count == 1
                    ? plotLeft + plotWidth / 2
                    : plotLeft + (int)Math.Round(categoryIndex * (plotWidth / (double)(categories.Count - 1)));
                var y = plotBottom - (int)Math.Round((series[seriesIndex].Values[categoryIndex] / maxValue) * (plotHeight - 6));
                points[categoryIndex] = new Point(x, y);
            }

            for (var pointIndex = 0; pointIndex < points.Length - 1; pointIndex++)
                DrawLine(image, points[pointIndex].X, points[pointIndex].Y, points[pointIndex + 1].X, points[pointIndex + 1].Y, color, 3);
            foreach (var point in points)
                FillCircle(image, point.X, point.Y, 6, color);
        }

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] RenderPieChartImage(int width, int height, IReadOnlyList<string> categories, IReadOnlyList<double> values, IReadOnlyList<string> palette)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 0));
        var total = Math.Max(0.0001d, values.Sum());
        var centerX = width / 2;
        var centerY = height / 2;
        var radius = Math.Max(10, Math.Min(width, height) / 2 - 18);
        var sliceBoundaries = new List<(double Start, double End, Rgba32 Color)>();
        var currentAngle = -Math.PI / 2;
        for (var i = 0; i < categories.Count; i++)
        {
            var sweep = (values[i] / total) * (Math.PI * 2);
            sliceBoundaries.Add((currentAngle, currentAngle + sweep, ParseColor(palette[i % palette.Count])));
            currentAngle += sweep;
        }

        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                var distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radius * radius) continue;

                var angle = Math.Atan2(dy, dx);
                if (angle < -Math.PI / 2) angle += Math.PI * 2;

                foreach (var slice in sliceBoundaries)
                {
                    if (angle >= slice.Start && angle < slice.End)
                    {
                        image[x, y] = slice.Color;
                        break;
                    }
                }
            }
        }

        foreach (var slice in sliceBoundaries)
        {
            var x = centerX + (int)Math.Round(Math.Cos(slice.Start) * radius);
            var y = centerY + (int)Math.Round(Math.Sin(slice.Start) * radius);
            DrawLine(image, centerX, centerY, x, y, new Rgba32(255, 255, 255, 255), 2);
        }

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static void DrawLine(Image<Rgba32> image, int x0, int y0, int x1, int y1, Rgba32 color, int thickness)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;

        while (true)
        {
            FillCircle(image, x0, y0, Math.Max(1, thickness / 2), color);
            if (x0 == x1 && y0 == y1) break;

            var e2 = err * 2;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void FillCircle(Image<Rgba32> image, int centerX, int centerY, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if (x * x + y * y > radius * radius) continue;
                var px = centerX + x;
                var py = centerY + y;
                if (px < 0 || py < 0 || px >= image.Width || py >= image.Height) continue;
                image[px, py] = color;
            }
        }
    }

    private static Rgba32 ParseColor(string hexColor)
    {
        var raw = hexColor.Trim().TrimStart('#');
        return new Rgba32(
            Convert.ToByte(raw.Substring(0, 2), 16),
            Convert.ToByte(raw.Substring(2, 2), 16),
            Convert.ToByte(raw.Substring(4, 2), 16),
            255);
    }

    private static Shape MakeFilledShape(uint id, string name, long x, long y, long cx, long cy, string fillHex)
        => new(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.SolidFill(new A.RgbColorModelHex { Val = fillHex }),
                new A.Outline(new A.NoFill())),
            new TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));

    private static Shape MakePlainTextBox(uint id, string name, string text, long x, long y, long cx, long cy, bool bold, int fontSize)
    {
        var txBody = new TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var line in text.Split('\n'))
        {
            var run = new A.Run(new A.Text(line));
            run.RunProperties = new A.RunProperties { Language = "en-US", FontSize = fontSize };
            if (bold) run.RunProperties.Bold = true;
            txBody.Append(new A.Paragraph(run));
        }

        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill(),
                new A.Outline(new A.NoFill())),
            txBody);
    }

    // ── Slide extraction ──────────────────────────────────────────────────────

    private record SlideInfo(string? title, string body, int index);

    private static List<SlideInfo> ExtractSlides(PresentationDocument pres)
    {
        var result = new List<SlideInfo>();
        var slideParts = GetSlideParts(pres);
        for (int i = 0; i < slideParts.Count; i++)
        {
            var titleShape = ResolveTitleShape(slideParts[i]);
            var bodyShapes = ResolveBodyShapes(slideParts[i]);
            var title = titleShape?.TextBody?.InnerText;
            var bodyText = string.Join("\n", bodyShapes.Select(s => s.TextBody?.InnerText ?? ""));
            result.Add(new SlideInfo(title, bodyText, i + 1));
        }
        return result;
    }

    private static List<Shape> GetContentShapes(SlidePart slidePart)
    {
        var allShapes = slidePart.Slide.CommonSlideData?.ShapeTree?.Elements<Shape>()
            .Where(shape => shape.TextBody is not null && !IsManagedShape(shape))
            .ToList() ?? new List<Shape>();

        var titleShape = ResolvePlaceholderShape(slidePart, SlideContentRole.Title);
        var bodyShapes = ResolvePlaceholderShapes(slidePart, SlideContentRole.Body);
        var ordered = new List<Shape>();
        if (titleShape is not null) ordered.Add(titleShape);
        ordered.AddRange(bodyShapes.Where(shape => titleShape != shape));
        ordered.AddRange(allShapes.Where(shape => !ordered.Contains(shape)));
        return ordered;
    }

    private static Shape? ResolveTitleShape(SlidePart slidePart)
    {
        var placeholder = ResolvePlaceholderShape(slidePart, SlideContentRole.Title);
        if (placeholder is not null) return placeholder;
        return GetContentShapes(slidePart).FirstOrDefault();
    }

    private static List<Shape> ResolveBodyShapes(SlidePart slidePart)
    {
        var placeholders = ResolvePlaceholderShapes(slidePart, SlideContentRole.Body);
        if (placeholders.Count > 0) return placeholders;

        var titleShape = ResolveTitleShape(slidePart);
        return GetContentShapes(slidePart)
            .Where(shape => shape != titleShape)
            .ToList();
    }

    private static Shape? ResolvePlaceholderShape(SlidePart slidePart, SlideContentRole desiredRole)
        => ResolvePlaceholderShapes(slidePart, desiredRole).FirstOrDefault();

    private static List<Shape> ResolvePlaceholderShapes(SlidePart slidePart, SlideContentRole desiredRole)
        => slidePart.Slide.CommonSlideData?.ShapeTree?.Elements<Shape>()
            .Where(shape => shape.TextBody is not null && !IsManagedShape(shape) && DetermineSlideContentRole(slidePart, shape) == desiredRole)
            .ToList()
           ?? new List<Shape>();

    private static SlideContentRole DetermineSlideContentRole(SlidePart slidePart, Shape shape)
    {
        var placeholderToken = ResolvePlaceholderTypeToken(slidePart, shape);
        if (!string.IsNullOrWhiteSpace(placeholderToken))
        {
            switch (placeholderToken.Trim().ToLowerInvariant())
            {
                case "title":
                case "centeredtitle":
                    return SlideContentRole.Title;
                case "body":
                case "object":
                case "obj":
                case "subtitle":
                case "text":
                    return SlideContentRole.Body;
            }
        }

        var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.Contains("title", StringComparison.OrdinalIgnoreCase)) return SlideContentRole.Title;
            if (name.Contains("content", StringComparison.OrdinalIgnoreCase)
                || name.Contains("body", StringComparison.OrdinalIgnoreCase)
                || name.Contains("subtitle", StringComparison.OrdinalIgnoreCase))
                return SlideContentRole.Body;
        }

        return SlideContentRole.None;
    }

    private static string? ResolvePlaceholderTypeToken(SlidePart slidePart, Shape shape)
    {
        var placeholder = GetPlaceholderShape(shape);
        var direct = placeholder?.Type?.Value.ToString();
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var layoutShape = slidePart.SlideLayoutPart?.SlideLayout?.CommonSlideData?.ShapeTree?.Elements<Shape>()
            .FirstOrDefault(candidate => PlaceholderMatches(GetPlaceholderShape(candidate), placeholder, candidate.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value));
        return GetPlaceholderShape(layoutShape)?.Type?.Value.ToString();
    }

    private static bool PlaceholderMatches(PlaceholderShape? layoutPlaceholder, PlaceholderShape? slidePlaceholder, string? layoutName, string? slideName)
    {
        if (layoutPlaceholder is null || slidePlaceholder is null) return false;
        if (layoutPlaceholder.Index?.Value is { } layoutIndex && slidePlaceholder.Index?.Value is { } slideIndex)
            return layoutIndex == slideIndex;
        return !string.IsNullOrWhiteSpace(layoutName)
               && !string.IsNullOrWhiteSpace(slideName)
               && string.Equals(layoutName, slideName, StringComparison.OrdinalIgnoreCase);
    }

    private static PlaceholderShape? GetPlaceholderShape(Shape? shape)
        => shape?.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();

    private static bool IsManagedShape(Shape shape)
    {
        var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
        if (string.IsNullOrWhiteSpace(name)) return false;

        return name.StartsWith("Brand ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Chart ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Legend ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Category Label", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SlidePart> GetSlideParts(PresentationDocument pres)
    {
        var slideIdList = pres.PresentationPart?.Presentation.SlideIdList;
        if (slideIdList is null) return new List<SlidePart>();
        return slideIdList.Elements<SlideId>()
            .Select(sid => (SlidePart)pres.PresentationPart!.GetPartById(sid.RelationshipId!.Value!))
            .ToList();
    }

    private static uint GetNextShapeId(SlidePart slidePart)
        => slidePart.Slide.Descendants<NonVisualDrawingProperties>()
            .Select(p => p.Id?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;

    private static SlidePart CloneSlidePart(PresentationPart presentationPart, SlidePart sourceSlidePart)
    {
        var newSlidePart = presentationPart.AddNewPart<SlidePart>();
        using (var sourceStream = sourceSlidePart.GetStream(FileMode.Open, FileAccess.Read))
            newSlidePart.FeedData(sourceStream);

        foreach (var part in sourceSlidePart.Parts)
        {
            if (part.OpenXmlPart is NotesSlidePart) continue;
            newSlidePart.AddPart(part.OpenXmlPart, part.RelationshipId);
        }

        newSlidePart.Slide?.Save();
        return newSlidePart;
    }

    private static List<SlidePart> ResolveSlideSelection(JsonElement doc, List<SlidePart> slideParts)
    {
        if (!doc.TryGetProperty("slideIndices", out var indicesEl) || indicesEl.ValueKind != JsonValueKind.Array)
            return slideParts;

        var selected = new List<SlidePart>();
        var seen = new HashSet<int>();
        foreach (var indexEl in indicesEl.EnumerateArray())
        {
            if (!indexEl.TryGetInt32(out var slideIndex))
                throw new ArgumentException("slideIndices must contain integers.");
            if (slideIndex < 1 || slideIndex > slideParts.Count)
                throw new ArgumentException($"slideIndex {slideIndex} is out of range (1–{slideParts.Count}).");
            if (seen.Add(slideIndex)) selected.Add(slideParts[slideIndex - 1]);
        }
        return selected;
    }

    private static List<(string Find, string Replace)> ParseReplacements(JsonElement doc, bool requireArray, out string? error)
    {
        error = null;
        if (!doc.TryGetProperty("replacements", out var replacementsEl))
        {
            if (requireArray) error = "'replacements' must be an array.";
            return new List<(string Find, string Replace)>();
        }

        if (replacementsEl.ValueKind != JsonValueKind.Array)
        {
            error = "'replacements' must be an array.";
            return new List<(string Find, string Replace)>();
        }

        var replacements = new List<(string Find, string Replace)>();
        foreach (var repl in replacementsEl.EnumerateArray())
        {
            var find = repl.TryGetProperty("find", out var fEl) ? fEl.GetString() ?? string.Empty : string.Empty;
            var replace = repl.TryGetProperty("replace", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(find))
            {
                error = "Each replacement requires a non-empty 'find' value.";
                return new List<(string Find, string Replace)>();
            }
            replacements.Add((find, replace));
        }

        if (requireArray && replacements.Count == 0)
            error = "At least one replacement is required.";

        return replacements;
    }

    private static List<(string Find, string Replace, int Count)> ApplyTextReplacements(IEnumerable<SlidePart> slideParts, IReadOnlyList<(string Find, string Replace)> replacements, StringComparison comparison)
    {
        var counts = replacements.Select(r => (r.Find, r.Replace, Count: 0)).ToList();
        if (replacements.Count == 0) return counts;

        foreach (var slidePart in slideParts)
        {
            foreach (var text in slidePart.Slide.Descendants<A.Text>())
            {
                var current = text.Text;
                if (string.IsNullOrEmpty(current)) continue;

                for (var i = 0; i < counts.Count; i++)
                {
                    var replacement = counts[i];
                    if (!current.Contains(replacement.Find, comparison)) continue;
                    current = current.Replace(replacement.Find, replacement.Replace, comparison);
                    counts[i] = (replacement.Find, replacement.Replace, replacement.Count + 1);
                }

                text.Text = current;
            }

            slidePart.Slide.Save();
        }

        return counts;
    }

    private static void ApplyBrandingToSlide(SlidePart slidePart, long slideWidth, long slideHeight, string? backgroundColor, string? titleColor, string? bodyColor, string? accentColor, bool managesFooter, string? footerText, string? footerColor)
    {
        var shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree ?? throw new InvalidOperationException("Slide has no shape tree.");
        var nextId = GetNextShapeId(slidePart);

        if (backgroundColor is not null)
        {
            RemoveShapeByName(shapeTree, "Brand Background");
            InsertBackgroundShape(shapeTree, MakeFilledShape(nextId++, "Brand Background", 0L, 0L, slideWidth, slideHeight, backgroundColor));
        }

        if (titleColor is not null || bodyColor is not null)
        {
            var titleShape = ResolveTitleShape(slidePart);
            if (titleShape is not null && titleColor is not null)
                SetShapeTextColor(titleShape, titleColor);
            if (bodyColor is not null)
            {
                foreach (var bodyShape in ResolveBodyShapes(slidePart))
                    SetShapeTextColor(bodyShape, bodyColor);
            }
        }

        if (managesFooter || accentColor is not null)
        {
            RemoveShapeByName(shapeTree, "Brand Footer Band");
            RemoveShapeByName(shapeTree, "Brand Footer Text");

            if (accentColor is not null)
            {
                shapeTree.Append(MakeFilledShape(
                    nextId++,
                    "Brand Footer Band",
                    0L,
                    slideHeight - OfficeToolSupport.InchesToEmu(0.32),
                    slideWidth,
                    OfficeToolSupport.InchesToEmu(0.32),
                    accentColor));
            }

            if (!string.IsNullOrWhiteSpace(footerText))
            {
                var footerShape = MakePlainTextBox(
                    nextId++,
                    "Brand Footer Text",
                    footerText!,
                    OfficeToolSupport.InchesToEmu(0.5),
                    slideHeight - OfficeToolSupport.InchesToEmu(0.28),
                    slideWidth - OfficeToolSupport.InchesToEmu(1.0),
                    OfficeToolSupport.InchesToEmu(0.18),
                    false,
                    1000);
                SetShapeTextColor(footerShape, footerColor ?? (accentColor is not null ? "FFFFFF" : (bodyColor ?? titleColor ?? "1F2937")));
                shapeTree.Append(footerShape);
            }
        }
    }

    private static void InsertBackgroundShape(ShapeTree shapeTree, Shape backgroundShape)
    {
        var groupShapeProps = shapeTree.Elements<GroupShapeProperties>().FirstOrDefault();
        if (groupShapeProps is not null)
        {
            shapeTree.InsertAfter(backgroundShape, groupShapeProps);
            return;
        }

        shapeTree.PrependChild(backgroundShape);
    }

    private static void RemoveShapeByName(ShapeTree shapeTree, string name)
    {
        foreach (var shape in shapeTree.Elements<Shape>()
                     .Where(s => string.Equals(s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            shape.Remove();
        }
    }

    private static void SetShapeTextColor(Shape shape, string hexColor)
    {
        if (shape.TextBody is null) return;

        foreach (var paragraph in shape.TextBody.Elements<A.Paragraph>())
        {
            foreach (var run in paragraph.Elements<A.Run>())
            {
                run.RunProperties ??= new A.RunProperties { Language = "en-US" };
                run.RunProperties.RemoveAllChildren<A.SolidFill>();
                run.RunProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = hexColor }));
            }

            var endRunProperties = paragraph.GetFirstChild<A.EndParagraphRunProperties>()
                                   ?? paragraph.AppendChild(new A.EndParagraphRunProperties { Language = "en-US" });
            endRunProperties.RemoveAllChildren<A.SolidFill>();
            endRunProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = hexColor }));
        }
    }

    private static bool TryGetOptionalHexColor(JsonElement doc, string propertyName, out string? normalizedColor, out string? error)
    {
        normalizedColor = null;
        error = null;
        if (!doc.TryGetProperty(propertyName, out var colorEl)) return false;

        var raw = colorEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalizedColor = null;
            return true;
        }

        var candidate = raw.Trim().TrimStart('#');
        if (candidate.Length != 6 || !candidate.All(Uri.IsHexDigit))
        {
            error = $"{propertyName} must be a 6-digit hex color like '#0E5A7A'.";
            return true;
        }

        normalizedColor = candidate.ToUpperInvariant();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ResolveFile(JsonElement doc, ToolContext ctx)
    {
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return null;
        if (!filename.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) filename += ".pptx";
        return SafePath(ctx.WorkDirectory, filename);
    }

    private static bool TryParse(string? json, out JsonElement el)
    {
        el = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { el = JsonDocument.Parse(json).RootElement; return el.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    private static string? SafePath(string workDir, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        try { return OfficeToolSupport.ResolveWorkFile(workDir, filename, ".pptx"); }
        catch { return null; }
    }

    private static Task<ToolResult> Err(string msg) =>
        Task.FromResult(ToolResult.Error(msg));

    private sealed record SlideChartSeries(string Name, IReadOnlyList<double> Values);

    private enum SlideContentRole
    {
        None,
        Title,
        Body,
    }
}
