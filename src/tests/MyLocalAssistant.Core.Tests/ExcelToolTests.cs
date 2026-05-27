using System.Text.Json;
using ClosedXML.Excel;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace MyLocalAssistant.Core.Tests;

public sealed class ExcelToolTests
{
    [Fact]
    public async Task WriteNamedRange_fills_template_cell_and_readNamedRange_returns_positioned_value()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(workDir, "template.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Template");
                ws.Cell("B2").Value = string.Empty;
                wb.NamedRanges.Add("CustomerName", "'Template'!$B$2");
                wb.SaveAs(path);
            }

            var tool = new ExcelTool();
            var write = await tool.InvokeAsync(
                new ToolInvocation("excel.write_named_range", "{\"filename\":\"template.xlsx\",\"name\":\"CustomerName\",\"value\":\"Acme Ltd\"}"),
                MakeContext(workDir));

            Assert.False(write.IsError);

            using (var wb = new XLWorkbook(path))
            {
                Assert.Equal("Acme Ltd", wb.Worksheet("Template").Cell("B2").GetString());
            }

            var read = await tool.InvokeAsync(
                new ToolInvocation("excel.read_named_range", "{\"filename\":\"template.xlsx\",\"name\":\"CustomerName\"}"),
                MakeContext(workDir));

            Assert.False(read.IsError);
            using var doc = JsonDocument.Parse(read.Content);
            Assert.Equal(2, doc.RootElement.GetProperty("firstRow").GetInt32());
            Assert.Equal("B", doc.RootElement.GetProperty("firstColumn").GetString());
            var value = doc.RootElement.GetProperty("rows").EnumerateArray().First().EnumerateArray().First().GetString();
            Assert.Equal("Acme Ltd", value);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddChart_creates_native_chart_part_in_subfolder_workbook()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "reports"));
            var workbookPath = Path.Combine(workDir, "reports", "dashboard.xlsx");

            using (var wb = new XLWorkbook())
            {
                var data = wb.AddWorksheet("Data");
                data.Cell("A1").Value = "Month";
                data.Cell("B1").Value = "Sales";
                data.Cell("A2").Value = "Jan";
                data.Cell("A3").Value = "Feb";
                data.Cell("A4").Value = "Mar";
                data.Cell("B2").Value = 10;
                data.Cell("B3").Value = 15;
                data.Cell("B4").Value = 18;
                wb.AddWorksheet("Dashboard");
                wb.SaveAs(workbookPath);
            }

            var tool = new ExcelTool();
            var result = await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"reports/dashboard.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"column\",\"title\":\"Quarterly Sales\",\"categoryRange\":\"$A$2:$A$4\",\"series\":[{\"name\":\"Sales\",\"valuesRange\":\"$B$2:$B$4\"}],\"topLeftCell\":\"D2\"}"),
                MakeContext(workDir));

            Assert.False(result.IsError);

            using var workbook = SpreadsheetDocument.Open(workbookPath, false);
            var dashboardPart = workbook.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .Where(sheet => sheet.Name?.Value == "Dashboard")
                .Select(sheet => workbook.WorkbookPart.GetPartById(sheet.Id!.Value!))
                .OfType<WorksheetPart>()
                .Single();

            Assert.NotNull(dashboardPart.DrawingsPart);
            Assert.Single(dashboardPart.DrawingsPart!.ChartParts);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task AddChart_supports_extended_chart_types_and_axis_controls()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var workbookPath = Path.Combine(workDir, "extended-charts.xlsx");

            using (var wb = new XLWorkbook())
            {
                var data = wb.AddWorksheet("Data");
                data.Cell("A1").Value = "Month";
                data.Cell("B1").Value = "Sales";
                data.Cell("C1").Value = "Forecast";
                data.Cell("D1").Value = "Margin";
                data.Cell("E1").Value = "Segment";
                data.Cell("F1").Value = "Segment Value";
                data.Cell("G1").Value = "Spend";
                data.Cell("H1").Value = "Leads";

                data.Cell("A2").Value = "Jan";
                data.Cell("A3").Value = "Feb";
                data.Cell("A4").Value = "Mar";
                data.Cell("A5").Value = "Apr";
                data.Cell("B2").Value = 100;
                data.Cell("B3").Value = 120;
                data.Cell("B4").Value = 140;
                data.Cell("B5").Value = 160;
                data.Cell("C2").Value = 90;
                data.Cell("C3").Value = 110;
                data.Cell("C4").Value = 135;
                data.Cell("C5").Value = 150;
                data.Cell("D2").Value = 21;
                data.Cell("D3").Value = 24;
                data.Cell("D4").Value = 26;
                data.Cell("D5").Value = 28;
                data.Cell("E2").Value = "North";
                data.Cell("E3").Value = "South";
                data.Cell("E4").Value = "West";
                data.Cell("E5").Value = "East";
                data.Cell("F2").Value = 35;
                data.Cell("F3").Value = 25;
                data.Cell("F4").Value = 20;
                data.Cell("F5").Value = 20;
                data.Cell("G2").Value = 10;
                data.Cell("G3").Value = 20;
                data.Cell("G4").Value = 30;
                data.Cell("G5").Value = 40;
                data.Cell("H2").Value = 50;
                data.Cell("H3").Value = 65;
                data.Cell("H4").Value = 78;
                data.Cell("H5").Value = 95;
                wb.AddWorksheet("Dashboard");
                wb.SaveAs(workbookPath);
            }

            var tool = new ExcelTool();
            var ctx = MakeContext(workDir);

            Assert.False((await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"extended-charts.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"stackedColumn\",\"title\":\"Stacked Sales\",\"categoryRange\":\"$A$2:$A$5\",\"categoryAxisTitle\":\"Month\",\"valueAxisTitle\":\"Revenue\",\"showDataLabels\":true,\"series\":[{\"name\":\"Sales\",\"valuesRange\":\"$B$2:$B$5\",\"color\":\"#1F4E79\"},{\"name\":\"Forecast\",\"valuesRange\":\"$C$2:$C$5\",\"color\":\"#D97A00\"}],\"topLeftCell\":\"B2\"}"),
                ctx)).IsError);

            Assert.False((await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"extended-charts.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"area\",\"title\":\"Area Trend\",\"categoryRange\":\"$A$2:$A$5\",\"showLegend\":false,\"series\":[{\"name\":\"Sales\",\"valuesRange\":\"$B$2:$B$5\",\"color\":\"#6AA84F\"}],\"topLeftCell\":\"L2\"}"),
                ctx)).IsError);

            Assert.False((await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"extended-charts.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"doughnut\",\"title\":\"Segment Mix\",\"categoryRange\":\"$E$2:$E$5\",\"showDataLabels\":true,\"series\":[{\"name\":\"Mix\",\"valuesRange\":\"$F$2:$F$5\"}],\"topLeftCell\":\"B22\"}"),
                ctx)).IsError);

            Assert.False((await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"extended-charts.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"scatter\",\"title\":\"Conversion Curve\",\"categoryAxisTitle\":\"Spend\",\"valueAxisTitle\":\"Leads\",\"series\":[{\"name\":\"Pipeline\",\"xValuesRange\":\"$G$2:$G$5\",\"valuesRange\":\"$H$2:$H$5\",\"color\":\"#C00000\"}],\"topLeftCell\":\"L22\"}"),
                ctx)).IsError);

            Assert.False((await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.add_chart",
                    "{\"filename\":\"extended-charts.xlsx\",\"targetSheet\":\"Dashboard\",\"dataSheet\":\"Data\",\"chartType\":\"combo\",\"title\":\"Revenue vs Margin\",\"categoryRange\":\"$A$2:$A$5\",\"categoryAxisTitle\":\"Month\",\"valueAxisTitle\":\"Revenue\",\"secondaryValueAxisTitle\":\"Margin %\",\"legendPosition\":\"bottom\",\"series\":[{\"name\":\"Revenue\",\"valuesRange\":\"$B$2:$B$5\",\"chartType\":\"column\",\"color\":\"#4F81BD\"},{\"name\":\"Margin\",\"valuesRange\":\"$D$2:$D$5\",\"chartType\":\"line\",\"secondaryAxis\":true,\"color\":\"#9E480E\"}],\"topLeftCell\":\"V2\"}"),
                ctx)).IsError);

            using var workbook = SpreadsheetDocument.Open(workbookPath, false);
            var dashboardPart = workbook.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>()
                .Where(sheet => sheet.Name?.Value == "Dashboard")
                .Select(sheet => workbook.WorkbookPart.GetPartById(sheet.Id!.Value!))
                .OfType<WorksheetPart>()
                .Single();

            var chartParts = dashboardPart.DrawingsPart!.ChartParts.ToList();
            Assert.Equal(5, chartParts.Count);
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.BarGrouping>().Any(group => group.Val?.Value == C.BarGroupingValues.Stacked));
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.AreaChart>().Any());
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.DoughnutChart>().Any());
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.ScatterChart>().Any());
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.DataLabels>().Any());
            Assert.Contains(chartParts, chartPart => chartPart.ChartSpace.Descendants<C.ChartShapeProperties>().Any());

            var comboPart = chartParts.Single(chartPart => chartPart.ChartSpace.Descendants<A.Text>().Any(text => text.Text == "Revenue vs Margin"));
            var comboTexts = comboPart.ChartSpace.Descendants<A.Text>().Select(text => text.Text).ToList();
            Assert.True(comboPart.ChartSpace.Descendants<C.BarChart>().Any());
            Assert.True(comboPart.ChartSpace.Descendants<C.LineChart>().Any());
            Assert.Equal(2, comboPart.ChartSpace.Descendants<C.ValueAxis>().Count());
            Assert.Contains("Month", comboTexts);
            Assert.Contains("Revenue", comboTexts);
            Assert.Contains("Margin %", comboTexts);
            Assert.True(comboPart.ChartSpace.Descendants<C.Legend>().Any());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreatePivotReport_writes_grouped_matrix_summary()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "reports"));
            var workbookPath = Path.Combine(workDir, "reports", "summary.xlsx");

            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Data");
                ws.Cell("A1").Value = "Region";
                ws.Cell("B1").Value = "Quarter";
                ws.Cell("C1").Value = "Amount";
                ws.Cell("A2").Value = "East";
                ws.Cell("B2").Value = "Q1";
                ws.Cell("C2").Value = 12;
                ws.Cell("A3").Value = "East";
                ws.Cell("B3").Value = "Q2";
                ws.Cell("C3").Value = 20;
                ws.Cell("A4").Value = "West";
                ws.Cell("B4").Value = "Q1";
                ws.Cell("C4").Value = 8;
                ws.Cell("A5").Value = "West";
                ws.Cell("B5").Value = "Q2";
                ws.Cell("C5").Value = 14;
                wb.SaveAs(workbookPath);
            }

            var tool = new ExcelTool();
            var result = await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.create_pivot_report",
                    "{\"filename\":\"reports/summary.xlsx\",\"sourceSheet\":\"Data\",\"sourceRange\":\"A1:C5\",\"reportSheet\":\"Summary\",\"rowFields\":[\"Region\"],\"columnField\":\"Quarter\",\"values\":[{\"field\":\"Amount\",\"summary\":\"sum\",\"label\":\"Amount\"}],\"includeGrandTotal\":true}"),
                MakeContext(workDir));

            Assert.False(result.IsError);

            using var workbook = new XLWorkbook(workbookPath);
            var summary = workbook.Worksheet("Summary");
            Assert.Equal("Region", summary.Cell("A1").GetString());
            Assert.Equal("Q1 - Amount", summary.Cell("B1").GetString());
            Assert.Equal("Q2 - Amount", summary.Cell("C1").GetString());
            Assert.Equal("Grand Total - Amount", summary.Cell("D1").GetString());
            Assert.Equal("East", summary.Cell("A2").GetString());
            Assert.Equal(12d, summary.Cell("B2").GetDouble());
            Assert.Equal(20d, summary.Cell("C2").GetDouble());
            Assert.Equal(32d, summary.Cell("D2").GetDouble());
            Assert.Equal("West", summary.Cell("A3").GetString());
            Assert.Equal(8d, summary.Cell("B3").GetDouble());
            Assert.Equal(14d, summary.Cell("C3").GetDouble());
            Assert.Equal(22d, summary.Cell("D3").GetDouble());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Calculation_mode_recalc_and_formula_evaluation_refresh_workbook_results()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var workbookPath = Path.Combine(workDir, "calc.xlsx");

            var tool = new ExcelTool();
            var ctx = MakeContext(workDir);

            var create = await tool.InvokeAsync(
                new ToolInvocation("excel.create", "{\"filename\":\"calc.xlsx\",\"sheets\":[\"Data\"]}"),
                ctx);
            Assert.False(create.IsError);

            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.write_cell", "{\"filename\":\"calc.xlsx\",\"sheet\":\"Data\",\"cell\":\"A1\",\"value\":4}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.write_cell", "{\"filename\":\"calc.xlsx\",\"sheet\":\"Data\",\"cell\":\"A2\",\"value\":6}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.write_cell", "{\"filename\":\"calc.xlsx\",\"sheet\":\"Data\",\"cell\":\"A3\",\"formula\":\"SUM(A1:A2)\"}"), ctx)).IsError);

            var setMode = await tool.InvokeAsync(
                new ToolInvocation("excel.set_calculation_mode", "{\"filename\":\"calc.xlsx\",\"mode\":\"manual\"}"),
                ctx);
            Assert.False(setMode.IsError);

            var recalc = await tool.InvokeAsync(
                new ToolInvocation("excel.recalculate", "{\"filename\":\"calc.xlsx\"}"),
                ctx);
            Assert.False(recalc.IsError);

            var evaluate = await tool.InvokeAsync(
                new ToolInvocation("excel.evaluate_formula", "{\"filename\":\"calc.xlsx\",\"formula\":\"SUM(Data!A1:A3)\"}"),
                ctx);
            Assert.False(evaluate.IsError);

            var read = await tool.InvokeAsync(
                new ToolInvocation("excel.read_range", "{\"filename\":\"calc.xlsx\",\"sheet\":\"Data\",\"range\":\"A3:A3\"}"),
                ctx);
            Assert.False(read.IsError);

            using (var wb = new XLWorkbook(workbookPath))
            {
                Assert.Equal(XLCalculateMode.Manual, wb.CalculateMode);
            }

            using var readJson = JsonDocument.Parse(read.Content);
            Assert.Equal(10d, readJson.RootElement.GetProperty("rows").EnumerateArray().Single().EnumerateArray().Single().GetDouble());
            using var evalJson = JsonDocument.Parse(evaluate.Content);
            Assert.Equal(20d, evalJson.RootElement.GetProperty("value").GetDouble());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreatePivotTable_creates_native_refreshable_pivot_with_filters()
    {
        var workDir = CreateTempDirectory();
        try
        {
            var workbookPath = Path.Combine(workDir, "native-pivot.xlsx");

            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Data");
                ws.Cell("A1").Value = "Region";
                ws.Cell("B1").Value = "Quarter";
                ws.Cell("C1").Value = "Department";
                ws.Cell("D1").Value = "Amount";
                ws.Cell("A2").Value = "East";
                ws.Cell("B2").Value = "Q1";
                ws.Cell("C2").Value = "Sales";
                ws.Cell("D2").Value = 12;
                ws.Cell("A3").Value = "East";
                ws.Cell("B3").Value = "Q2";
                ws.Cell("C3").Value = "Sales";
                ws.Cell("D3").Value = 19;
                ws.Cell("A4").Value = "West";
                ws.Cell("B4").Value = "Q1";
                ws.Cell("C4").Value = "Support";
                ws.Cell("D4").Value = 8;
                ws.Cell("A5").Value = "West";
                ws.Cell("B5").Value = "Q2";
                ws.Cell("C5").Value = "Support";
                ws.Cell("D5").Value = 14;
                wb.AddWorksheet("Pivot");
                wb.SaveAs(workbookPath);
            }

            var tool = new ExcelTool();
            var result = await tool.InvokeAsync(
                new ToolInvocation(
                    "excel.create_pivot_table",
                    "{\"filename\":\"native-pivot.xlsx\",\"sourceSheet\":\"Data\",\"sourceRange\":\"A1:D5\",\"targetSheet\":\"Pivot\",\"targetCell\":\"B3\",\"name\":\"RegionalPivot\",\"rowFields\":[\"Region\"],\"columnFields\":[\"Quarter\"],\"filterFields\":[\"Department\"],\"values\":[{\"field\":\"Amount\",\"summary\":\"sum\",\"label\":\"Total Amount\",\"numberFormat\":\"#,##0\"}],\"refreshOnOpen\":true,\"saveSourceData\":true,\"repeatRowLabels\":true,\"classicLayout\":true}"),
                MakeContext(workDir));

            Assert.False(result.IsError);

            using var workbook = SpreadsheetDocument.Open(workbookPath, false);
            Assert.Single(workbook.WorkbookPart!.PivotTableCacheDefinitionParts);
            var pivotSheet = workbook.WorkbookPart.Workbook.Sheets!.Elements<Sheet>()
                .Where(sheet => sheet.Name?.Value == "Pivot")
                .Select(sheet => workbook.WorkbookPart.GetPartById(sheet.Id!.Value!))
                .OfType<WorksheetPart>()
                .Single();
            Assert.Single(pivotSheet.PivotTableParts);

            var pivotPart = pivotSheet.PivotTableParts.Single();
            var pivotDefinition = pivotPart.PivotTableDefinition;
            var cacheDefinition = workbook.WorkbookPart.PivotTableCacheDefinitionParts.Single().PivotCacheDefinition;
            Assert.Equal("RegionalPivot", pivotDefinition?.Name?.Value);
            Assert.True(cacheDefinition?.RefreshOnLoad?.Value ?? false);
            Assert.NotNull(pivotDefinition?.RowFields);
            Assert.NotNull(pivotDefinition?.ColumnFields);
            Assert.NotNull(pivotDefinition?.PageFields);
            Assert.NotNull(pivotDefinition?.DataFields);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Visual_object_commands_add_images_links_comments_text_boxes_and_shapes()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "assets"));
            File.WriteAllBytes(
                Path.Combine(workDir, "assets", "logo.png"),
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9pJ6f0sAAAAASUVORK5CYII="));

            var tool = new ExcelTool();
            var ctx = MakeContext(workDir);

            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.create", "{\"filename\":\"visuals.xlsx\",\"sheets\":[\"Dashboard\"]}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.add_image", "{\"filename\":\"visuals.xlsx\",\"sheet\":\"Dashboard\",\"imagePath\":\"assets/logo.png\",\"topLeftCell\":\"B2\",\"widthPixels\":24,\"heightPixels\":24}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.add_hyperlink", "{\"filename\":\"visuals.xlsx\",\"sheet\":\"Dashboard\",\"cell\":\"D4\",\"address\":\"https://example.com/report\",\"text\":\"Open report\"}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.add_comment", "{\"filename\":\"visuals.xlsx\",\"sheet\":\"Dashboard\",\"cell\":\"E5\",\"text\":\"Reviewer note\",\"author\":\"Copilot\",\"visible\":true}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.add_text_box", "{\"filename\":\"visuals.xlsx\",\"sheet\":\"Dashboard\",\"topLeftCell\":\"G2\",\"text\":\"Quarterly callout\",\"widthColumns\":3,\"heightRows\":2}"), ctx)).IsError);
            Assert.False((await tool.InvokeAsync(new ToolInvocation("excel.add_shape", "{\"filename\":\"visuals.xlsx\",\"sheet\":\"Dashboard\",\"topLeftCell\":\"J2\",\"shapeType\":\"ellipse\",\"text\":\"KPI\",\"widthColumns\":2,\"heightRows\":2}"), ctx)).IsError);

            using var workbook = new XLWorkbook(Path.Combine(workDir, "visuals.xlsx"));
            var sheet = workbook.Worksheet("Dashboard");
            Assert.True(sheet.Cell("D4").HasHyperlink);
            Assert.Equal("Open report", sheet.Cell("D4").GetString());
            Assert.True(sheet.Cell("E5").HasComment);
            Assert.Contains("Reviewer note", sheet.Cell("E5").GetComment().Text, StringComparison.Ordinal);
            Assert.Single(sheet.Pictures);

            using var openXml = SpreadsheetDocument.Open(Path.Combine(workDir, "visuals.xlsx"), false);
            var worksheetPart = openXml.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>()
                .Where(sheetInfo => sheetInfo.Name?.Value == "Dashboard")
                .Select(sheetInfo => openXml.WorkbookPart.GetPartById(sheetInfo.Id!.Value!))
                .OfType<WorksheetPart>()
                .Single();
            Assert.NotNull(worksheetPart.DrawingsPart);
            Assert.True(worksheetPart.DrawingsPart!.WorksheetDrawing!.Elements<Xdr.TwoCellAnchor>().Count() >= 3);
            Assert.True(worksheetPart.DrawingsPart.WorksheetDrawing.Descendants<Xdr.Shape>().Count() >= 2);
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
        var path = Path.Combine(Path.GetTempPath(), "mla-exceltool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}