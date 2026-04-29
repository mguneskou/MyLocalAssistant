using System.Text;
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
        return ext is ".txt" or ".md" or ".markdown" or ".pdf" or ".docx" or ".html" or ".htm";
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
}
