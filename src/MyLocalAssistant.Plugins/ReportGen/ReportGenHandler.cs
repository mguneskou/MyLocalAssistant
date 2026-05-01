using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OxmlDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using DocumentFormat.OpenXml.Wordprocessing;
using MyLocalAssistant.Plugin.Shared;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QDocument = QuestPDF.Fluent.Document;

namespace MyLocalAssistant.Plugins.ReportGen;

/// <summary>
/// Generates PDF, Word (.docx), and Excel (.xlsx) reports from structured data.
/// Reports are saved to the conversation WorkDirectory.
/// Config JSON: {"defaultAuthor":"MyLocalAssistant","defaultCompany":""}
/// </summary>
internal sealed class ReportGenHandler : IPluginTool
{
    private string _author  = "MyLocalAssistant";
    private string _company = "";

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (!string.IsNullOrWhiteSpace(cfg?.DefaultAuthor))  _author  = cfg.DefaultAuthor;
        if (!string.IsNullOrWhiteSpace(cfg?.DefaultCompany)) _company = cfg.DefaultCompany;
    }

    public async Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        var workDir = context.WorkDirectory;
        if (string.IsNullOrWhiteSpace(workDir))
            return PluginToolResult.Error("WorkDirectory not set — cannot save report");
        Directory.CreateDirectory(workDir);

        return toolName switch
        {
            "report.pdf"   => await Task.Run(() => GeneratePdf(arguments, workDir),  ct),
            "report.word"  => await Task.Run(() => GenerateWord(arguments, workDir), ct),
            "report.excel" => await Task.Run(() => GenerateExcel(arguments, workDir), ct),
            _              => PluginToolResult.Error($"Unknown tool '{toolName}'"),
        };
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    private PluginToolResult GeneratePdf(JsonElement args, string workDir)
    {
        var title    = args.TryGetProperty("title",    out var t) ? t.GetString() ?? "Report" : "Report";
        var filename = GetFilename(args, title, ".pdf");
        var sections = ParseSections(args);
        var path     = Path.Combine(workDir, filename);

        try
        {
            QDocument.Create(container =>
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
                        if (!string.IsNullOrWhiteSpace(_company))
                            col.Item().Text(_company).FontSize(9).FontColor(Colors.Grey.Medium);

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
        catch (Exception ex) { return PluginToolResult.Error($"PDF generation failed: {ex.Message}"); }
    }

    // ── Word ──────────────────────────────────────────────────────────────────

    private PluginToolResult GenerateWord(JsonElement args, string workDir)
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

            // Title paragraph.
            body.AppendChild(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text(title))));

            foreach (var (heading, content) in sections)
            {
                if (!string.IsNullOrWhiteSpace(heading))
                    body.AppendChild(new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                        new Run(new Text(heading))));

                // Split content on newlines → separate paragraphs.
                foreach (var line in content.Split('\n'))
                    body.AppendChild(new Paragraph(new Run(new Text(line))));
            }

            // Document properties (author/date).
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
        catch (Exception ex) { return PluginToolResult.Error($"Word generation failed: {ex.Message}"); }
    }

    // ── Excel ─────────────────────────────────────────────────────────────────

    private PluginToolResult GenerateExcel(JsonElement args, string workDir)
    {
        var title    = args.TryGetProperty("title", out var t) ? t.GetString() ?? "Report" : "Report";
        var filename = GetFilename(args, title, ".xlsx");
        var path     = Path.Combine(workDir, filename);

        try
        {
            using var wb = new XLWorkbook();
            wb.Properties.Author  = _author;
            wb.Properties.Title   = title;

            if (!args.TryGetProperty("sheets", out var sheetsEl) ||
                sheetsEl.ValueKind != JsonValueKind.Array)
                return PluginToolResult.Error("sheets array is required for report.excel");

            foreach (var sheetEl in sheetsEl.EnumerateArray())
            {
                var sheetName = sheetEl.TryGetProperty("name",    out var sn) ? sn.GetString() ?? "Sheet1" : "Sheet1";
                var ws        = wb.Worksheets.Add(sheetName);
                var row       = 1;

                // Headers.
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

                // Data rows.
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
        catch (Exception ex) { return PluginToolResult.Error($"Excel generation failed: {ex.Message}"); }
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

    private static PluginToolResult OkWithStructured(string filename, string path, string format)
    {
        var structured = JsonSerializer.Serialize(
            new { type = "file", filename, path, format }, s_json);
        return PluginToolResult.Ok($"Report saved: {filename}", structured);
    }

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Config
    {
        [JsonPropertyName("defaultAuthor")]  public string? DefaultAuthor  { get; set; }
        [JsonPropertyName("defaultCompany")] public string? DefaultCompany { get; set; }
    }
}
