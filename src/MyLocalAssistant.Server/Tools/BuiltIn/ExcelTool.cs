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
            Description: "Read a cell range. Returns {firstRow, firstColumn, rows} — the 2-D data array plus starting position metadata so you know which Excel row/column each value belongs to. Correct types are preserved: booleans, dates (yyyy-MM-dd), numbers, and text. Use firstRow when constructing subsequent write_cell or format_range addresses.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet if omitted."},"range":{"type":"string","description":"Excel range address, e.g. 'A1:D10'. Omit to read the entire used range."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.read_formulas",
            Description: "Read formula strings from a cell range. Returns a 2-D array matching the range; cells without formulas have an empty string. Formula strings are returned with a leading '=' (e.g. '=SUM(A1:A10)').",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet if omitted."},"range":{"type":"string","description":"Excel range address, e.g. 'A1:D10'. Omit to read the entire used range."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.write_range",
            Description: "Write a 2-D array of values and formulas to a workbook. Strings starting with '=' are written as Excel formulas. Examples of useful formulas: '=SUM(B2:B100)', '=AVERAGE(C:C)', '=COUNTA(A:A)-1', '=VLOOKUP(A2,$Data.$A:$C,3,FALSE)', '=SUMIF(B:B,\"Active\",D:D)', '=COUNTIF(C:C,\">\"+E1)', '=IF(D2>1000,\"High\",\"Low\")', '=IFERROR(VLOOKUP(A2,Sheet2!A:B,2,0),\"\")' , '=TEXT(TODAY(),\"dd/mm/yyyy\")'. Write headers first via the headers property for clean table structure.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet."},"startCell":{"type":"string","description":"Top-left cell address, e.g. 'A1'."},"values":{"type":"array","description":"Row-major 2-D array of cell values. Strings starting with '=' are treated as formulas.","items":{"type":"array","items":{}}},"headers":{"type":"array","description":"Optional header row written above 'values'.","items":{"type":"string"}}},"required":["filename","startCell","values"]}"""),

        new ToolFunctionDto(
            Name: "excel.write_cell",
            Description: "Write a single cell. Can set a value, an Excel formula, and/or a number format.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"cell":{"type":"string","description":"Cell address, e.g. 'B3'."},"value":{"description":"Cell value (string, number, boolean, or null to clear)."},"formula":{"type":"string","description":"Excel formula WITHOUT leading '=', e.g. 'SUM(A1:A10)'."},"numberFormat":{"type":"string","description":"Excel number format string, e.g. '#,##0.00', 'dd/mm/yyyy'."}},"required":["filename","cell"]}"""),

        new ToolFunctionDto(
            Name: "excel.format_range",
            Description: "Apply formatting to a cell range: bold, italic, font size, font/background color, outside and inside borders, horizontal and vertical alignment, text wrap, number format, and cell lock state. Common number formats: '#,##0.00' (currency), '0%' (percent), 'dd/mm/yyyy' (date), '#,##0' (integer with separator). Set locked=false on data-entry cells before calling excel.protect_sheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Excel range, e.g. 'A1:F1'."},"bold":{"type":"boolean"},"italic":{"type":"boolean"},"fontSize":{"type":"number","description":"Font size in points."},"fontColor":{"type":"string","description":"HTML hex color, e.g. '#FF0000'."},"backgroundColor":{"type":"string","description":"HTML hex fill color."},"borderStyle":{"type":"string","description":"Outside border: thin | medium | thick | none","enum":["thin","medium","thick","none"]},"insideBorder":{"type":"string","description":"Inside borders between cells: thin | medium | thick | none","enum":["thin","medium","thick","none"]},"alignment":{"type":"string","description":"Horizontal: left | center | right | general","enum":["left","center","right","general"]},"verticalAlignment":{"type":"string","description":"Vertical: top | center | bottom","enum":["top","center","bottom"]},"wrapText":{"type":"boolean","description":"Wrap long text within the cell."},"numberFormat":{"type":"string","description":"Excel number format string, e.g. '#,##0.00', '0%', 'dd/mm/yyyy'."},"locked":{"type":"boolean","description":"Lock cell (only effective when sheet is protected). Set false on data-entry cells before protect_sheet."}},"required":["filename","range"]}"""),

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

        new ToolFunctionDto(
            Name: "excel.summarize_range",
            Description: "Compute summary statistics (count, sum, min, max, average) per column in a range without reading all rows. Essential for analysing large datasets. The first row of the range is treated as column headers. Returns per-column stats.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range including header row, e.g. 'A1:E500'. First row is headers."},"columns":{"type":"array","description":"Optional: restrict to these column letters, e.g. ['C','D']. Defaults to all columns.","items":{"type":"string"}}},"required":["filename","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.add_data_validation",
            Description: "Add a validation rule to a range of cells. Use type='list' to create in-cell dropdown menus (e.g. department names, status codes, Yes/No). Use type='whole' or 'decimal' to restrict numeric input. Use type='date' to restrict date entry. Essential for data-entry forms.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Cells to apply validation to, e.g. 'B2:B100'."},"type":{"type":"string","enum":["list","whole","decimal","date","textLength"]},"values":{"type":"array","description":"For type=list: dropdown items, e.g. ['Approved','Pending','Rejected'].","items":{"type":"string"}},"listRange":{"type":"string","description":"For type=list: sheet range containing items instead of hard-coded values, e.g. 'Lists!$A$1:$A$5'."},"min":{"description":"For whole/decimal/date/textLength: minimum allowed value."},"max":{"description":"For whole/decimal/date/textLength: maximum allowed value."},"errorTitle":{"type":"string","description":"Title of the error pop-up on invalid entry."},"errorMessage":{"type":"string","description":"Body of the error pop-up."},"promptTitle":{"type":"string","description":"Input message title shown when cell is selected."},"promptMessage":{"type":"string","description":"Instruction shown when cell is selected (e.g. 'Select a department from the list')."}},"required":["filename","range","type"]}"""),

        new ToolFunctionDto(
            Name: "excel.add_conditional_format",
            Description: "Add conditional formatting to highlight cells automatically based on their values. Use formatType='highlight' to color cells matching a rule (e.g. sales > target = green, overdue = red, duplicates = orange). Use 'colorscale' for a heat-map gradient (red-yellow-green). Use 'databar' for in-cell bar chart. Ideal for dashboards and exception reports.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string","description":"Range to format, e.g. 'C2:C500'."},"formatType":{"type":"string","enum":["highlight","colorscale","databar"]},"condition":{"type":"string","description":"For highlight: comparison rule.","enum":["greaterThan","greaterThanOrEqual","lessThan","lessThanOrEqual","equalTo","between","notBetween","containsText","notContainsText","top","bottom","duplicates","unique"]},"value":{"description":"Threshold value (number or text). For top/bottom: the count N."},"value2":{"description":"Upper bound for 'between'/'notBetween'."},"backgroundColor":{"type":"string","description":"Hex fill color, e.g. '#C6EFCE' (light green), '#FFC7CE' (light red), '#FFEB9C' (light yellow)."},"fontColor":{"type":"string","description":"Hex font color, e.g. '#006100' (dark green), '#9C0006' (dark red)."},"bold":{"type":"boolean"},"minColor":{"type":"string","description":"For colorscale: lowest-value color, e.g. '#F8696B' (red)."},"midColor":{"type":"string","description":"For colorscale: midpoint color, e.g. '#FFEB84' (yellow). Omit for 2-color scale."},"maxColor":{"type":"string","description":"For colorscale: highest-value color, e.g. '#63BE7B' (green)."},"barColor":{"type":"string","description":"For databar: bar color, e.g. '#638EC6'."}},"required":["filename","range","formatType"]}"""),

        new ToolFunctionDto(
            Name: "excel.set_page_setup",
            Description: "Configure print settings for a sheet: orientation, paper size, fit-to-page, print area, repeat header rows on each page, grid lines, and centering. Call before the user prints or exports to PDF. A well-configured page setup is expected in professional reports.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"orientation":{"type":"string","enum":["portrait","landscape"]},"paperSize":{"type":"string","enum":["A4","A3","A5","Letter","Legal"]},"fitToPages":{"type":"object","description":"Fit sheet to N pages wide × M pages tall. Set tall=0 for unlimited height.","properties":{"wide":{"type":"integer"},"tall":{"type":"integer"}}},"scale":{"type":"integer","description":"Zoom percentage 10-400. Ignored when fitToPages is set."},"printArea":{"type":"string","description":"Range to print, e.g. 'A1:H50'."},"repeatHeaderRows":{"type":"integer","description":"Number of rows from top repeated on every printed page (e.g. 1 repeats the header row)."},"repeatHeaderCols":{"type":"integer","description":"Number of columns from left repeated on every page."},"showGridLines":{"type":"boolean","description":"Print grid lines. Default false."},"centerHorizontally":{"type":"boolean"},"centerVertically":{"type":"boolean"}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.add_named_range",
            Description: "Create a named range in the workbook so formulas can reference it by name (e.g. =SUM(Revenue) instead of =SUM(C2:C500)). Named ranges make formulas self-documenting and easier to maintain.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"name":{"type":"string","description":"Range name (no spaces), e.g. 'Revenue', 'TaxRate', 'StaffList'."},"range":{"type":"string","description":"Range address, optionally prefixed with sheet name, e.g. 'Data!$C$2:$C$500' or just 'A1:A50'."},"sheet":{"type":"string","description":"Sheet containing the range when range has no sheet prefix."}},"required":["filename","name","range"]}"""),

        new ToolFunctionDto(
            Name: "excel.get_named_ranges",
            Description: "List all named ranges defined in the workbook with their names and addresses.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.protect_sheet",
            Description: "Protect a sheet to prevent accidental edits to formulas and structure. Before protecting, use excel.format_range with locked=false on the data-entry cells so users can still type in those cells. Optionally allow sorting, filtering, or row insertion.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"password":{"type":"string","description":"Optional password. Omit for protection without password."},"allowSelectLocked":{"type":"boolean","description":"Allow selecting locked cells. Default true."},"allowSelectUnlocked":{"type":"boolean","description":"Allow selecting unlocked (data-entry) cells. Default true."},"allowSort":{"type":"boolean","description":"Allow sorting. Default false."},"allowFilter":{"type":"boolean","description":"Allow auto-filter. Default false."},"allowInsertRows":{"type":"boolean","description":"Allow inserting rows. Default false."},"allowDeleteRows":{"type":"boolean","description":"Allow deleting rows. Default false."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.unprotect_sheet",
            Description: "Remove protection from a sheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"password":{"type":"string","description":"Password used when protecting. Omit if no password was set."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "excel.copy_range",
            Description: "Copy a range of cells (values, formulas, and formatting) to another location within the same sheet or to a different sheet. Relative formula references are adjusted automatically for the new position.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sourceRange":{"type":"string","description":"Source range, e.g. 'A1:D10'."},"sourceSheet":{"type":"string","description":"Source sheet name. Defaults to first sheet."},"destCell":{"type":"string","description":"Top-left cell of the destination, e.g. 'F1'."},"destSheet":{"type":"string","description":"Destination sheet name. Defaults to same sheet as source."}},"required":["filename","sourceRange","destCell"]}"""),
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
                "excel.summarize_range"        => SummarizeRange(root, ctx),
                "excel.add_data_validation"    => AddDataValidation(root, ctx),
                "excel.add_conditional_format" => AddConditionalFormat(root, ctx),
                "excel.set_page_setup"         => SetPageSetup(root, ctx),
                "excel.add_named_range"        => AddNamedRange(root, ctx),
                "excel.get_named_ranges"       => GetNamedRanges(root, ctx),
                "excel.protect_sheet"          => ProtectSheet(root, ctx),
                "excel.unprotect_sheet"        => UnprotectSheet(root, ctx),
                "excel.copy_range"             => CopyRange(root, ctx),
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
        {
            var used = ws.RangeUsed();
            if (used is null)
                return ToolResult.Ok(JsonSerializer.Serialize(
                    new { firstRow = 1, firstColumn = "A", rows = Array.Empty<object?[]>() }, s_json));
            range = used;
        }

        int firstRow = range.FirstRow().RowNumber();
        int firstCol = range.FirstColumn().ColumnNumber();
        int lastRow  = range.LastRow().RowNumber();
        int lastCol  = range.LastColumn().ColumnNumber();

        // Iterate every cell in the declared range — not just "used" rows/cells.
        // This preserves correct row/column alignment even when rows are empty.
        var rows = new List<List<object?>>();
        for (int r = firstRow; r <= lastRow; r++)
        {
            var cells = new List<object?>();
            for (int c = firstCol; c <= lastCol; c++)
                cells.Add(GetCellValue(ws.Cell(r, c)));
            rows.Add(cells);
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            firstRow,
            firstColumn = XLHelper.GetColumnLetterFromNumber(firstCol),
            rows,
        }, s_json));
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
        if (root.TryGetProperty("wrapText", out var wt))
            range.Style.Alignment.WrapText = wt.GetBoolean();
        if (root.TryGetProperty("verticalAlignment", out var va) && va.GetString() is { } vaStr)
            range.Style.Alignment.Vertical = vaStr.ToLowerInvariant() switch
            {
                "top"    => XLAlignmentVerticalValues.Top,
                "center" => XLAlignmentVerticalValues.Center,
                _        => XLAlignmentVerticalValues.Bottom,
            };
        if (root.TryGetProperty("insideBorder", out var ib) && ib.GetString() is { } ibStr)
        {
            var insideStyle = ibStr.ToLowerInvariant() switch
            {
                "thin"   => XLBorderStyleValues.Thin,
                "medium" => XLBorderStyleValues.Medium,
                "thick"  => XLBorderStyleValues.Thick,
                _        => XLBorderStyleValues.None,
            };
            range.Style.Border.InsideBorder = insideStyle;
        }
        if (root.TryGetProperty("locked", out var lk))
            range.Style.Protection.Locked = lk.GetBoolean();

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

    // ── New operations (v2.21) ────────────────────────────────────────────────

    private static ToolResult SummarizeRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        using var wb = new XLWorkbook(path);
        var ws = GetSheet(wb, root);

        IXLRange range;
        if (root.TryGetProperty("range", out var rp) && rp.GetString() is { Length: > 0 } addr)
            range = ws.Range(addr);
        else
        {
            var used = ws.RangeUsed();
            if (used is null) return ToolResult.Ok("[]");
            range = used;
        }

        int firstRow = range.FirstRow().RowNumber();
        int firstCol = range.FirstColumn().ColumnNumber();
        int lastRow  = range.LastRow().RowNumber();
        int lastCol  = range.LastColumn().ColumnNumber();

        // Headers from first row
        var headers = new Dictionary<int, string>();
        for (int c = firstCol; c <= lastCol; c++)
            headers[c] = ws.Cell(firstRow, c).IsEmpty()
                ? XLHelper.GetColumnLetterFromNumber(c)
                : ws.Cell(firstRow, c).GetString();

        // Optional column filter
        HashSet<int>? filterCols = null;
        if (root.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
        {
            filterCols = new HashSet<int>();
            foreach (var col in colsEl.EnumerateArray())
                if (col.GetString() is { } letter)
                    filterCols.Add(XLHelper.GetColumnNumberFromLetter(letter));
        }

        var result = new List<Dictionary<string, object?>>();
        for (int c = firstCol; c <= lastCol; c++)
        {
            if (filterCols is not null && !filterCols.Contains(c)) continue;
            var nums = new List<double>();
            int textCnt = 0, emptyCnt = 0;
            for (int r = firstRow + 1; r <= lastRow; r++)
            {
                var cell = ws.Cell(r, c);
                if (cell.IsEmpty()) { emptyCnt++; continue; }
                if (cell.DataType == XLDataType.Number && !IsDateFormatted(cell))
                    nums.Add(cell.GetDouble());
                else if (cell.DataType == XLDataType.Boolean)
                    nums.Add(cell.GetBoolean() ? 1 : 0);
                else
                    textCnt++;
            }
            var col_result = new Dictionary<string, object?>
            {
                ["column"]     = XLHelper.GetColumnLetterFromNumber(c),
                ["header"]     = headers[c],
                ["totalRows"]  = lastRow - firstRow,
                ["emptyCount"] = emptyCnt,
            };
            if (nums.Count > 0)
            {
                col_result["count"]   = nums.Count;
                col_result["sum"]     = Math.Round(nums.Sum(), 6);
                col_result["min"]     = nums.Min();
                col_result["max"]     = nums.Max();
                col_result["average"] = Math.Round(nums.Average(), 6);
            }
            else
            {
                col_result["count"]    = textCnt;
                col_result["dataType"] = textCnt > 0 ? "text" : "empty";
            }
            result.Add(col_result);
        }
        return ToolResult.Ok(JsonSerializer.Serialize(result, s_json));
    }

    private static ToolResult AddDataValidation(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "list" : "list";
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var dv = ws.Range(rangeAddr).CreateDataValidation();
        dv.InCellDropdown = true;

        switch (type.ToLowerInvariant())
        {
            case "list":
                dv.AllowedValues = XLAllowedValues.List;
                if (root.TryGetProperty("listRange", out var lr) && lr.GetString() is { Length: > 0 } lrStr)
                {
                    dv.MinValue = lrStr;
                }
                else if (root.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                {
                    var items = vals.EnumerateArray()
                        .Select(v => v.GetString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToList();
                    // Excel list source: quoted comma-separated string
                    dv.MinValue = $"\"{string.Join(",", items)}\"";
                }
                break;
            case "whole":
                dv.AllowedValues = XLAllowedValues.WholeNumber;
                dv.Operator = XLOperator.Between;
                dv.MinValue = root.TryGetProperty("min", out var wMin) ? wMin.GetRawText() : "0";
                dv.MaxValue = root.TryGetProperty("max", out var wMax) ? wMax.GetRawText() : "9999999";
                break;
            case "decimal":
                dv.AllowedValues = XLAllowedValues.Decimal;
                dv.Operator = XLOperator.Between;
                dv.MinValue = root.TryGetProperty("min", out var dMin) ? dMin.GetRawText() : "0";
                dv.MaxValue = root.TryGetProperty("max", out var dMax) ? dMax.GetRawText() : "9999999";
                break;
            case "date":
                dv.AllowedValues = XLAllowedValues.Date;
                dv.Operator = XLOperator.Between;
                if (root.TryGetProperty("min", out var dtMin)) dv.MinValue = dtMin.GetString() ?? "";
                if (root.TryGetProperty("max", out var dtMax)) dv.MaxValue = dtMax.GetString() ?? "";
                break;
            case "textlength":
                dv.AllowedValues = XLAllowedValues.TextLength;
                dv.Operator = XLOperator.Between;
                dv.MinValue = root.TryGetProperty("min", out var tlMin) ? tlMin.GetRawText() : "0";
                dv.MaxValue = root.TryGetProperty("max", out var tlMax) ? tlMax.GetRawText() : "255";
                break;
        }

        if (root.TryGetProperty("errorTitle", out var et) && et.GetString() is { Length: > 0 } etStr)
            dv.ErrorTitle = etStr;
        if (root.TryGetProperty("errorMessage", out var em) && em.GetString() is { Length: > 0 } emStr)
        {
            dv.ShowErrorMessage = true;
            dv.ErrorMessage = emStr;
        }
        if (root.TryGetProperty("promptTitle", out var ptEl) && ptEl.GetString() is { Length: > 0 } ptStr)
            dv.InputTitle = ptStr;
        if (root.TryGetProperty("promptMessage", out var pmEl) && pmEl.GetString() is { Length: > 0 } pmStr)
        {
            dv.ShowInputMessage = true;
            dv.InputMessage = pmStr;
        }

        wb.SaveAs(path);
        return ToolResult.Ok($"Added {type} validation to '{rangeAddr}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult AddConditionalFormat(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var rangeAddr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        var formatType = root.TryGetProperty("formatType", out var ft) ? ft.GetString() ?? "highlight" : "highlight";
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var cf = ws.Range(rangeAddr).AddConditionalFormat();

        switch (formatType.ToLowerInvariant())
        {
            case "colorscale":
            {
                var minC = root.TryGetProperty("minColor", out var mc) && mc.GetString() is { } s1 ? s1 : "#F8696B";
                var maxC = root.TryGetProperty("maxColor", out var xc) && xc.GetString() is { } s3 ? s3 : "#63BE7B";
                if (root.TryGetProperty("midColor", out var mid) && mid.GetString() is { Length: > 0 } midC)
                {
                    var scale = cf.ColorScale();
                    scale.LowestValue(XLColor.FromHtml(minC))
                         .Midpoint(XLCFContentType.Percent, 50.0, XLColor.FromHtml(midC))
                         .HighestValue(XLColor.FromHtml(maxC));
                }
                else
                {
                    var scale = cf.ColorScale();
                    scale.LowestValue(XLColor.FromHtml(minC))
                         .HighestValue(XLColor.FromHtml(maxC));
                }
                break;
            }
            case "databar":
            {
                var barC = root.TryGetProperty("barColor", out var bc) && bc.GetString() is { } s ? s : "#638EC6";
                cf.DataBar(XLColor.FromHtml(barC), true);
                break;
            }
            default: // "highlight"
            {
                var cond = root.TryGetProperty("condition", out var cv) ? cv.GetString() ?? "greaterThan" : "greaterThan";
                var bgColor = root.TryGetProperty("backgroundColor", out var bg) && bg.GetString() is { Length: > 0 } bgs
                    ? XLColor.FromHtml(bgs) : (XLColor?)null;
                var fgColor = root.TryGetProperty("fontColor", out var fg) && fg.GetString() is { Length: > 0 } fgs
                    ? XLColor.FromHtml(fgs) : (XLColor?)null;
                bool bold = root.TryGetProperty("bold", out var boldEl) && boldEl.GetBoolean();
                double val  = root.TryGetProperty("value",  out var v)  && v.TryGetDouble(out var vd)   ? vd  : 0;
                double val2 = root.TryGetProperty("value2", out var v2) && v2.TryGetDouble(out var v2d) ? v2d : 0;
                string valS = root.TryGetProperty("value",  out var vs)
                    ? (vs.ValueKind == JsonValueKind.String ? vs.GetString()! : vs.GetDouble().ToString())
                    : "0";

                IXLStyle? cfc = cond.ToLowerInvariant() switch
                {
                    "greaterthan"        => cf.WhenGreaterThan(val),
                    "greaterthanorequal" => cf.WhenEqualOrGreaterThan(val),
                    "lessthan"           => cf.WhenLessThan(val),
                    "lessthanorequal"    => cf.WhenEqualOrLessThan(val),
                    "equalto"            => cf.WhenEquals(val),
                    "between"            => cf.WhenBetween(val, val2),
                    "notbetween"         => cf.WhenNotBetween(val, val2),
                    "containstext"       => cf.WhenContains(valS),
                    "notcontainstext"    => cf.WhenNotContains(valS),
                    "top"                => cf.WhenIsTop((int)val, XLTopBottomType.Items),
                    "bottom"             => cf.WhenIsBottom((int)val, XLTopBottomType.Items),
                    "duplicates"         => cf.WhenIsDuplicate(),
                    "unique"             => cf.WhenIsUnique(),
                    _                    => cf.WhenGreaterThan(val),
                };
                if (cfc is not null)
                {
                    if (bgColor is not null) cfc.Fill.SetBackgroundColor(bgColor);
                    if (fgColor is not null) cfc.Font.SetFontColor(fgColor);
                    if (bold) cfc.Font.Bold = true;
                }
                break;
            }
        }

        wb.SaveAs(path);
        return ToolResult.Ok($"Added '{formatType}' conditional format to '{rangeAddr}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult SetPageSetup(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        var ps = ws.PageSetup;

        if (root.TryGetProperty("orientation", out var ori) && ori.GetString() is { } oriStr)
            ps.PageOrientation = oriStr.ToLowerInvariant() == "landscape"
                ? XLPageOrientation.Landscape
                : XLPageOrientation.Portrait;

        if (root.TryGetProperty("paperSize", out var paper) && paper.GetString() is { } paperStr)
            ps.PaperSize = paperStr.ToUpperInvariant() switch
            {
                "A3"     => XLPaperSize.A3Paper,
                "A5"     => XLPaperSize.A5Paper,
                "LETTER" => XLPaperSize.LetterPaper,
                "LEGAL"  => XLPaperSize.LegalPaper,
                _        => XLPaperSize.A4Paper,
            };

        if (root.TryGetProperty("fitToPages", out var ftp) && ftp.ValueKind == JsonValueKind.Object)
        {
            int wide = ftp.TryGetProperty("wide", out var w) && w.TryGetInt32(out var wi) ? wi : 1;
            int tall = ftp.TryGetProperty("tall", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
            ps.FitToPages(wide, tall);
        }
        else if (root.TryGetProperty("scale", out var sc) && sc.TryGetInt32(out var scv))
        {
            ps.Scale = scv;
        }

        if (root.TryGetProperty("printArea", out var pa) && pa.GetString() is { Length: > 0 } paStr)
        {
            ps.PrintAreas.Clear();
            ps.PrintAreas.Add(paStr);
        }

        if (root.TryGetProperty("repeatHeaderRows", out var rhr) && rhr.TryGetInt32(out var rhrV) && rhrV > 0)
            ps.SetRowsToRepeatAtTop(1, rhrV);

        if (root.TryGetProperty("repeatHeaderCols", out var rhc) && rhc.TryGetInt32(out var rhcV) && rhcV > 0)
            ps.SetColumnsToRepeatAtLeft(1, rhcV);

        if (root.TryGetProperty("showGridLines", out var gl))
            ps.ShowGridlines = gl.GetBoolean();

        if (root.TryGetProperty("centerHorizontally", out var ch))
            ps.CenterHorizontally = ch.GetBoolean();

        if (root.TryGetProperty("centerVertically", out var cvert))
            ps.CenterVertically = cvert.GetBoolean();

        wb.SaveAs(path);
        return ToolResult.Ok($"Page setup configured for '{ws.Name}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult AddNamedRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var name = root.GetProperty("name").GetString()
            ?? throw new ArgumentException("name is required.");
        var rangeStr = root.GetProperty("range").GetString()
            ?? throw new ArgumentException("range is required.");
        using var wb = OpenOrCreate(path);

        string fullRange;
        if (rangeStr.Contains('!'))
        {
            fullRange = rangeStr;
        }
        else
        {
            var ws = GetSheet(wb, root, createIfMissing: false);
            fullRange = $"'{ws.Name}'!{rangeStr}";
        }

        // Remove existing named range with same name if present
        var existing = wb.NamedRanges.FirstOrDefault(nr =>
            string.Equals(nr.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) wb.NamedRanges.Delete(name);

        wb.NamedRanges.Add(name, fullRange);
        wb.SaveAs(path);
        return ToolResult.Ok($"Named range '{name}' → '{fullRange}' added to '{Path.GetFileName(path)}'.");
    }

    private static ToolResult GetNamedRanges(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        using var wb = new XLWorkbook(path);
        var result = wb.NamedRanges
            .Select(nr => new { name = nr.Name, refersTo = nr.RefersTo })
            .ToList();
        return ToolResult.Ok(JsonSerializer.Serialize(result, s_json));
    }

    private static ToolResult ProtectSheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);

        // Default: allow selecting both locked and unlocked cells
        bool asl  = !root.TryGetProperty("allowSelectLocked",   out var aslEl)  || aslEl.GetBoolean();
        bool asu  = !root.TryGetProperty("allowSelectUnlocked", out var asuEl)  || asuEl.GetBoolean();
        bool asrt = root.TryGetProperty("allowSort",            out var asrtEl) && asrtEl.GetBoolean();
        bool af   = root.TryGetProperty("allowFilter",          out var afEl)   && afEl.GetBoolean();
        bool air  = root.TryGetProperty("allowInsertRows",      out var airEl)  && airEl.GetBoolean();
        bool adr  = root.TryGetProperty("allowDeleteRows",      out var adrEl)  && adrEl.GetBoolean();

        var allowed = XLSheetProtectionElements.None;
        if (asl)  allowed |= XLSheetProtectionElements.SelectLockedCells;
        if (asu)  allowed |= XLSheetProtectionElements.SelectUnlockedCells;
        if (asrt) allowed |= XLSheetProtectionElements.Sort;
        if (af)   allowed |= XLSheetProtectionElements.AutoFilter;
        if (air)  allowed |= XLSheetProtectionElements.InsertRows;
        if (adr)  allowed |= XLSheetProtectionElements.DeleteRows;

        var password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        if (!string.IsNullOrEmpty(password))
            ws.Protect(password, XLProtectionAlgorithm.Algorithm.SimpleHash, allowed);
        else
            ws.Protect(allowed);

        wb.SaveAs(path);
        return ToolResult.Ok($"Sheet '{ws.Name}' is now protected in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult UnprotectSheet(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: false);
        var password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        ws.Unprotect(password ?? string.Empty);
        wb.SaveAs(path);
        return ToolResult.Ok($"Sheet '{ws.Name}' is now unprotected in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult CopyRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var sourceRangeAddr = root.GetProperty("sourceRange").GetString()
            ?? throw new ArgumentException("sourceRange is required.");
        var destCellAddr = root.GetProperty("destCell").GetString()
            ?? throw new ArgumentException("destCell is required.");
        using var wb = OpenOrCreate(path);

        IXLWorksheet srcWs;
        if (root.TryGetProperty("sourceSheet", out var ss) && ss.GetString() is { Length: > 0 } ssName)
        {
            if (!wb.TryGetWorksheet(ssName, out srcWs!))
                return ToolResult.Error($"Source sheet '{ssName}' not found.");
        }
        else srcWs = wb.Worksheets.First();

        IXLWorksheet dstWs;
        if (root.TryGetProperty("destSheet", out var ds) && ds.GetString() is { Length: > 0 } dsName)
        {
            if (!wb.TryGetWorksheet(dsName, out dstWs!))
                return ToolResult.Error($"Destination sheet '{dsName}' not found.");
        }
        else dstWs = srcWs;

        srcWs.Range(sourceRangeAddr).CopyTo(dstWs.Cell(destCellAddr));
        wb.SaveAs(path);
        return ToolResult.Ok($"Copied '{sourceRangeAddr}' → '{destCellAddr}' in '{Path.GetFileName(path)}'.");
    }

    // ── Value helpers ─────────────────────────────────────────────────────────

    private static object? GetCellValue(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        try
        {
            if (cell.HasFormula)
            {
                var cv = cell.CachedValue;
                if (cv.IsBlank) return null;
                if (cv.IsBoolean) return cv.GetBoolean();
                if (cv.IsError) return $"#{cv.GetError()}";
                if (cv.IsNumber)
                    return IsDateFormatted(cell) ? (object?)cell.GetDateTime().ToString("yyyy-MM-dd") : cv.GetNumber();
                return cv.GetText();
            }
            return cell.DataType switch
            {
                XLDataType.Boolean  => (object?)cell.GetBoolean(),
                XLDataType.Number   => IsDateFormatted(cell)
                                         ? cell.GetDateTime().ToString("yyyy-MM-dd")
                                         : (object?)cell.GetDouble(),
                XLDataType.Text     => cell.GetString(),
                XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd"),
                XLDataType.TimeSpan => cell.GetTimeSpan().ToString(@"hh\:mm\:ss"),
                XLDataType.Error    => "#ERROR",
                _                   => cell.GetString(),
            };
        }
        catch { return cell.GetString(); }
    }

    private static bool IsDateFormatted(IXLCell cell)
    {
        if (cell.DataType != XLDataType.Number) return false;
        var fmt = cell.Style.NumberFormat.Format;
        if (string.IsNullOrEmpty(fmt)) return false;
        // Excel date formats contain 'd' or 'y'; 'm' is ambiguous (months vs minutes)
        // so only treat 'm' as date when 'd' or 'y' also appear
        return fmt.IndexOfAny(['d', 'y']) >= 0;
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
