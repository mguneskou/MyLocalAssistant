using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MyLocalAssistant.Shared.Contracts;
using A = DocumentFormat.OpenXml.Drawing;

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
            Name: "powerpoint.add_table",
            Description: "Insert a table onto a slide. Provide rows as an array of string arrays.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"slideIndex":{"type":"integer","description":"1-based slide index."},"rows":{"type":"array","description":"Table data — array of rows, each an array of cell strings.","items":{"type":"array","items":{"type":"string"}}},"headerRow":{"type":"boolean","description":"Bold the first row."}},"required":["filename","slideIndex","rows"]}"""),
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
            "powerpoint.add_table"   => AddTableAsync(call, ctx),
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
            spTree.Append(MakeTextBox(2, "Title", title, 457200, 274638, 8229600, 1143000, bold: true, fontSize: 3600));
        if (body is not null)
            spTree.Append(MakeTextBox(3, "Content", body, 457200, 1600200, 8229600, 4525963, bold: false, fontSize: 2400));

        return new Slide(new CommonSlideData(spTree), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static Shape MakeTextBox(uint id, string name, string text, long x, long y, long cx, long cy, bool bold, int fontSize)
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

        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties(new PlaceholderShape())),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy })),
            txBody);
    }

    private static void SetSlideText(SlidePart slidePart, string? title, string? body)
    {
        var spTree = slidePart.Slide.CommonSlideData?.ShapeTree;
        if (spTree is null) return;
        var shapes = spTree.Elements<Shape>().ToList();

        // Heuristic: first shape = title, second = body
        if (title is not null && shapes.Count >= 1)
            ReplaceShapeText(shapes[0], title);
        if (body is not null && shapes.Count >= 2)
            ReplaceShapeText(shapes[1], body);
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

    // ── Slide extraction ──────────────────────────────────────────────────────

    private record SlideInfo(string? title, string body, int index);

    private static List<SlideInfo> ExtractSlides(PresentationDocument pres)
    {
        var result = new List<SlideInfo>();
        var slideParts = GetSlideParts(pres);
        for (int i = 0; i < slideParts.Count; i++)
        {
            var shapes = slideParts[i].Slide.CommonSlideData?.ShapeTree?.Elements<Shape>().ToList()
                         ?? new List<Shape>();
            var title = shapes.Count > 0 ? shapes[0].TextBody?.InnerText : null;
            var bodyText = string.Join("\n", shapes.Skip(1).Select(s => s.TextBody?.InnerText ?? ""));
            result.Add(new SlideInfo(title, bodyText, i + 1));
        }
        return result;
    }

    private static List<SlidePart> GetSlideParts(PresentationDocument pres)
    {
        var slideIdList = pres.PresentationPart?.Presentation.SlideIdList;
        if (slideIdList is null) return new List<SlidePart>();
        return slideIdList.Elements<SlideId>()
            .Select(sid => (SlidePart)pres.PresentationPart!.GetPartById(sid.RelationshipId!.Value!))
            .ToList();
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
        var name = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var full = Path.GetFullPath(Path.Combine(workDir, name));
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static Task<ToolResult> Err(string msg) =>
        Task.FromResult(ToolResult.Error(msg));
}
