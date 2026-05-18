using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class PowerPointToolTests
{
    [Fact]
    public async Task DuplicateSlide_creates_independent_copy_and_replaceText_updates_template_content()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var tool = new PowerPointTool();
            var ctx = MakeContext(workDir);

            var create = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.create", "{\"filename\":\"deck.pptx\",\"title\":\"{{Title}}\",\"subtitle\":\"Prepared for {{Name}}\"}"),
                ctx);
            Assert.False(create.IsError);

            var duplicate = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.duplicate_slide", "{\"filename\":\"deck.pptx\",\"slideIndex\":1}"),
                ctx);
            Assert.False(duplicate.IsError);

            var replace = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.replace_text", "{\"filename\":\"deck.pptx\",\"replacements\":[{\"find\":\"{{Title}}\",\"replace\":\"Board Pack\"},{\"find\":\"{{Name}}\",\"replace\":\"Alice\"}]}"),
                ctx);
            Assert.False(replace.IsError);

            var updateSecondSlide = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.write_slide", "{\"filename\":\"deck.pptx\",\"slideIndex\":2,\"title\":\"Board Pack 2\",\"body\":\"Second slide\"}"),
                ctx);
            Assert.False(updateSecondSlide.IsError);

            var read = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.read", "{\"filename\":\"deck.pptx\"}"),
                ctx);
            Assert.False(read.IsError);

            using var doc = JsonDocument.Parse(read.Content);
            Assert.Equal(2, doc.RootElement.GetProperty("slideCount").GetInt32());
            var slides = doc.RootElement.GetProperty("slides").EnumerateArray().ToList();
            Assert.Equal("Board Pack", slides[0].GetProperty("title").GetString());
            Assert.Contains("Alice", slides[0].GetProperty("body").GetString());
            Assert.Equal("Board Pack 2", slides[1].GetProperty("title").GetString());
            Assert.Contains("Second slide", slides[1].GetProperty("body").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Template_slide_image_chart_and_branding_commands_update_the_new_slide()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "presentations"));
            Directory.CreateDirectory(Path.Combine(workDir, "assets"));
            File.WriteAllBytes(
                Path.Combine(workDir, "assets", "logo.png"),
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9pJ6f0sAAAAASUVORK5CYII="));

            var tool = new PowerPointTool();
            var ctx = MakeContext(workDir);

            var create = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.create", "{\"filename\":\"presentations/deck.pptx\",\"title\":\"{{Title}}\",\"subtitle\":\"Prepared for {{Name}}\"}"),
                ctx);
            Assert.False(create.IsError);

            var fromTemplate = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_slide_from_template", "{\"filename\":\"presentations/deck.pptx\",\"templateSlideIndex\":1,\"title\":\"Board Review\",\"body\":\"Quarterly summary\",\"replacements\":[{\"find\":\"{{Name}}\",\"replace\":\"Alice\"}]}"),
                ctx);
            Assert.False(fromTemplate.IsError);

            var addImage = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_image", "{\"filename\":\"presentations/deck.pptx\",\"slideIndex\":2,\"imagePath\":\"assets/logo.png\",\"x\":0.9,\"y\":1.5,\"width\":1.0,\"height\":1.0}"),
                ctx);
            Assert.False(addImage.IsError);

            var addChart = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_chart", "{\"filename\":\"presentations/deck.pptx\",\"slideIndex\":2,\"chartType\":\"stackedColumn\",\"title\":\"Revenue\",\"categories\":[\"Q1\",\"Q2\"],\"series\":[{\"name\":\"Revenue\",\"values\":[12,18]},{\"name\":\"Cost\",\"values\":[4,6]}]}"),
                ctx);
            Assert.False(addChart.IsError);

            var branding = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.apply_branding", "{\"filename\":\"presentations/deck.pptx\",\"slideIndices\":[2],\"backgroundColor\":\"#F5F1E8\",\"titleColor\":\"#1F2A44\",\"bodyColor\":\"#3F4C63\",\"accentColor\":\"#0E5A7A\",\"footerText\":\"Confidential\",\"footerColor\":\"#FFFFFF\"}"),
                ctx);
            Assert.False(branding.IsError);

            var read = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.read", "{\"filename\":\"presentations/deck.pptx\"}"),
                ctx);
            Assert.False(read.IsError);

            using var readJson = JsonDocument.Parse(read.Content);
            Assert.Equal(2, readJson.RootElement.GetProperty("slideCount").GetInt32());
            var slides = readJson.RootElement.GetProperty("slides").EnumerateArray().ToList();
            Assert.Equal("Board Review", slides[1].GetProperty("title").GetString());
            Assert.Contains("Quarterly summary", slides[1].GetProperty("body").GetString());

            using var presentation = PresentationDocument.Open(Path.Combine(workDir, "presentations", "deck.pptx"), false);
            var slideIds = presentation.PresentationPart!.Presentation.SlideIdList!.Elements<SlideId>().ToList();
            var secondSlide = (SlidePart)presentation.PresentationPart.GetPartById(slideIds[1].RelationshipId!.Value!);

            Assert.NotEmpty(secondSlide.Slide.Descendants<Picture>());
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => (shape.TextBody?.InnerText ?? string.Empty).Contains("Revenue", StringComparison.Ordinal));
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => (shape.TextBody?.InnerText ?? string.Empty).Contains("Board Review", StringComparison.Ordinal));
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => string.Equals(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, "Brand Background", StringComparison.Ordinal));
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => string.Equals(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, "Brand Footer Text", StringComparison.Ordinal));
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => string.Equals(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, "Brand Footer Text", StringComparison.Ordinal) && (shape.TextBody?.InnerText ?? string.Empty).Contains("Confidential", StringComparison.Ordinal));
            Assert.Contains(secondSlide.Slide.Descendants<Shape>(), shape => (shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value ?? string.Empty).StartsWith("Chart Segment", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Role_aware_template_updates_and_line_pie_charts_preserve_slide_content_targets()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "presentations"));

            var tool = new PowerPointTool();
            var ctx = MakeContext(workDir);
            var deckPath = Path.Combine(workDir, "presentations", "role-aware.pptx");

            var create = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.create", "{\"filename\":\"presentations/role-aware.pptx\",\"title\":\"Template Title\",\"subtitle\":\"Template Body\"}"),
                ctx);
            Assert.False(create.IsError);

            ReorderTemplatePlaceholders(deckPath);

            var lineSlide = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_slide_from_template", "{\"filename\":\"presentations/role-aware.pptx\",\"templateSlideIndex\":1,\"title\":\"Revenue Trend\",\"body\":\"Monthly performance\"}"),
                ctx);
            Assert.False(lineSlide.IsError);

            var addLineChart = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_chart", "{\"filename\":\"presentations/role-aware.pptx\",\"slideIndex\":2,\"chartType\":\"line\",\"title\":\"Revenue Trend\",\"categories\":[\"Jan\",\"Feb\",\"Mar\"],\"series\":[{\"name\":\"Actual\",\"values\":[11,15,19]},{\"name\":\"Target\",\"values\":[10,14,18]}]}"),
                ctx);
            Assert.False(addLineChart.IsError);

            var pieSlide = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_slide_from_template", "{\"filename\":\"presentations/role-aware.pptx\",\"templateSlideIndex\":1,\"title\":\"Revenue Mix\",\"body\":\"Portfolio composition\"}"),
                ctx);
            Assert.False(pieSlide.IsError);

            var addPieChart = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.add_chart", "{\"filename\":\"presentations/role-aware.pptx\",\"slideIndex\":3,\"chartType\":\"pie\",\"title\":\"Revenue Mix\",\"categories\":[\"Software\",\"Services\",\"Support\"],\"series\":[{\"name\":\"Revenue\",\"values\":[52,31,17]}]}"),
                ctx);
            Assert.False(addPieChart.IsError);

            var branding = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.apply_branding", "{\"filename\":\"presentations/role-aware.pptx\",\"slideIndices\":[2,3],\"backgroundColor\":\"#F7F4EE\",\"titleColor\":\"#14213D\",\"bodyColor\":\"#334155\",\"accentColor\":\"#0E5A7A\",\"footerText\":\"Internal\"}"),
                ctx);
            Assert.False(branding.IsError);

            var updateLineSlide = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.write_slide", "{\"filename\":\"presentations/role-aware.pptx\",\"slideIndex\":2,\"title\":\"Revenue Trend Updated\",\"body\":\"Updated monthly performance\"}"),
                ctx);
            Assert.False(updateLineSlide.IsError);

            var read = await tool.InvokeAsync(
                new ToolInvocation("powerpoint.read", "{\"filename\":\"presentations/role-aware.pptx\"}"),
                ctx);
            Assert.False(read.IsError);

            using var readJson = JsonDocument.Parse(read.Content);
            var slides = readJson.RootElement.GetProperty("slides").EnumerateArray().ToList();
            Assert.Equal("Revenue Trend Updated", slides[1].GetProperty("title").GetString());
            Assert.Contains("Updated monthly performance", slides[1].GetProperty("body").GetString());
            Assert.Equal("Revenue Mix", slides[2].GetProperty("title").GetString());
            Assert.Contains("Portfolio composition", slides[2].GetProperty("body").GetString());

            using var presentation = PresentationDocument.Open(deckPath, false);
            var slideIds = presentation.PresentationPart!.Presentation.SlideIdList!.Elements<SlideId>().ToList();
            var secondSlide = (SlidePart)presentation.PresentationPart.GetPartById(slideIds[1].RelationshipId!.Value!);
            var thirdSlide = (SlidePart)presentation.PresentationPart.GetPartById(slideIds[2].RelationshipId!.Value!);

            Assert.Contains(secondSlide.Slide.Descendants<Picture>(), picture => string.Equals(picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Name?.Value, "Chart Surface Line", StringComparison.Ordinal));
            Assert.Contains(thirdSlide.Slide.Descendants<Picture>(), picture => string.Equals(picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Name?.Value, "Chart Surface Pie", StringComparison.Ordinal));
            Assert.Contains(thirdSlide.Slide.Descendants<Shape>(), shape => (shape.TextBody?.InnerText ?? string.Empty).Contains("Software", StringComparison.Ordinal));
            Assert.Contains(thirdSlide.Slide.Descendants<Shape>(), shape => (shape.TextBody?.InnerText ?? string.Empty).Contains("Services", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
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
        var path = Path.Combine(Path.GetTempPath(), "mla-ppttool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ReorderTemplatePlaceholders(string presentationPath)
    {
        using var presentation = PresentationDocument.Open(presentationPath, true);
        var slideId = presentation.PresentationPart!.Presentation.SlideIdList!.Elements<SlideId>().First();
        var slidePart = (SlidePart)presentation.PresentationPart.GetPartById(slideId.RelationshipId!.Value!);
        var shapeTree = slidePart.Slide.CommonSlideData!.ShapeTree!;
        var titleShape = shapeTree.Elements<Shape>().First(shape => string.Equals(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, "Title", StringComparison.Ordinal));
        var bodyShape = shapeTree.Elements<Shape>().First(shape => string.Equals(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, "Content", StringComparison.Ordinal));

        titleShape.Remove();
        bodyShape.Remove();
        shapeTree.Append(bodyShape);
        shapeTree.Append(titleShape);
        slidePart.Slide.Save();
    }
}