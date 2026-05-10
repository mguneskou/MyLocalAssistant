using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using HtmlAgilityPack;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MyLocalAssistant.Server.Rag;

/// <summary>One page (or logical section) of extracted text.</summary>
public sealed record DocumentPage(int Page, string Text);

/// <summary>Extracts plain text from supported file types.</summary>
public static class DocumentParsers
{
    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".markdown" or ".pdf" or ".docx" or ".pptx" or ".html" or ".htm" or ".xlsx" or ".xls";
    }

    public static IReadOnlyList<DocumentPage> Parse(Stream content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".markdown" => ParsePlainText(content),
            ".pdf" => ParsePdf(content),
            ".docx" => ParseDocx(content),
            ".html" or ".htm" => ParseHtml(content),
            ".xlsx" or ".xls" => ParseExcel(content),
            ".pptx" => ParsePptx(content),
            _ => throw new NotSupportedException($"Unsupported file extension: {ext}"),
        };
    }

    private static IReadOnlyList<DocumentPage> ParsePlainText(Stream s)
    {
        using var sr = new StreamReader(s, leaveOpen: true);
        return new[] { new DocumentPage(1, sr.ReadToEnd()) };
    }

    private static IReadOnlyList<DocumentPage> ParsePdf(Stream s)
    {
        using var doc = PdfDocument.Open(s);
        var pages = new List<DocumentPage>();
        foreach (Page p in doc.GetPages())
        {
            var text = p.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add(new DocumentPage(p.Number, text));
        }
        return pages;
    }

    private static IReadOnlyList<DocumentPage> ParseDocx(Stream s)
    {
        using var doc = WordprocessingDocument.Open(s, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body is null) return Array.Empty<DocumentPage>();
        var sb = new StringBuilder();
        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var line = p.InnerText;
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return new[] { new DocumentPage(1, sb.ToString()) };
    }

    private static IReadOnlyList<DocumentPage> ParseHtml(Stream s)
    {
        var html = new HtmlDocument();
        html.Load(s);
        // Drop script/style nodes.
        foreach (var n in html.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>())
            n.Remove();
        var text = HtmlEntity.DeEntitize(html.DocumentNode.InnerText ?? "");
        return new[] { new DocumentPage(1, text) };
    }

    private static IReadOnlyList<DocumentPage> ParseExcel(Stream s)
    {
        using var wb = new XLWorkbook(s);
        var pages = new List<DocumentPage>();
        int pageNum = 1;
        foreach (var ws in wb.Worksheets)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Sheet: {ws.Name}");
            var range = ws.RangeUsed();
            if (range is null) { pageNum++; continue; }
            foreach (var row in range.Rows())
            {
                var cells = row.Cells().Select(c => c.GetFormattedString()).ToArray();
                sb.AppendLine(string.Join("\t", cells));
            }
            pages.Add(new DocumentPage(pageNum++, sb.ToString()));
        }
        return pages.Count > 0 ? pages : new[] { new DocumentPage(1, "") };
    }

    private static IReadOnlyList<DocumentPage> ParsePptx(Stream s)
    {
        using var pres = PresentationDocument.Open(s, false);
        var slideIdList = pres.PresentationPart?.Presentation.SlideIdList;
        if (slideIdList is null) return Array.Empty<DocumentPage>();
        var pages = new List<DocumentPage>();
        int slideNum = 1;
        foreach (var slideId in slideIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideId>())
        {
            var slidePart = (DocumentFormat.OpenXml.Packaging.SlidePart)
                pres.PresentationPart!.GetPartById(slideId.RelationshipId!.Value!);
            var sb = new StringBuilder();
            foreach (var para in slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>())
            {
                var line = para.InnerText;
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            }
            var text = sb.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add(new DocumentPage(slideNum, text));
            slideNum++;
        }
        return pages.Count > 0 ? pages : new[] { new DocumentPage(1, "") };
    }
}
