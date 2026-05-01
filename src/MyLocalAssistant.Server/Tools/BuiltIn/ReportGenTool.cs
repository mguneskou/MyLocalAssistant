using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OxmlDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using DocumentFormat.OpenXml.Wordprocessing;
using MyLocalAssistant.Shared.Contracts;
using PdfDocument = QuestPDF.Fluent.Document;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Generates PDF, Word (.docx), and Excel (.xlsx) reports from structured data.
/// Reports are saved to the conversation WorkDirectory.
/// Config JSON: {"defaultAuthor":"MyLocalAssistant","defaultCompany":""}
/// </summary>
internal sealed class ReportGenTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "report.gen";
    public string  Name        => "Report Generator";
    public string  Description => "Generates PDF, Word (.docx), and Excel (.xlsx) reports from structured data. Reports are saved to the conversation work directory.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "report.pdf",
            Description: "Generate a PDF report with a title and sections. Sections are rendered as headings and paragraphs. Returns the filename and path.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"title":{"type":"string","description":"Report title"},"sections":{"type":"array","description":"Array of {heading, content} objects","items":{"type":"object","properties":{"heading":{"type":"string"},"content":{"type":"string"}}}},"filename":{"type":"string","description":"Optional output filename"}},"required":["title","sections"]}"""),
        new ToolFunctionDto(
            Name: "report.word",
            Description: "Generate a Word (.docx) document with a title and sections. Returns the filename and path.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"title":{"type":"string","description":"Document title"},"sections":{"type":"array","description":"Array of {heading, content} objects","items":{"type":"object","properties":{"heading":{"type":"string"},"content":{"type":"string"}}}},"filename":{"type":"string","description":"Optional output filename"}},"required":["title","sections"]}"""),
        new ToolFunctionDto(
            Name: "report.excel",
            Description: "Generate an Excel (.xlsx) workbook with one or more sheets of tabular data. Returns the filename and path.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"title":{"type":"string","description":"Workbook title"},"sheets":{"type":"array","description":"Array of {name, headers, rows} sheet objects","items":{"type":"object","properties":{"name":{"type":"string"},"headers":{"type":"array","items":{"type":"string"}},"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}}}}},"filename":{"type":"string","description":"Optional output filename"}},"required":["title","sheets"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private string _author  = "MyLocalAssistant";
    private string _company = "";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static ReportGenTool()
    {
        // QuestPDF Community License for non-commercial / open-source use.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DefaultAuthor))  _author  = cfg.DefaultAuthor;
        if (!string.IsNullOrWhiteSpace(cfg?.DefaultCompany)) _company = cfg.DefaultCompany;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args    = doc.RootElement.Clone();
        var workDir = ctx.WorkDirectory;
        var ct      = ctx.CancellationToken;

        if (string.IsNullOrWhiteSpace(workDir))
            return ToolResult.Error("WorkDirectory not set — cannot save report");
        Directory.CreateDirectory(workDir);

        return call.ToolName switch
        {
            "report.pdf"   => await Task.Run(() => GeneratePdf(args, workDir),   ct),
            "report.word"  => await Task.Run(() => GenerateWord(args, workDir),  ct),
            "report.excel" => await Task.Run(() => GenerateExcel(args, workDir), ct),
            _              => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
        };
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    private ToolResult GeneratePdf(JsonElement args, string workDir)
    {
        var title    = args.TryGetProperty("title",    out var t) ? t.GetString() ?? "Report" : "Report";
        var filename = GetFilename(args, title, ".pdf");
        var sections = ParseSections(args);
        var path     = Path.Combine(workDir, filename);

        try
        {
            var author  = _author;
            var company = _company;
            PdfDocument.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Arial"));

                    page.Header().Text(title)
                        .SemiBold().FontSize(18).FontColor(Colors.Grey.Darken3);

                    page.Content().Column(col =>
                    {
                        col.Spacing(10);
                        if (!string.IsNullOrWhiteSpace(company))
                            col.Item().Text(company).FontSize(9).FontColor(Colors.Grey.Medium);

                        foreach (var (heading, content) in sections)
                        {
                            if (!string.IsNullOrWhiteSpace(heading))
                                col.Item().Text(heading).Bold().FontSize(13);
                            col.Item().Text(content).FontSize(11).LineHeight(1.4f);
                        }
                    });

                    page.Footer().AlignRight()
                        .Text(x =>
                        {
                            x.Span($"Generated {DateTime.Now:yyyy-MM-dd}  |  ").FontSize(8).FontColor(Colors.Grey.Medium);
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                });
            }).GeneratePdf(path);

            return OkWithStructured(filename, path, "pdf");
        }
        catch (Exception ex) { return ToolResult.Error($"PDF generation failed: {ex.Message}"); }
    }

    // ── Word ──────────────────────────────────────────────────────────────────

    private ToolResult GenerateWord(JsonElement args, string workDir)
    {
        var title    = args.TryGetProperty("title", out var t) ? t.GetString() ?? "Report" : "Report";
        var filename = GetFilename(args, title, ".docx");
        var sections = ParseSections(args);
        var path     = Path.Combine(workDir, filename);

        try
        {
            using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var mainPart  = doc.AddMainDocumentPart();
            mainPart.Document = new OxmlDocument();
            var body = mainPart.Document.AppendChild(new Body());

            body.AppendChild(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text(title))));

            foreach (var (heading, content) in sections)
            {
                if (!string.IsNullOrWhiteSpace(heading))
                    body.AppendChild(new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                        new Run(new Text(heading))));

                foreach (var line in content.Split('\n'))
                    body.AppendChild(new Paragraph(new Run(new Text(line))));
            }

            var coreProps = doc.AddCoreFilePropertiesPart();
            using var sw  = new System.IO.StreamWriter(coreProps.GetStream(System.IO.FileMode.Create));
            sw.Write($@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<cp:coreProperties xmlns:cp=""http://schemas.openxmlformats.org/package/2006/metadata/core-properties""
  xmlns:dc=""http://purl.org/dc/elements/1.1/"">
  <dc:title>{System.Security.SecurityElement.Escape(title)}</dc:title>
  <dc:creator>{System.Security.SecurityElement.Escape(_author)}</dc:creator>
  <cp:lastModifiedBy>{System.Security.SecurityElement.Escape(_author)}</cp:lastModifiedBy>
</cp:coreProperties>");

            mainPart.Document.Save();
            return OkWithStructured(filename, path, "docx");
        }
        catch (Exception ex) { return ToolResult.Error($"Word generation failed: {ex.Message}"); }
    }

    // ── Excel ─────────────────────────────────────────────────────────────────

    private ToolResult GenerateExcel(JsonElement args, string workDir)
    {
        var title    = args.TryGetProperty("title", out var t) ? t.GetString() ?? "Report" : "Report";
        var filename = GetFilename(args, title, ".xlsx");
        var path     = Path.Combine(workDir, filename);

        try
        {
            using var wb = new XLWorkbook();
            wb.Properties.Author = _author;
            wb.Properties.Title  = title;

            if (!args.TryGetProperty("sheets", out var sheetsEl) ||
                sheetsEl.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("sheets array is required for report.excel");

            foreach (var sheetEl in sheetsEl.EnumerateArray())
            {
                var sheetName = sheetEl.TryGetProperty("name", out var sn) ? sn.GetString() ?? "Sheet1" : "Sheet1";
                var ws  = wb.Worksheets.Add(sheetName);
                var row = 1;

                if (sheetEl.TryGetProperty("headers", out var headersEl) &&
                    headersEl.ValueKind == JsonValueKind.Array)
                {
                    var col = 1;
                    foreach (var h in headersEl.EnumerateArray())
                    {
                        var cell = ws.Cell(row, col++);
                        cell.Value = h.GetString() ?? "";
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                    }
                    ws.Row(row).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                    row++;
                }

                if (sheetEl.TryGetProperty("rows", out var rowsEl) &&
                    rowsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dataRow in rowsEl.EnumerateArray())
                    {
                        var col = 1;
                        if (dataRow.ValueKind == JsonValueKind.Array)
                            foreach (var cell in dataRow.EnumerateArray())
                                ws.Cell(row, col++).Value = cell.GetString() ?? "";
                        row++;
                    }
                }

                ws.Columns().AdjustToContents();
            }

            wb.SaveAs(path);
            return OkWithStructured(filename, path, "xlsx");
        }
        catch (Exception ex) { return ToolResult.Error($"Excel generation failed: {ex.Message}"); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<(string Heading, string Content)> ParseSections(JsonElement args)
    {
        var result = new List<(string, string)>();
        if (!args.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var sec in sections.EnumerateArray())
        {
            var h = sec.TryGetProperty("heading", out var hv) ? hv.GetString() ?? "" : "";
            var c = sec.TryGetProperty("content", out var cv) ? cv.GetString() ?? "" : "";
            result.Add((h, c));
        }
        return result;
    }

    private static string GetFilename(JsonElement args, string title, string ext)
    {
        if (args.TryGetProperty("filename", out var fn) && fn.GetString() is { Length: > 0 } name)
            return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;

        var safe = string.Concat(title.Split(Path.GetInvalidFileNameChars()))
                         .Replace(' ', '_')
                         .ToLowerInvariant();
        if (safe.Length > 40) safe = safe[..40];
        return $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }

    private static ToolResult OkWithStructured(string filename, string path, string format)
    {
        var structured = JsonSerializer.Serialize(
            new { type = "file", filename, path, format }, s_json);
        return ToolResult.Ok($"Report saved: {filename}", structured);
    }

    private sealed class Config
    {
        [JsonPropertyName("defaultAuthor")]  public string? DefaultAuthor  { get; set; }
        [JsonPropertyName("defaultCompany")] public string? DefaultCompany { get; set; }
    }
}
