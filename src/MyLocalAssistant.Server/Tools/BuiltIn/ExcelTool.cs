using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Comprehensive Excel (.xlsx) tool. Exposes granular cell-level read/write,
/// formatting, formulas, tables, sheets management, and auto-fit.
/// All file paths are scoped to the conversation WorkDirectory.
/// </summary>
internal sealed class ExcelTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "excel";
    public string  Name        => "Excel Tool";
    public string  Description => "Comprehensive Excel (.xlsx) workbook operations: create, read/write cells and ranges, formatting, formulas, tables, sheet management. Files are saved in the conversation work directory.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 8);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Tool definitions ──────────────────────────────────────────────────────

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "excel.create",
            Description: "Create a new empty Excel workbook. Optionally add named sheets. Returns the filename.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Output filename (e.g. 'report.xlsx'). Extension added if missing."},"sheets":{"type":"array","description":"Optional sheet names to pre-create (first is active).","items":{"type":"string"}}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.get_sheet_names",
            Description: "List the names of all sheets in an existing workbook.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"Workbook filename."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.add_sheet",
            Description: "Add a new sheet to an existing workbook.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"New sheet name."},"position":{"type":"integer","description":"1-based insert position. Omit to append."}},"required":["filename","sheet"]}"""),

        new ToolFunctionDto(
            Name: "excel.delete_sheet",
            Description: "Delete a sheet from an existing workbook.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name to delete."}},"required":["filename","sheet"]}"""),

        new ToolFunctionDto(
            Name: "excel.read_range",
            Description: "Read a cell range from an existing workbook. Returns a 2-D array of cell values (row-major).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet if omitted."},"range":{"type":"string","description":"Excel range address, e.g. 'A1:D10'. Omit to read the entire used range."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.read_formulas",
            Description: "Read formula strings from a cell range. Returns a 2-D array matching the range; cells without formulas have an empty string. Formula strings are returned with a leading '=' (e.g. '=SUM(A1:A10)').",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet if omitted."},"range":{"type":"string","description":"Excel range address, e.g. 'A1:D10'. Omit to read the entire used range."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.write_range",
            Description: "Write a 2-D array of values to a cell range in a workbook. Existing content in the range is overwritten. String values starting with '=' are written as Excel formulas (e.g. '=SUM(A1:A5)').",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet."},"startCell":{"type":"string","description":"Top-left cell address, e.g. 'A1'."},"values":{"type":"array","description":"Row-major 2-D array of cell values. Strings starting with '=' are treated as formulas.","items":{"type":"array","items":{}}},"headers":{"type":"array","description":"Optional header row written above 'values'.","items":{"type":"string"}}},"required":["filename","startCell","values"]}"""),

        new ToolFunctionDto(
            Name: "excel.write_cell",
            Description: "Write a single cell. Can set a value, an Excel formula, and/or a number format.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"cell":{"type":"string","description":"Cell address, e.g. 'B3'."},"value":{"description":"Cell value (string, number, boolean, or null to clear)."},"formula":{"type":"string","description":"Excel formula WITHOUT leading '=', e.g. 'SUM(A1:A10)'."},"numberFormat":{"type":"string","description":"Excel number format string, e.g. '#,##0.00', 'dd/mm/yyyy'."}},"required":["filename","cell"]}"""),

        new ToolFunctionDto(
            Name: "excel.format_range",
            Description: "Apply formatting to a cell range: bold, italic, font size, font color, background color, border style, horizontal alignment, and/or number format.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Excel range, e.g. 'A1:F1'."},"bold":{"type":"boolean"},"italic":{"type":"boolean"},"fontSize":{"type":"number","description":"Font size in points."},"fontColor":{"type":"string","description":"HTML hex color, e.g. '#FF0000'."},"backgroundColor":{"type":"string","description":"HTML hex fill color."},"borderStyle":{"type":"string","description":"thin | medium | thick | none","enum":["thin","medium","thick","none"]},"alignment":{"type":"string","description":"left | center | right | general","enum":["left","center","right","general"]},"numberFormat":{"type":"string","description":"Excel number format string."}},"required":["filename","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.set_column_width",
            Description: "Set the width of one or more columns.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"columns":{"type":"array","description":"Column letters or ranges, e.g. ['A','B','D:F'].","items":{"type":"string"}},"width":{"type":"number","description":"Column width in Excel character units."}},"required":["filename","columns","width"]}"""),

        new ToolFunctionDto(
            Name: "excel.set_row_height",
            Description: "Set the height of one or more rows.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"rows":{"type":"array","description":"1-based row numbers.","items":{"type":"integer"}},"height":{"type":"number","description":"Row height in points."}},"required":["filename","rows","height"]}"""),

        new ToolFunctionDto(
            Name: "excel.auto_fit",
            Description: "Auto-fit column widths and optionally row heights for all used cells in a sheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"rows":{"type":"boolean","description":"Also auto-fit row heights. Default false."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.create_table",
            Description: "Convert a cell range into an Excel Table with auto-filter headers.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range that includes the header row, e.g. 'A1:D20'."},"tableName":{"type":"string","description":"Optional table name. Auto-generated if omitted."},"tableStyle":{"type":"string","description":"Excel table style name, e.g. 'TableStyleMedium9'. Defaults to 'TableStyleMedium2'."}},"required":["filename","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.merge_cells",
            Description: "Merge a range of cells.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range to merge, e.g. 'A1:D1'."}},"required":["filename","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.unmerge_cells",
            Description: "Unmerge a previously merged range.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range to unmerge."}},"required":["filename","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.freeze_panes",
            Description: "Freeze rows and/or columns (set freeze pane).",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"rows":{"type":"integer","description":"Number of rows to freeze from top."},"columns":{"type":"integer","description":"Number of columns to freeze from left."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.rename_sheet",
            Description: "Rename a sheet in an existing workbook.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Current sheet name."},"newName":{"type":"string","description":"New sheet name."}},"required":["filename","sheet","newName"]}"""),

        new ToolFunctionDto(
            Name: "excel.copy_sheet",
            Description: "Duplicate an existing sheet within the same workbook.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet to copy."},"newName":{"type":"string","description":"Name for the copy."}},"required":["filename","sheet","newName"]}"""),

        new ToolFunctionDto(
            Name: "excel.insert_rows",
            Description: "Insert blank rows into a sheet. Existing rows shift down.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"row":{"type":"integer","description":"1-based row number to insert before."},"count":{"type":"integer","description":"Number of rows to insert. Default 1."}},"required":["filename","row"]}"""),

        new ToolFunctionDto(
            Name: "excel.delete_rows",
            Description: "Delete a range of rows from a sheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"row":{"type":"integer","description":"1-based first row to delete."},"count":{"type":"integer","description":"Number of rows to delete. Default 1."}},"required":["filename","row"]}"""),

        new ToolFunctionDto(
            Name: "excel.insert_columns",
            Description: "Insert blank columns into a sheet. Existing columns shift right.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"column":{"type":"string","description":"Column letter to insert before, e.g. 'C'."},"count":{"type":"integer","description":"Number of columns to insert. Default 1."}},"required":["filename","column"]}"""),

        new ToolFunctionDto(
            Name: "excel.delete_columns",
            Description: "Delete a range of columns from a sheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"column":{"type":"string","description":"Column letter of first column to delete, e.g. 'C'."},"count":{"type":"integer","description":"Number of columns to delete. Default 1."}},"required":["filename","column"]}"""),

        new ToolFunctionDto(
            Name: "excel.sort_range",
            Description: "Sort rows within a range by one or more key columns.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range to sort, e.g. 'A2:E100' (exclude header row)."},"keys":{"type":"array","description":"Sort key columns.","items":{"type":"object","properties":{"column":{"type":"string","description":"Column letter, e.g. 'B'."},"descending":{"type":"boolean","description":"Sort descending. Default false."}},"required":["column"]}}},"required":["filename","range","keys"]}"""),

        new ToolFunctionDto(
            Name: "excel.find_replace",
            Description: "Find all occurrences of a value in a sheet and optionally replace them. Returns the count of cells affected.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"find":{"type":"string","description":"Value to search for."},"replace":{"type":"string","description":"Replacement value. Omit to report matches without replacing."},"matchCase":{"type":"boolean","description":"Case-sensitive search. Default false."}},"required":["filename","find"]}"""),
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(string? configJson)
    {
        // No configuration needed for this tool.
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var root = doc.RootElement;

            return call.ToolName switch
            {
                "excel.create"          => CreateWorkbook(root, ctx),
                "excel.get_sheet_names" => GetSheetNames(root, ctx),
                "excel.add_sheet"       => AddSheet(root, ctx),
                "excel.delete_sheet"    => DeleteSheet(root, ctx),
                "excel.read_range"      => ReadRange(root, ctx),
                "excel.read_formulas"   => ReadFormulas(root, ctx),
                "excel.write_range"     => WriteRange(root, ctx),
                "excel.write_cell"      => WriteCell(root, ctx),
                "excel.format_range"    => FormatRange(root, ctx),
                "excel.set_column_width"=> SetColumnWidth(root, ctx),
                "excel.set_row_height"  => SetRowHeight(root, ctx),
                "excel.auto_fit"        => AutoFit(root, ctx),
                "excel.create_table"    => CreateTable(root, ctx),
                "excel.merge_cells"     => MergeCells(root, ctx),
                "excel.unmerge_cells"   => UnmergeCells(root, ctx),
                "excel.freeze_panes"    => FreezePanes(root, ctx),
                "excel.rename_sheet"    => RenameSheet(root, ctx),
                "excel.copy_sheet"      => CopySheet(root, ctx),
                "excel.insert_rows"     => InsertRows(root, ctx),
                "excel.delete_rows"     => DeleteRows(root, ctx),
                "excel.insert_columns"  => InsertColumns(root, ctx),
                "excel.delete_columns"  => DeleteColumns(root, ctx),
                "excel.sort_range"      => SortRange(root, ctx),
                "excel.find_replace"    => FindReplace(root, ctx),
                _                       => ToolResult.Error($"Unknown tool: {call.ToolName}"),
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{call.ToolName} failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveFile(JsonElement root, ToolContext ctx)
    {
        var name = root.GetProperty("filename").GetString()
            ?? throw new ArgumentException("filename is required.");
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            name += ".xlsx";
        // Prevent path traversal: only allow simple filenames, no slashes.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid filename: {name}");
        Directory.CreateDirectory(ctx.WorkDirectory);
        return Path.Combine(ctx.WorkDirectory, name);
    }

    private static IXLWorksheet GetSheet(XLWorkbook wb, JsonElement root, bool createIfMissing = false)
    {
        if (root.TryGetProperty("sheet", out var sv) && sv.GetString() is { Length: > 0 } sheetName)
        {
            if (wb.TryGetWorksheet(sheetName, out var ws))
                return ws;
            if (createIfMissing)
                return wb.AddWorksheet(sheetName);
            throw new ArgumentException($"Sheet '{sheetName}' not found.");
        }
        return wb.Worksheets.First();
    }

    private static XLWorkbook OpenOrCreate(string path)
    {
        return File.Exists(path) ? new XLWorkbook(path) : new XLWorkbook();
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private static ToolResult CreateWorkbook(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = new XLWorkbook();

        if (root.TryGetProperty("sheets", out var sheetsEl) && sheetsEl.ValueKind == JsonValueKind.Array)
        {
            bool first = true;
            foreach (var s in sheetsEl.EnumerateArray())
            {
                var name = s.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                wb.AddWorksheet(name);
                first = false;
            }
            if (!first) { /* at least one sheet was added */ }
            else wb.AddWorksheet("Sheet1");
        }
        else
        {
            wb.AddWorksheet("Sheet1");
        }

        wb.SaveAs(path);
        return ToolResult.Ok($"Created workbook '{Path.GetFileName(path)}' in work directory.");
    }

    private static ToolResult GetSheetNames(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        using var wb = new XLWorkbook(path);
        var names = wb.Worksheets.Select(ws => ws.Name).ToList();
        return ToolResult.Ok(JsonSerializer.Serialize(names, s_json));
    }

    private static ToolResult AddSheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var sheetName = root.GetProperty("sheet").GetString()
            ?? throw new ArgumentException("sheet is required.");
        using var wb = OpenOrCreate(path);
        if (wb.TryGetWorksheet(sheetName, out _))
            return ToolResult.Error($"Sheet '{sheetName}' already exists.");
        if (root.TryGetProperty("position", out var pos) && pos.TryGetInt32(out var idx) && idx >= 1)
            wb.AddWorksheet(sheetName, idx);
        else
            wb.AddWorksheet(sheetName);
        wb.SaveAs(path);
        return ToolResult.Ok($"Added sheet '{sheetName}'.");
    }

    private static ToolResult DeleteSheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var sheetName = root.GetProperty("sheet").GetString()
            ?? throw new ArgumentException("sheet is required.");
        using var wb = new XLWorkbook(path);
        if (!wb.TryGetWorksheet(sheetName, out var ws))
            return ToolResult.Error($"Sheet '{sheetName}' not found.");
        if (wb.Worksheets.Count == 1)
            return ToolResult.Error("Cannot delete the only sheet in a workbook.");
        ws.Delete();
        wb.SaveAs(path);
        return ToolResult.Ok($"Deleted sheet '{sheetName}'.");
    }

    private static ToolResult ReadRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        using var wb = new XLWorkbook(path);
        var ws = GetSheet(wb, root);

        IXLRange range;
        if (root.TryGetProperty("range", out var rp) && rp.GetString() is { Length: > 0 } addr)
            range = ws.Range(addr);
        else
            range = ws.RangeUsed() ?? ws.Range("A1:A1");

        var rows = new List<List<object?>>();
        foreach (var row in range.RowsUsed())
        {
            var cells = new List<object?>();
            foreach (var cell in row.Cells())
                cells.Add(cell.CachedValue.IsText ? cell.GetString() : (object?)cell.GetDouble());
            rows.Add(cells);
        }
        return ToolResult.Ok(JsonSerializer.Serialize(rows, s_json));
    }

    private static ToolResult ReadFormulas(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        using var wb = new XLWorkbook(path);
        var ws = GetSheet(wb, root);

        IXLRange range;
        if (root.TryGetProperty("range", out var rp) && rp.GetString() is { Length: > 0 } addr)
            range = ws.Range(addr);
        else
            range = ws.RangeUsed() ?? ws.Range("A1:A1");

        var rows = new List<List<string>>();
        foreach (var row in range.Rows())
        {
            var cells = new List<string>();
            foreach (var cell in row.Cells())
                cells.Add(cell.HasFormula ? "=" + cell.FormulaA1 : "");
            rows.Add(cells);
        }
        return ToolResult.Ok(JsonSerializer.Serialize(rows, s_json));
    }

    private static ToolResult WriteRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var startCell = root.GetProperty("startCell").GetString()
            ?? throw new ArgumentException("startCell is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);

        var startAddr = ws.Cell(startCell).Address;
        int row = startAddr.RowNumber;
        int col = startAddr.ColumnNumber;

        // Write headers if supplied
        if (root.TryGetProperty("headers", out var hdrsEl) && hdrsEl.ValueKind == JsonValueKind.Array)
        {
            int c = col;
            foreach (var h in hdrsEl.EnumerateArray())
            {
                ws.Cell(row, c++).Value = h.GetString() ?? "";
            }
            row++;
        }

        var valuesEl = root.GetProperty("values");
        foreach (var rowEl in valuesEl.EnumerateArray())
        {
            int c = col;
            foreach (var cellEl in rowEl.EnumerateArray())
            {
                var cell = ws.Cell(row, c++);
                // Strings starting with '=' are treated as Excel formulas.
                if (cellEl.ValueKind == JsonValueKind.String &&
                    cellEl.GetString() is { } sv && sv.StartsWith('='))
                    cell.FormulaA1 = sv[1..];
                else
                    SetCellValue(cell, cellEl);
            }
            row++;
        }

        wb.SaveAs(path);
        return ToolResult.Ok($"Wrote data to '{Path.GetFileName(path)}' starting at {startCell}.");
    }

    private static ToolResult WriteCell(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var cellAddr = root.GetProperty("cell").GetString()
            ?? throw new ArgumentException("cell is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var cell = ws.Cell(cellAddr);

        if (root.TryGetProperty("formula", out var formulaEl) && formulaEl.GetString() is { Length: > 0 } formula)
        {
            cell.FormulaA1 = formula;
        }
        else if (root.TryGetProperty("value", out var valueEl))
        {
            if (valueEl.ValueKind == JsonValueKind.Null)
                cell.Value = Blank.Value;
            else
                SetCellValue(cell, valueEl);
        }

        if (root.TryGetProperty("numberFormat", out var nfEl) && nfEl.GetString() is { Length: > 0 } nf)
            cell.Style.NumberFormat.Format = nf;

        wb.SaveAs(path);
        return ToolResult.Ok($"Wrote to cell {cellAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult FormatRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var range = ws.Range(rangeAddr);

        if (root.TryGetProperty("bold", out var bold))
            range.Style.Font.Bold = bold.GetBoolean();
        if (root.TryGetProperty("italic", out var italic))
            range.Style.Font.Italic = italic.GetBoolean();
        if (root.TryGetProperty("fontSize", out var fs) && fs.TryGetDouble(out var fsv))
            range.Style.Font.FontSize = fsv;
        if (root.TryGetProperty("fontColor", out var fc) && fc.GetString() is { Length: > 0 } fcStr)
            range.Style.Font.FontColor = XLColor.FromHtml(fcStr);
        if (root.TryGetProperty("backgroundColor", out var bg) && bg.GetString() is { Length: > 0 } bgStr)
        {
            range.Style.Fill.PatternType = XLFillPatternValues.Solid;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml(bgStr);
        }
        if (root.TryGetProperty("borderStyle", out var bs) && bs.GetString() is { } bsStr)
        {
            var borderStyle = bsStr.ToLowerInvariant() switch
            {
                "thin"   => XLBorderStyleValues.Thin,
                "medium" => XLBorderStyleValues.Medium,
                "thick"  => XLBorderStyleValues.Thick,
                _        => XLBorderStyleValues.None,
            };
            range.Style.Border.OutsideBorder = borderStyle;
        }
        if (root.TryGetProperty("alignment", out var al) && al.GetString() is { } alStr)
        {
            range.Style.Alignment.Horizontal = alStr.ToLowerInvariant() switch
            {
                "left"    => XLAlignmentHorizontalValues.Left,
                "center"  => XLAlignmentHorizontalValues.Center,
                "right"   => XLAlignmentHorizontalValues.Right,
                _         => XLAlignmentHorizontalValues.General,
            };
        }
        if (root.TryGetProperty("numberFormat", out var nf2) && nf2.GetString() is { Length: > 0 } nfStr)
            range.Style.NumberFormat.Format = nfStr;

        wb.SaveAs(path);
        return ToolResult.Ok($"Applied formatting to {rangeAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult SetColumnWidth(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var width = root.GetProperty("width").GetDouble();
        var columnsEl = root.GetProperty("columns");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        foreach (var col in columnsEl.EnumerateArray())
        {
            var spec = col.GetString() ?? "";
            if (spec.Contains(':'))
            {
                var parts = spec.Split(':');
                var range = ws.Columns(parts[0].Trim(), parts[1].Trim());
                foreach (var c in range) c.Width = width;
            }
            else
            {
                ws.Column(spec).Width = width;
            }
        }
        wb.SaveAs(path);
        return ToolResult.Ok($"Set column width to {width} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult SetRowHeight(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var height = root.GetProperty("height").GetDouble();
        var rowsEl = root.GetProperty("rows");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        foreach (var r in rowsEl.EnumerateArray())
        {
            if (r.TryGetInt32(out var rowNum))
                ws.Row(rowNum).Height = height;
        }
        wb.SaveAs(path);
        return ToolResult.Ok($"Set row height to {height} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult AutoFit(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        ws.Columns().AdjustToContents();
        if (root.TryGetProperty("rows", out var rProp) && rProp.GetBoolean())
            ws.Rows().AdjustToContents();
        wb.SaveAs(path);
        return ToolResult.Ok($"Auto-fit applied to '{Path.GetFileName(path)}'.");
    }

    private static ToolResult CreateTable(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var range = ws.Range(rangeAddr);

        var tableStyle = root.TryGetProperty("tableStyle", out var ts) && ts.GetString() is { Length: > 0 } tsStr
            ? tsStr
            : "TableStyleMedium2";

        var tableName = root.TryGetProperty("tableName", out var tn) && tn.GetString() is { Length: > 0 } tnStr
            ? tnStr
            : $"Table{ws.Tables.Count() + 1}";

        var table = range.CreateTable(tableName);
        table.Theme = XLTableTheme.FromName(tableStyle);
        wb.SaveAs(path);
        return ToolResult.Ok($"Created table '{tableName}' on range {rangeAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult MergeCells(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        ws.Range(rangeAddr).Merge();
        wb.SaveAs(path);
        return ToolResult.Ok($"Merged {rangeAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult UnmergeCells(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        ws.Range(rangeAddr).Unmerge();
        wb.SaveAs(path);
        return ToolResult.Ok($"Unmerged {rangeAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult FreezePanes(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        int rows = root.TryGetProperty("rows", out var rEl) && rEl.TryGetInt32(out var r) ? r : 0;
        int cols = root.TryGetProperty("columns", out var cEl) && cEl.TryGetInt32(out var c) ? c : 0;
        if (rows == 0 && cols == 0)
            return ToolResult.Error("Specify at least 'rows' or 'columns' to freeze.");
        ws.SheetView.FreezeRows(rows);
        ws.SheetView.FreezeColumns(cols);
        wb.SaveAs(path);
        return ToolResult.Ok($"Froze {rows} row(s) and {cols} column(s) in '{Path.GetFileName(path)}'.");
    }

    // ── New operations ────────────────────────────────────────────────────────

    private static ToolResult RenameSheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var sheetName = root.GetProperty("sheet").GetString()
            ?? throw new ArgumentException("sheet is required.");
        var newName = root.GetProperty("newName").GetString()
            ?? throw new ArgumentException("newName is required.");
        using var wb = new XLWorkbook(path);
        if (!wb.TryGetWorksheet(sheetName, out var ws))
            return ToolResult.Error($"Sheet '{sheetName}' not found.");
        ws.Name = newName;
        wb.SaveAs(path);
        return ToolResult.Ok($"Renamed sheet '{sheetName}' to '{newName}'.");
    }

    private static ToolResult CopySheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var sheetName = root.GetProperty("sheet").GetString()
            ?? throw new ArgumentException("sheet is required.");
        var newName = root.GetProperty("newName").GetString()
            ?? throw new ArgumentException("newName is required.");
        using var wb = new XLWorkbook(path);
        if (!wb.TryGetWorksheet(sheetName, out var ws))
            return ToolResult.Error($"Sheet '{sheetName}' not found.");
        if (wb.TryGetWorksheet(newName, out _))
            return ToolResult.Error($"A sheet named '{newName}' already exists.");
        ws.CopyTo(newName);
        wb.SaveAs(path);
        return ToolResult.Ok($"Copied sheet '{sheetName}' to '{newName}'.");
    }

    private static ToolResult InsertRows(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var row = root.GetProperty("row").GetInt32();
        var count = root.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : 1;
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        ws.Row(row).InsertRowsAbove(count);
        wb.SaveAs(path);
        return ToolResult.Ok($"Inserted {count} row(s) before row {row} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult DeleteRows(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var row = root.GetProperty("row").GetInt32();
        var count = root.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : 1;
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        ws.Rows(row, row + count - 1).Delete();
        wb.SaveAs(path);
        return ToolResult.Ok($"Deleted {count} row(s) starting at row {row} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult InsertColumns(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var colLetter = root.GetProperty("column").GetString()
            ?? throw new ArgumentException("column is required.");
        var count = root.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : 1;
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        ws.Column(colLetter).InsertColumnsBefore(count);
        wb.SaveAs(path);
        return ToolResult.Ok($"Inserted {count} column(s) before '{colLetter}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult DeleteColumns(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var colLetter = root.GetProperty("column").GetString()
            ?? throw new ArgumentException("column is required.");
        var count = root.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c) ? c : 1;
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        int colNum = XLHelper.GetColumnNumberFromLetter(colLetter);
        ws.Columns(colNum, colNum + count - 1).Delete();
        wb.SaveAs(path);
        return ToolResult.Ok($"Deleted {count} column(s) starting at '{colLetter}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult SortRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        var keysEl = root.GetProperty("keys");
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        var range = ws.Range(rangeAddr);
        var sortEl = range.SortRows;
        foreach (var key in keysEl.EnumerateArray())
        {
            var col = key.GetProperty("column").GetString()
                ?? throw new ArgumentException("keys[].column is required.");
            var desc = key.TryGetProperty("descending", out var dEl) && dEl.GetBoolean();
            int colNum = XLHelper.GetColumnNumberFromLetter(col);
            // sortEl is fluent — column index is relative to range start
            int relCol = colNum - range.FirstCell().Address.ColumnNumber + 1;
            if (desc) sortEl.Add(relCol, XLSortOrder.Descending);
            else       sortEl.Add(relCol, XLSortOrder.Ascending);
        }
        range.Sort();
        wb.SaveAs(path);
        return ToolResult.Ok($"Sorted range {rangeAddr} in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult FindReplace(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var find = root.GetProperty("find").GetString() ?? "";
        var replaceWith = root.TryGetProperty("replace", out var rEl) ? rEl.GetString() : null;
        var matchCase = root.TryGetProperty("matchCase", out var mcEl) && mcEl.GetBoolean();
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        var comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0;
        foreach (var cell in ws.CellsUsed())
        {
            var val = cell.GetString();
            if (val.Contains(find, comp))
            {
                count++;
                if (replaceWith is not null)
                    cell.Value = val.Replace(find, replaceWith, comp);
            }
        }
        if (replaceWith is not null) wb.SaveAs(path);
        var action = replaceWith is not null ? $"Replaced {count} occurrence(s)" : $"Found {count} occurrence(s)";
        return ToolResult.Ok($"{action} of '{find}' in '{Path.GetFileName(path)}'.");
    }

    // ── Value helper ──────────────────────────────────────────────────────────

    private static void SetCellValue(IXLCell cell, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                cell.Value = Blank.Value;
                break;
            case JsonValueKind.True:
                cell.Value = true;
                break;
            case JsonValueKind.False:
                cell.Value = false;
                break;
            case JsonValueKind.Number:
                cell.Value = el.GetDouble();
                break;
            default:
                cell.Value = el.GetString() ?? "";
                break;
        }
    }
}
