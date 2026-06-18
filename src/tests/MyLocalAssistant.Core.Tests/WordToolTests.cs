using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class WordToolTests
{
    [Fact]
    public async Task ReplaceTokens_updates_body_header_and_footer()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var filename = "template.docx";
            CreateTemplate(Path.Combine(workDir, filename));

            var tool = new WordTool();
            var result = await tool.InvokeAsync(new ToolInvocation(
                "word.replace_tokens",
                "{\"filename\":\"template.docx\",\"replacements\":[{\"find\":\"{{Name}}\",\"replace\":\"Alice\"},{\"find\":\"{{Title}}\",\"replace\":\"Quarterly Review\"}]}"),
                MakeContext(workDir));

            Assert.False(result.IsError);

            using var doc = WordprocessingDocument.Open(Path.Combine(workDir, filename), false);
            Assert.Contains("Alice", doc.MainDocumentPart!.Document.Body!.InnerText);
            Assert.Contains("Quarterly Review", string.Join("\n", doc.MainDocumentPart.HeaderParts.Select(p => p.Header?.InnerText ?? string.Empty)));
            Assert.Contains("Alice", string.Join("\n", doc.MainDocumentPart.FooterParts.Select(p => p.Footer?.InnerText ?? string.Empty)));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Write_returns_structured_success_envelope()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var tool = new WordTool();
            var result = await tool.InvokeAsync(new ToolInvocation(
                "word.write",
                "{\"filename\":\"report.docx\",\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Hello world\"}]}"),
                MakeContext(workDir));

            Assert.False(result.IsError);
            Assert.NotNull(result.StructuredJson);
            using var envelope = JsonDocument.Parse(result.StructuredJson!);
            Assert.Equal("success", envelope.RootElement.GetProperty("status").GetString());
            Assert.Equal("Word document 'report.docx' saved.", envelope.RootElement.GetProperty("summary").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Professional_commands_update_header_layout_and_images_in_subfolders()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "reports"));
            Directory.CreateDirectory(Path.Combine(workDir, "assets"));

            var filename = "reports/template.docx";
            var imagePath = Path.Combine(workDir, "assets", "logo.png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9pJ6f0sAAAAASUVORK5CYII="));
            CreateTemplate(Path.Combine(workDir, "reports", "template.docx"));

            var tool = new WordTool();

            var headerFooterResult = await tool.InvokeAsync(new ToolInvocation(
                "word.set_header_footer",
                "{\"filename\":\"reports/template.docx\",\"headerText\":\"<<Logo>>\\nQuarterly Review\",\"footerText\":\"Confidential\",\"alignment\":\"center\"}"),
                MakeContext(workDir));
            Assert.False(headerFooterResult.IsError);

            var layoutResult = await tool.InvokeAsync(new ToolInvocation(
                "word.set_section_layout",
                "{\"filename\":\"reports/template.docx\",\"orientation\":\"landscape\",\"paperSize\":\"A4\",\"columns\":2,\"margins\":{\"top\":0.75,\"bottom\":0.75,\"left\":0.6,\"right\":0.6}}"),
                MakeContext(workDir));
            Assert.False(layoutResult.IsError);

            var imageResult = await tool.InvokeAsync(new ToolInvocation(
                "word.insert_image",
                "{\"filename\":\"reports/template.docx\",\"imagePath\":\"assets/logo.png\",\"location\":\"header\",\"replaceToken\":\"<<Logo>>\",\"widthInches\":1.0,\"heightInches\":1.0}"),
                MakeContext(workDir));
            Assert.False(imageResult.IsError);

            using var doc = WordprocessingDocument.Open(Path.Combine(workDir, filename), false);
            var header = doc.MainDocumentPart!.HeaderParts.Single().Header!;
            var footer = doc.MainDocumentPart.FooterParts.Single().Footer!;
            var sectionProps = doc.MainDocumentPart.Document.Body!.GetFirstChild<SectionProperties>();

            Assert.NotNull(sectionProps);
            Assert.DoesNotContain("<<Logo>>", header.InnerText);
            Assert.Contains("Quarterly Review", header.InnerText);
            Assert.Contains("Confidential", footer.InnerText);
            Assert.NotEmpty(header.Descendants<Drawing>());
            Assert.Equal(PageOrientationValues.Landscape, sectionProps!.GetFirstChild<PageSize>()?.Orient?.Value);
            Assert.Equal((short)2, sectionProps.GetFirstChild<Columns>()?.ColumnCount?.Value);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static void CreateTemplate(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();

        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(new Run(new Text("{{Title}}"))));
        headerPart.Header.Save();

        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(new Run(new Text("Prepared for {{Name}}"))));
        footerPart.Footer.Save();

        mainPart.Document = new Document(
            new Body(
                new Paragraph(new Run(new Text("Employee: {{Name}}"))),
                new SectionProperties(
                    new HeaderReference { Id = mainPart.GetIdOfPart(headerPart), Type = HeaderFooterValues.Default },
                    new FooterReference { Id = mainPart.GetIdOfPart(footerPart), Type = HeaderFooterValues.Default },
                    new PageSize { Width = 12240U, Height = 15840U },
                    new PageMargin
                    {
                        Top = 1440,
                        Right = 1440U,
                        Bottom = 1440,
                        Left = 1440U,
                        Header = 720U,
                        Footer = 720U,
                        Gutter = 0U,
                    })));

        mainPart.Document.Save();
    }

    private static ToolContext MakeContext(string workDir) => new(
        UserId: Guid.NewGuid(),
        Username: "tester",
        IsAdmin: false,
        IsGlobalAdmin: false,
        AgentId: "agent-1",
        ConversationId: Guid.NewGuid(),
        WorkDirectory: workDir,
        CancellationToken: default);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mla-wordtool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}