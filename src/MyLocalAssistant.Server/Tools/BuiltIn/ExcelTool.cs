using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MyLocalAssistant.Shared.Contracts;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

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
            Name: "excel.set_calculation_mode",
            Description: "Set the workbook calculation mode to auto, autoNoTable, or manual so workbook recalc behavior is explicit and repeatable.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"mode":{"type":"string","enum":["auto","autoNoTable","manual"],"description":"Workbook calculation mode."}},"required":["filename","mode"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.recalculate",
            Description: "Force recalculation of workbook formulas and save the refreshed cached results. Provide a sheet to recalculate only that worksheet.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Optional worksheet name to recalculate instead of the whole workbook."}},"required":["filename"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.evaluate_formula",
            Description: "Evaluate a workbook formula expression immediately using the workbook context. The formula may include or omit the leading '='.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"formula":{"type":"string","description":"Formula expression such as '=SUM(Data!B2:B10)' or 'SUM(1,2,3)'."}},"required":["filename","formula"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.write_range",
            Description: "Write a 2-D array of values and formulas to a workbook. Strings starting with '=' are written as Excel formulas. Examples of useful formulas: '=SUM(B2:B100)', '=AVERAGE(C:C)', '=COUNTA(A:A)-1', '=VLOOKUP(A2,$Data.$A:$C,3,FALSE)', '=SUMIF(B:B,\"Active\",D:D)', '=COUNTIF(C:C,\">\"+E1)', '=IF(D2>1000,\"High\",\"Low\")', '=IFERROR(VLOOKUP(A2,Sheet2!A:B,2,0),\"\")' , '=TEXT(TODAY(),\"dd/mm/yyyy\")'. Write headers first via the headers property for clean table structure.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Sheet name. Defaults to first sheet."},"startCell":{"type":"string","description":"Top-left cell address, e.g. 'A1'."},"values":{"type":"array","description":"Row-major 2-D array of cell values. Strings starting with '=' are treated as formulas.","items":{"type":"array","items":{}}},"headers":{"type":"array","description":"Optional header row written above 'values'.","items":{"type":"string"}}},"required":["filename","startCell","values"]}"""),

        new ToolFunctionDto(
            Name: "excel.preview_write_range",
            Description: "Preview how a write_range operation would affect a workbook without saving any changes. Useful for safe, commercial-grade editing workflows.",
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
            Name: "excel.read_named_range",
            Description: "Read the contents of a named range from an Excel template. Returns the range position metadata and a 2-D values array, just like excel.read_range.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"name":{"type":"string","description":"Workbook named range to read."}},"required":["filename","name"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.write_named_range",
            Description: "Write a scalar value, formula, or 2-D array into an existing named range. Prefer this over hard-coded cell addresses when filling customer-owned Excel templates.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"name":{"type":"string","description":"Workbook named range to fill."},"value":{"description":"Scalar value for single-cell named ranges."},"values":{"type":"array","description":"2-D row-major values array for a multi-cell named range.","items":{"type":"array","items":{}}},"formula":{"type":"string","description":"Excel formula without a leading '='. Only valid for a single-cell named range."},"numberFormat":{"type":"string","description":"Optional Excel number format applied to the written cell(s)."}},"required":["filename","name"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_image",
            Description: "Insert an image onto a worksheet at a cell anchor. Useful for logos, signatures, screenshots, and branded dashboard assets.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string","description":"Worksheet that will host the image. Defaults to the first sheet."},"imagePath":{"type":"string","description":"Relative or absolute image path resolved inside the work directory."},"topLeftCell":{"type":"string","description":"Top-left anchor cell, e.g. 'B2'."},"name":{"type":"string","description":"Optional picture name."},"widthPixels":{"type":"integer","description":"Optional width in pixels."},"heightPixels":{"type":"integer","description":"Optional height in pixels."},"xOffsetPixels":{"type":"integer","description":"Optional horizontal pixel offset from the anchor cell."},"yOffsetPixels":{"type":"integer","description":"Optional vertical pixel offset from the anchor cell."},"placement":{"type":"string","enum":["moveAndSize","move","freeFloating"],"description":"Excel picture placement behavior. Default 'moveAndSize'."}},"required":["filename","imagePath","topLeftCell"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_hyperlink",
            Description: "Add or replace a hyperlink on a worksheet cell. Supports external URLs and internal workbook targets.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"cell":{"type":"string","description":"Cell that will host the hyperlink, e.g. 'B3'."},"address":{"type":"string","description":"External URL such as 'https://...' or internal target such as 'Summary!A1'."},"text":{"type":"string","description":"Optional cell text. Defaults to the address when the cell is empty."},"tooltip":{"type":"string","description":"Optional screen tip."}},"required":["filename","cell","address"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_comment",
            Description: "Add or replace a cell comment for review notes, instructions, or data-entry guidance.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"cell":{"type":"string","description":"Target cell, e.g. 'C5'."},"text":{"type":"string","description":"Comment text."},"author":{"type":"string","description":"Optional author name."},"visible":{"type":"boolean","description":"Whether the comment should be visible by default."}},"required":["filename","cell","text"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_text_box",
            Description: "Add a worksheet text box using the drawing layer. Useful for callouts, instructions, and dashboard annotations.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"topLeftCell":{"type":"string","description":"Top-left anchor cell, e.g. 'F2'."},"widthColumns":{"type":"integer","description":"Approximate width in worksheet columns. Default 4."},"heightRows":{"type":"integer","description":"Approximate height in worksheet rows. Default 3."},"name":{"type":"string","description":"Optional text box name."},"text":{"type":"string","description":"Text box content."},"fillColor":{"type":"string","description":"Optional hex fill color."},"fontColor":{"type":"string","description":"Optional hex font color."},"borderColor":{"type":"string","description":"Optional hex border color."},"bold":{"type":"boolean","description":"Bold text. Default false."},"fontSize":{"type":"integer","description":"Font size in half-points. Default 1200 (12pt)."}},"required":["filename","topLeftCell","text"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_shape",
            Description: "Add a simple worksheet shape using the drawing layer. Supports rectangle, ellipse, and line shapes with optional text.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"topLeftCell":{"type":"string","description":"Top-left anchor cell, e.g. 'J4'."},"widthColumns":{"type":"integer","description":"Approximate width in worksheet columns. Default 4."},"heightRows":{"type":"integer","description":"Approximate height in worksheet rows. Default 3."},"shapeType":{"type":"string","enum":["rectangle","ellipse","line"],"description":"Shape preset. Default 'rectangle'."},"name":{"type":"string","description":"Optional shape name."},"text":{"type":"string","description":"Optional text rendered inside the shape."},"fillColor":{"type":"string","description":"Optional hex fill color."},"fontColor":{"type":"string","description":"Optional hex font color."},"lineColor":{"type":"string","description":"Optional hex outline color."},"bold":{"type":"boolean","description":"Bold text. Default false."},"fontSize":{"type":"integer","description":"Font size in half-points. Default 1200 (12pt)."}},"required":["filename","topLeftCell"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.add_chart",
            Description: "Insert a native Excel chart object onto a worksheet using existing workbook ranges for categories and series values.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"targetSheet":{"type":"string","description":"Worksheet that will host the chart object."},"dataSheet":{"type":"string","description":"Fallback sheet used when categoryRange or series ranges omit a sheet prefix."},"chartType":{"type":"string","enum":["column","bar","line","pie","stackedColumn","stackedBar","area","doughnut","scatter","combo"],"description":"Chart type. Default 'column'."},"title":{"type":"string","description":"Optional chart title."},"categoryRange":{"type":"string","description":"A1 range for categories, e.g. 'Data!$A$2:$A$6'. Required for all chart types except scatter."},"categoryAxisTitle":{"type":"string","description":"Optional category/X-axis title."},"valueAxisTitle":{"type":"string","description":"Optional primary value/Y-axis title."},"secondaryValueAxisTitle":{"type":"string","description":"Optional secondary value-axis title for combo charts."},"showLegend":{"type":"boolean","description":"Show legend. Default true."},"legendPosition":{"type":"string","enum":["right","left","top","bottom"],"description":"Legend position. Default 'right'."},"showDataLabels":{"type":"boolean","description":"Show data labels on the plotted series."},"series":{"type":"array","description":"Series definitions.","items":{"type":"object","properties":{"name":{"type":"string","description":"Literal series name."},"nameCell":{"type":"string","description":"Single-cell reference holding the series name."},"valuesRange":{"type":"string","description":"A1 range for series values."},"xValuesRange":{"type":"string","description":"Scatter X-values range. Required per series for scatter unless categoryRange is provided."},"chartType":{"type":"string","enum":["column","line"],"description":"Series chart type override used by combo charts."},"secondaryAxis":{"type":"boolean","description":"Plot this series on the secondary axis in combo charts."},"color":{"type":"string","description":"Optional series hex color."}},"required":["valuesRange"]}},"topLeftCell":{"type":"string","description":"Top-left anchor cell for the chart, e.g. 'H2'."},"widthColumns":{"type":"integer","description":"Approximate chart width in worksheet columns. Default 8."},"heightRows":{"type":"integer","description":"Approximate chart height in worksheet rows. Default 16."}},"required":["filename","targetSheet","series","topLeftCell"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.create_pivot_report",
            Description: "Create a pivot-style summary sheet from a source range with row fields, optional column grouping, and one or more aggregate measures.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sourceSheet":{"type":"string","description":"Worksheet containing the source data. Defaults to the first sheet."},"sourceRange":{"type":"string","description":"Range including the header row, e.g. 'A1:F200'."},"reportSheet":{"type":"string","description":"Worksheet name to create or replace with the summary output."},"rowFields":{"type":"array","description":"Header names or worksheet column letters used as row groups.","items":{"type":"string"}},"columnField":{"type":"string","description":"Optional header name or worksheet column letter used to spread values across columns."},"values":{"type":"array","description":"Aggregate definitions.","items":{"type":"object","properties":{"field":{"type":"string","description":"Header name or worksheet column letter to aggregate."},"summary":{"type":"string","enum":["sum","count","average","min","max"],"description":"Aggregate function. Default 'sum'."},"label":{"type":"string","description":"Optional header label for the output metric."},"numberFormat":{"type":"string","description":"Optional Excel number format applied to the output values."}},"required":["field"]}},"includeGrandTotal":{"type":"boolean","description":"Append grand totals. Default true."}},"required":["filename","sourceRange","reportSheet","rowFields","values"],"additionalProperties":false}"""),

        new ToolFunctionDto(
            Name: "excel.create_pivot_table",
            Description: "Create a native Excel PivotTable backed by a refreshable pivot cache. Supports row, column, and report-filter fields plus aggregate value definitions.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sourceSheet":{"type":"string","description":"Worksheet containing the source data. Defaults to the first sheet."},"sourceRange":{"type":"string","description":"Range including the header row, e.g. 'A1:F500'."},"targetSheet":{"type":"string","description":"Worksheet that will host the PivotTable. Created if missing."},"targetCell":{"type":"string","description":"Top-left destination cell for the PivotTable, e.g. 'A3'."},"name":{"type":"string","description":"Optional PivotTable name. Auto-generated if omitted."},"title":{"type":"string","description":"Optional PivotTable title."},"rowFields":{"type":"array","items":{"type":"string"},"description":"Fields shown on the row axis."},"columnFields":{"type":"array","items":{"type":"string"},"description":"Optional fields shown on the column axis."},"filterFields":{"type":"array","items":{"type":"string"},"description":"Optional report filter fields."},"values":{"type":"array","description":"Aggregate value definitions.","items":{"type":"object","properties":{"field":{"type":"string"},"summary":{"type":"string","enum":["sum","count","average","min","max"],"description":"Aggregate function. Default 'sum'."},"label":{"type":"string","description":"Optional custom caption for the value field."},"numberFormat":{"type":"string","description":"Optional number format for the value field."}},"required":["field"]}},"refreshOnOpen":{"type":"boolean","description":"Refresh pivot cache when Excel opens the workbook. Default true."},"saveSourceData":{"type":"boolean","description":"Persist source data in the pivot cache. Default true."},"repeatRowLabels":{"type":"boolean","description":"Repeat row labels in tabular layout. Default true."},"classicLayout":{"type":"boolean","description":"Use classic tabular layout. Default true."},"autofitColumns":{"type":"boolean","description":"Auto-fit columns when Excel opens the workbook. Default true."},"showGrandTotalsRows":{"type":"boolean","description":"Show grand totals for rows. Default true."},"showGrandTotalsColumns":{"type":"boolean","description":"Show grand totals for columns. Default true."}},"required":["filename","sourceRange","targetSheet","targetCell","rowFields","values"],"additionalProperties":false}"""),

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
        new ToolFunctionDto(
            Name: "excel.stream_filter_to_csv",
            Description: "Stream a worksheet row-by-row and write a filtered CSV without loading the full workbook into memory.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"sheet":{"type":"string"},"filterColumnName":{"type":"string"},"excludeValue":{"type":"string"},"outputFilename":{"type":"string"}},"required":["filename","filterColumnName","excludeValue"]}"""),
        new ToolFunctionDto(
            Name: "excel.repair_openxml",
            Description: "Attempt a safe OpenXML repair by converting shared-string references to inline strings and removing the SharedStringTable. Produces a repaired copy and returns repairedFilename.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"outputFilename":{"type":"string"}},"required":["filename"]}"""),
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
                "excel.set_calculation_mode" => SetCalculationMode(root, ctx),
                "excel.recalculate"     => Recalculate(root, ctx),
                "excel.evaluate_formula" => EvaluateFormula(root, ctx),
                "excel.preview_write_range" => PreviewWriteRange(root, ctx),
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
                "excel.read_named_range"       => ReadNamedRange(root, ctx),
                "excel.write_named_range"      => WriteNamedRange(root, ctx),
                "excel.add_image"             => AddImage(root, ctx),
                "excel.add_hyperlink"         => AddHyperlink(root, ctx),
                "excel.add_comment"           => AddComment(root, ctx),
                "excel.add_text_box"          => AddTextBox(root, ctx),
                "excel.add_shape"             => AddShape(root, ctx),
                "excel.add_chart"             => AddChart(root, ctx),
                "excel.create_pivot_report"   => CreatePivotReport(root, ctx),
                "excel.create_pivot_table"    => CreatePivotTable(root, ctx),
                "excel.protect_sheet"          => ProtectSheet(root, ctx),
                "excel.unprotect_sheet"        => UnprotectSheet(root, ctx),
                "excel.copy_range"             => CopyRange(root, ctx),
                "excel.stream_filter_to_csv"   => StreamFilterToCsv(root, ctx),
                "excel.repair_openxml"         => RepairOpenXml(root, ctx),
                _                       => ToolResult.Error($"Unknown tool: {call.ToolName}"),
            };
        }
        catch (Exception ex)
        {
            // Return full exception (includes inner exceptions and stack trace) so testers/devs can see root cause
            return ToolResult.Error($"{call.ToolName} failed: {ex}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveFile(JsonElement root, ToolContext ctx)
    {
        var name = root.GetProperty("filename").GetString()
            ?? throw new ArgumentException("filename is required.");
        return OfficeToolSupport.ResolveWorkFile(ctx.WorkDirectory, name, ".xlsx");
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

    private static ToolResult StreamFilterToCsv(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {Path.GetFileName(path)}");

        var filterColumnName = root.GetProperty("filterColumnName").GetString() ?? throw new ArgumentException("filterColumnName is required.");
        var excludeValue = root.GetProperty("excludeValue").GetString() ?? string.Empty;
        var sheetName = root.TryGetProperty("sheet", out var sEl) && sEl.GetString() is { Length: > 0 } s ? s : null;

        var outName = root.TryGetProperty("outputFilename", out var of) && of.GetString() is { Length: > 0 } ofn
            ? ofn
            : Path.GetFileNameWithoutExtension(path) + "_filtered.csv";
        var outPath = OfficeToolSupport.ResolveWorkFile(ctx.WorkDirectory, outName, ".csv");

        try
        {
            using var doc = SpreadsheetDocument.Open(path, false);
            var workbookPart = doc.WorkbookPart ?? throw new InvalidOperationException("WorkbookPart missing");
            var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
            Sheet sheet;
            if (!string.IsNullOrEmpty(sheetName))
                sheet = sheets.FirstOrDefault(sh => string.Equals(sh.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase)) ?? sheets.FirstOrDefault();
            else
                sheet = sheets.FirstOrDefault();
            if (sheet is null) return ToolResult.Error("No sheets found in workbook.");

            var wsPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
            var sst = workbookPart.SharedStringTablePart?.SharedStringTable;

            using var writer = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);

            // Read rows with OpenXmlReader to avoid loading full sheet
            using var reader = OpenXmlReader.Create(wsPart);
            bool headerProcessed = false;
            int filterColIndex = -1; // 1-based

            while (reader.Read())
            {
                if (reader.ElementType == typeof(Row))
                {
                    var row = (Row)reader.LoadCurrentElement();
                    var cells = row.Elements<Cell>().ToList();
                    // Build list of strings by column index
                    var maxCol = cells.Count == 0 ? 0 : cells.Max(c => GetColumnIndexFromName(GetColumnName(c.CellReference?.Value ?? string.Empty)));
                    var values = new string[maxCol + 1];
                    foreach (var c in cells)
                    {
                        var colName = GetColumnName(c.CellReference?.Value ?? string.Empty);
                        var idx = GetColumnIndexFromName(colName);
                        values[idx] = GetCellText(c, sst);
                    }

                    if (!headerProcessed)
                    {
                        // find filter column index
                        for (int i = 1; i < values.Length; i++)
                        {
                            if (string.Equals(values[i]?.Trim(), filterColumnName, StringComparison.OrdinalIgnoreCase))
                            {
                                filterColIndex = i; break;
                            }
                        }
                        headerProcessed = true;
                        // write header line
                        writer.WriteLine(string.Join(',', values.Skip(1).Select(EscapeCsv)));
                        continue;
                    }

                    // If filter column missing, treat as keep
                    var cellVal = filterColIndex > 0 && filterColIndex < values.Length ? values[filterColIndex] : null;
                    if (string.Equals(cellVal ?? string.Empty, excludeValue, StringComparison.Ordinal))
                        continue; // skip row

                    writer.WriteLine(string.Join(',', values.Skip(1).Select(EscapeCsv)));
                }
            }

            return ToolResult.Ok(JsonSerializer.Serialize(new { output = Path.GetFileName(outPath) }, s_json));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"StreamFilter failed: {ex}");
        }
    }

    private static string GetCellText(Cell cell, SharedStringTable? sst)
    {
        if (cell == null) return string.Empty;
        var value = cell.InnerText ?? string.Empty;
        if (cell.DataType != null && cell.DataType == CellValues.SharedString && sst != null)
        {
            if (int.TryParse(value, out var idx) && idx >= 0 && idx < sst.Count())
            {
                return sst.ElementAt(idx).InnerText ?? string.Empty;
            }
            return value;
        }
        return value;
    }

    private static string GetColumnName(string cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(cellRef, "[A-Za-z]+");
        return match.Success ? match.Value : string.Empty;
    }

    private static int GetColumnIndexFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        int sum = 0;
        foreach (char c in name.ToUpperInvariant())
            sum = sum * 26 + (c - 'A' + 1);
        return sum;
    }

    private static string EscapeCsv(string? s)
        => s is null ? "" : (s.Contains(',') || s.Contains('"') || s.Contains('\n') ? '"' + s.Replace("\"", "\"\"") + '"' : s);

    private static ToolResult RepairOpenXml(JsonElement root, ToolContext ctx)
    {
        var originalPath = ResolveFile(root, ctx);
        if (!File.Exists(originalPath)) return ToolResult.Error($"File not found: {Path.GetFileName(originalPath)}");

        var outName = root.TryGetProperty("outputFilename", out var of) && of.GetString() is { Length: > 0 } ofn
            ? ofn
            : Path.GetFileNameWithoutExtension(originalPath) + "_repaired.xlsx";
        var outPath = OfficeToolSupport.ResolveWorkFile(ctx.WorkDirectory, outName, ".xlsx");

        // ensure unique output path
        var baseOut = Path.Combine(Path.GetDirectoryName(outPath) ?? ctx.WorkDirectory, Path.GetFileNameWithoutExtension(outPath));
        var ext = Path.GetExtension(outPath);
        var candidate = outPath;
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = baseOut + "-" + suffix + ext;
            suffix++;
        }
        outPath = candidate;

        // copy original to outPath and repair in-place on the copy
        File.Copy(originalPath, outPath);

        try
        {
            using var doc = SpreadsheetDocument.Open(outPath, true);
            var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("WorkbookPart missing");
            var sstPart = wbPart.SharedStringTablePart;
            var sst = sstPart?.SharedStringTable;

            foreach (var wsPart in wbPart.WorksheetParts)
            {
                var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
                if (sheetData == null) continue;
                foreach (var row in sheetData.Elements<Row>())
                {
                    foreach (var cell in row.Elements<Cell>().ToList())
                    {
                        if (cell.DataType != null && cell.DataType == CellValues.SharedString)
                        {
                            string text = string.Empty;
                            if (int.TryParse(cell.CellValue?.Text, out var sidx) && sst != null)
                            {
                                if (sidx >= 0 && sidx < sst.Count())
                                    text = sst.ElementAt(sidx).InnerText ?? string.Empty;
                            }
                            if (string.IsNullOrEmpty(text)) text = cell.InnerText ?? string.Empty;

                            cell.CellValue = null;
                            cell.DataType = null;
                            cell.InlineString = new InlineString(new Text(text));
                        }
                    }
                }
                wsPart.Worksheet.Save();
            }

            // remove shared string table part if present
            if (sstPart != null)
                wbPart.DeletePart(sstPart);

            wbPart.Workbook.Save();
            doc.Close();

            return ToolResult.Ok(JsonSerializer.Serialize(new { repairedFilename = Path.GetFileName(outPath) }, s_json));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Repair failed: {ex}");
        }
    }

    private static XLWorkbook OpenOrCreate(string path)
    {
        try
        {
            return File.Exists(path) ? new XLWorkbook(path) : new XLWorkbook();
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to open or create workbook '{Path.GetFileName(path)}': {ex}", ex);
        }
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

    private static ToolResult SetCalculationMode(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var mode = root.GetProperty("mode").GetString()
            ?? throw new ArgumentException("mode is required.");
        using var wb = OpenOrCreate(path);
        wb.CalculateMode = ParseCalculationMode(mode);
        wb.SaveAs(path);
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(path),
            calculationMode = wb.CalculateMode.ToString(),
        }, s_json));
    }

    private static ToolResult Recalculate(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");

        using var wb = new XLWorkbook(path);
        string scope;
        if (root.TryGetProperty("sheet", out var sheetEl) && sheetEl.GetString() is { Length: > 0 } sheetName)
        {
            var ws = GetSheet(wb, root, createIfMissing: false);
            ws.RecalculateAllFormulas();
            scope = ws.Name;
        }
        else
        {
            wb.RecalculateAllFormulas();
            scope = "workbook";
        }

        wb.SaveAs(path, false, true);
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(path),
            recalculated = scope,
            calculationMode = wb.CalculateMode.ToString(),
        }, s_json));
    }

    private static ToolResult EvaluateFormula(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        var formula = root.GetProperty("formula").GetString()
            ?? throw new ArgumentException("formula is required.");

        using var wb = new XLWorkbook(path);
        var normalized = formula.StartsWith('=') ? formula : "=" + formula;
        var sheetName = "__mla_eval__";
        while (wb.TryGetWorksheet(sheetName, out _))
            sheetName += "_";

        var scratch = wb.AddWorksheet(sheetName);
        var value = scratch.Evaluate(normalized, "A1");
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            formula = normalized,
            value = ConvertCellValue(value),
        }, s_json));
    }

    private static ToolResult PreviewWriteRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {Path.GetFileName(path)}");

        var startCell = root.GetProperty("startCell").GetString()
            ?? throw new ArgumentException("startCell is required.");

        using var wb = new XLWorkbook(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var startAddr = ws.Cell(startCell).Address;
        int row = startAddr.RowNumber;
        int col = startAddr.ColumnNumber;
        int cellsWritten = 0;

        if (root.TryGetProperty("headers", out var hdrsEl) && hdrsEl.ValueKind == JsonValueKind.Array)
        {
            int c = col;
            foreach (var h in hdrsEl.EnumerateArray())
            {
                cellsWritten++;
                c++;
            }
            row++;
        }

        var valuesEl = root.GetProperty("values");
        foreach (var rowEl in valuesEl.EnumerateArray())
        {
            foreach (var cellEl in rowEl.EnumerateArray())
            {
                cellsWritten++;
            }
            row++;
        }

        return ToolResult.Success(JsonSerializer.Serialize(new
        {
            preview = true,
            previewOnly = true,
            filename = Path.GetFileName(path),
            sheet = ws.Name,
            startCell,
            cellsWritten,
            rows = valuesEl.GetArrayLength(),
        }, s_json), "Preview complete", new { filename = Path.GetFileName(path), sheet = ws.Name, startCell });
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

        var preview = root.TryGetProperty("preview", out var previewEl) && previewEl.ValueKind == JsonValueKind.True;
        if (preview)
        {
            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {Path.GetFileName(path)}");

            using var wb = new XLWorkbook(path);
            var ws = GetSheet(wb, root, createIfMissing: true);
            var cell = ws.Cell(cellAddr);
            var originalValue = cell.GetString();

            if (root.TryGetProperty("formula", out var formulaEl) && formulaEl.GetString() is { Length: > 0 } formula)
            {
                return ToolResult.Success(JsonSerializer.Serialize(new
                {
                    preview = true,
                    previewOnly = true,
                    filename = Path.GetFileName(path),
                    sheet = ws.Name,
                    cell = cellAddr,
                    action = "formula",
                    formula,
                    previousValue = originalValue,
                }, s_json), "Preview complete", new { filename = Path.GetFileName(path), sheet = ws.Name, cell = cellAddr });
            }

            if (root.TryGetProperty("value", out var valueEl))
            {
                var previewValue = valueEl.ValueKind == JsonValueKind.Null
                    ? "<clear>"
                    : valueEl.ToString();

                return ToolResult.Success(JsonSerializer.Serialize(new
                {
                    preview = true,
                    previewOnly = true,
                    filename = Path.GetFileName(path),
                    sheet = ws.Name,
                    cell = cellAddr,
                    action = "value",
                    value = previewValue,
                    previousValue = originalValue,
                }, s_json), "Preview complete", new { filename = Path.GetFileName(path), sheet = ws.Name, cell = cellAddr });
            }

            return ToolResult.Success(JsonSerializer.Serialize(new
            {
                preview = true,
                previewOnly = true,
                filename = Path.GetFileName(path),
                sheet = ws.Name,
                cell = cellAddr,
                action = "noop",
                previousValue = originalValue,
            }, s_json), "Preview complete", new { filename = Path.GetFileName(path), sheet = ws.Name, cell = cellAddr });
        }

        using var workbook = OpenOrCreate(path);
        var ws2 = GetSheet(workbook, root, createIfMissing: true);
        var cell2 = ws2.Cell(cellAddr);

        if (root.TryGetProperty("formula", out var formulaEl2) && formulaEl2.GetString() is { Length: > 0 } formula2)
        {
            cell2.FormulaA1 = formula2;
        }
        else if (root.TryGetProperty("value", out var valueEl2))
        {
            if (valueEl2.ValueKind == JsonValueKind.Null)
                cell2.Value = Blank.Value;
            else
                SetCellValue(cell2, valueEl2);
        }

        if (root.TryGetProperty("numberFormat", out var nfEl) && nfEl.GetString() is { Length: > 0 } nf)
            cell2.Style.NumberFormat.Format = nf;

        workbook.SaveAs(path);
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

    private static ToolResult ReadNamedRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {Path.GetFileName(path)}");
        var name = root.GetProperty("name").GetString()
            ?? throw new ArgumentException("name is required.");
        using var wb = new XLWorkbook(path);

        var range = ResolveNamedRange(wb, name);
        int firstRow = range.FirstCell().Address.RowNumber;
        int firstCol = range.FirstCell().Address.ColumnNumber;
        int lastRow = range.LastCell().Address.RowNumber;
        int lastCol = range.LastCell().Address.ColumnNumber;

        var rows = new List<List<object?>>();
        for (int r = firstRow; r <= lastRow; r++)
        {
            var cells = new List<object?>();
            for (int c = firstCol; c <= lastCol; c++)
                cells.Add(GetCellValue(range.Worksheet.Cell(r, c)));
            rows.Add(cells);
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            name,
            firstRow,
            firstColumn = XLHelper.GetColumnLetterFromNumber(firstCol),
            rows,
        }, s_json));
    }

    private static ToolResult WriteNamedRange(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var name = root.GetProperty("name").GetString()
            ?? throw new ArgumentException("name is required.");
        using var wb = OpenOrCreate(path);

        var range = ResolveNamedRange(wb, name);
        var targetCell = range.FirstCell();

        if (root.TryGetProperty("formula", out var formulaEl) && formulaEl.GetString() is { Length: > 0 } formula)
        {
            if (range.RowCount() != 1 || range.ColumnCount() != 1)
                return ToolResult.Error("formula is only supported for a single-cell named range.");
            targetCell.FormulaA1 = formula;
        }
        else if (root.TryGetProperty("values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array)
        {
            var rows = valuesEl.EnumerateArray().ToList();
            int inputRowCount = rows.Count;
            int inputColCount = rows.Count == 0 ? 0 : rows.Max(r => r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0);
            if (inputRowCount == 0 || inputColCount == 0)
                return ToolResult.Error("values must contain at least one row and one column.");
            if (inputRowCount > range.RowCount() || inputColCount > range.ColumnCount())
                return ToolResult.Error($"values ({inputRowCount}x{inputColCount}) do not fit inside named range '{name}' ({range.RowCount()}x{range.ColumnCount()}).");

            for (int r = 0; r < rows.Count; r++)
            {
                var rowEl = rows[r];
                if (rowEl.ValueKind != JsonValueKind.Array)
                    return ToolResult.Error("values must be a 2-D array.");

                int c = 0;
                foreach (var cellEl in rowEl.EnumerateArray())
                {
                    var cell = range.Worksheet.Cell(targetCell.Address.RowNumber + r, targetCell.Address.ColumnNumber + c);
                    if (cellEl.ValueKind == JsonValueKind.String && cellEl.GetString() is { } formulaText && formulaText.StartsWith('='))
                        cell.FormulaA1 = formulaText[1..];
                    else
                        SetCellValue(cell, cellEl);
                    c++;
                }
            }
        }
        else if (root.TryGetProperty("value", out var valueEl))
        {
            SetCellValue(targetCell, valueEl);
        }
        else
        {
            return ToolResult.Error("Provide one of 'value', 'values', or 'formula'.");
        }

        if (root.TryGetProperty("numberFormat", out var nfEl) && nfEl.GetString() is { Length: > 0 } nf)
            range.Style.NumberFormat.Format = nf;

        wb.SaveAs(path);
        return ToolResult.Ok($"Wrote named range '{name}' in '{Path.GetFileName(path)}'.");
    }

    private static ToolResult AddImage(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var imagePathArg = root.GetProperty("imagePath").GetString()
            ?? throw new ArgumentException("imagePath is required.");
        var topLeftCell = root.GetProperty("topLeftCell").GetString()
            ?? throw new ArgumentException("topLeftCell is required.");
        var resolvedImagePath = OfficeToolSupport.ResolveExistingWorkAsset(ctx.WorkDirectory, imagePathArg);
        var sheetName = root.TryGetProperty("sheet", out var sheetEl) && sheetEl.GetString() is { Length: > 0 } providedSheetName
            ? providedSheetName
            : "Sheet1";
        var pictureName = root.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } providedName
            ? providedName
            : Path.GetFileNameWithoutExtension(resolvedImagePath);
        var xOffset = root.TryGetProperty("xOffsetPixels", out var xOffsetEl) && xOffsetEl.TryGetInt32(out var parsedXOffset) ? parsedXOffset : 0;
        var yOffset = root.TryGetProperty("yOffsetPixels", out var yOffsetEl) && yOffsetEl.TryGetInt32(out var parsedYOffset) ? parsedYOffset : 0;
        var widthColumns = root.TryGetProperty("widthPixels", out var widthEl) && widthEl.TryGetInt32(out var widthPx) && widthPx > 0
            ? Math.Max(1, (int)Math.Ceiling(widthPx / 64d))
            : 2;
        var heightRows = root.TryGetProperty("heightPixels", out var heightEl) && heightEl.TryGetInt32(out var heightPx) && heightPx > 0
            ? Math.Max(1, (int)Math.Ceiling(heightPx / 20d))
            : 2;

        if (!OfficeToolSupport.TryGetImagePartType(resolvedImagePath, out var imagePartType))
            return ToolResult.Error($"Unsupported image format for '{Path.GetFileName(resolvedImagePath)}'.");

        using (var wb = OpenOrCreate(path))
        {
            GetSheetByNameOrCreate(wb, sheetName);
            wb.SaveAs(path);
        }

        using (var workbook = SpreadsheetDocument.Open(path, true))
        {
            var workbookPart = workbook.WorkbookPart ?? throw new InvalidOperationException("Workbook is missing a workbook part.");
            var worksheetPart = GetWorksheetPart(workbookPart, sheetName);
            if (worksheetPart is null)
                return ToolResult.Error($"Sheet '{sheetName}' not found.");

            var drawingsPart = EnsureWorksheetDrawingPart(worksheetPart);
            var imagePart = drawingsPart.AddImagePart(imagePartType);
            using (var imageStream = File.OpenRead(resolvedImagePath))
                imagePart.FeedData(imageStream);

            var worksheetDrawing = drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
            var nextId = GetNextWorksheetObjectId(worksheetDrawing);
            var picture = CreateWorksheetPicture(nextId, pictureName, drawingsPart.GetIdOfPart(imagePart));
            worksheetDrawing.Append(BuildWorksheetPictureAnchor(picture, topLeftCell, widthColumns, heightRows, xOffset, yOffset));
            worksheetDrawing.Save();
            worksheetPart.Worksheet.Save();
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(path),
            sheet = sheetName,
            imagePath = OfficeToolSupport.ToRelativeDisplayPath(ctx.WorkDirectory, resolvedImagePath),
            topLeftCell,
        }, s_json));
    }

    private static ToolResult AddHyperlink(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var cellAddress = root.GetProperty("cell").GetString()
            ?? throw new ArgumentException("cell is required.");
        var address = root.GetProperty("address").GetString()
            ?? throw new ArgumentException("address is required.");

        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var cell = ws.Cell(cellAddress);
        var hyperlink = cell.CreateHyperlink();

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Scheme)
            && uri.Scheme is not "sheet")
        {
            hyperlink.ExternalAddress = uri;
        }
        else
        {
            hyperlink.InternalAddress = address.TrimStart('#');
        }

        if (root.TryGetProperty("tooltip", out var tooltipEl) && tooltipEl.GetString() is { Length: > 0 } tooltip)
            hyperlink.Tooltip = tooltip;

        if (root.TryGetProperty("text", out var textEl) && textEl.GetString() is { } text)
            cell.Value = text;
        else if (cell.IsEmpty())
            cell.Value = address;

        cell.SetHyperlink(hyperlink);
        wb.SaveAs(path);
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(path),
            sheet = ws.Name,
            cell = cellAddress,
            address,
        }, s_json));
    }

    private static ToolResult AddComment(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var cellAddress = root.GetProperty("cell").GetString()
            ?? throw new ArgumentException("cell is required.");
        var text = root.GetProperty("text").GetString()
            ?? throw new ArgumentException("text is required.");

        using var wb = OpenOrCreate(path);
        var ws = GetSheet(wb, root, createIfMissing: true);
        var comment = ws.Cell(cellAddress).GetComment();
        comment.ClearText();
        comment.AddText(text);

        if (root.TryGetProperty("author", out var authorEl) && authorEl.GetString() is { Length: > 0 } author)
            comment.SetAuthor(author);
        if (root.TryGetProperty("visible", out var visibleEl))
            comment.SetVisible(visibleEl.GetBoolean());

        wb.SaveAs(path);
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(path),
            sheet = ws.Name,
            cell = cellAddress,
        }, s_json));
    }

    private static ToolResult AddTextBox(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var text = root.GetProperty("text").GetString()
            ?? throw new ArgumentException("text is required.");
        return AddDrawingShape(root, ctx, path, root.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } name ? name : "Text Box", "rectangle", text, isTextBox: true);
    }

    private static ToolResult AddShape(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        var shapeType = root.TryGetProperty("shapeType", out var shapeTypeEl) && shapeTypeEl.GetString() is { Length: > 0 } providedShapeType
            ? providedShapeType
            : "rectangle";
        var name = root.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } providedName
            ? providedName
            : $"{shapeType} shape";
        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
        return AddDrawingShape(root, ctx, path, name, shapeType, text, isTextBox: false);
    }

    private static ToolResult AddChart(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"Workbook not found: {Path.GetFileName(path)}");
        if (!root.TryGetProperty("targetSheet", out var targetSheetEl) || targetSheetEl.GetString() is not { Length: > 0 } targetSheet)
            return ToolResult.Error("targetSheet is required.");
        if (!root.TryGetProperty("topLeftCell", out var anchorEl) || anchorEl.GetString() is not { Length: > 0 } topLeftCell)
            return ToolResult.Error("topLeftCell is required.");
        if (!root.TryGetProperty("series", out var seriesEl) || seriesEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("series must be an array.");

        var seriesRequests = ParseChartSeries(seriesEl).ToList();
        if (seriesRequests.Count == 0) return ToolResult.Error("At least one series is required.");

        var fallbackDataSheet = root.TryGetProperty("dataSheet", out var dataSheetEl) && dataSheetEl.GetString() is { Length: > 0 } explicitDataSheet
            ? explicitDataSheet
            : targetSheet;
        var chartType = root.TryGetProperty("chartType", out var chartTypeEl) ? (chartTypeEl.GetString() ?? "column").Trim().ToLowerInvariant() : "column";
        if (chartType is not ("column" or "bar" or "line" or "pie" or "stackedcolumn" or "stackedbar" or "area" or "doughnut" or "scatter" or "combo"))
            return ToolResult.Error("chartType must be 'column', 'bar', 'line', 'pie', 'stackedColumn', 'stackedBar', 'area', 'doughnut', 'scatter', or 'combo'.");

        var categoryRange = root.TryGetProperty("categoryRange", out var categoryRangeEl) && categoryRangeEl.GetString() is { Length: > 0 } categoryRangeValue
            ? categoryRangeValue
            : null;
        if (chartType is not "scatter" && string.IsNullOrWhiteSpace(categoryRange))
            return ToolResult.Error("categoryRange is required unless chartType is 'scatter'.");
        if (chartType == "combo" && string.IsNullOrWhiteSpace(categoryRange))
            return ToolResult.Error("combo charts require categoryRange.");
        if (chartType == "scatter" && string.IsNullOrWhiteSpace(categoryRange) && seriesRequests.Any(s => string.IsNullOrWhiteSpace(s.XValuesRange)))
            return ToolResult.Error("scatter charts require categoryRange or xValuesRange for each series.");

        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var widthColumns = root.TryGetProperty("widthColumns", out var widthEl) && widthEl.TryGetInt32(out var parsedWidth) && parsedWidth > 0 ? parsedWidth : 8;
        var heightRows = root.TryGetProperty("heightRows", out var heightEl) && heightEl.TryGetInt32(out var parsedHeight) && parsedHeight > 0 ? parsedHeight : 16;
        var options = new ExcelChartOptions(
            !root.TryGetProperty("showLegend", out var showLegendEl) || showLegendEl.GetBoolean(),
            root.TryGetProperty("legendPosition", out var legendPositionEl) && legendPositionEl.GetString() is { Length: > 0 } legendPosition ? legendPosition : "right",
            root.TryGetProperty("showDataLabels", out var showDataLabelsEl) && showDataLabelsEl.GetBoolean(),
            root.TryGetProperty("categoryAxisTitle", out var categoryAxisTitleEl) ? categoryAxisTitleEl.GetString() : null,
            root.TryGetProperty("valueAxisTitle", out var valueAxisTitleEl) ? valueAxisTitleEl.GetString() : null,
            root.TryGetProperty("secondaryValueAxisTitle", out var secondaryAxisTitleEl) ? secondaryAxisTitleEl.GetString() : null);

        try
        {
            using var workbook = SpreadsheetDocument.Open(path, true);
            var workbookPart = workbook.WorkbookPart ?? throw new InvalidOperationException("Workbook is missing a workbook part.");
            var worksheetPart = GetWorksheetPart(workbookPart, targetSheet);
            if (worksheetPart is null) return ToolResult.Error($"Sheet '{targetSheet}' not found.");

            var drawingsPart = EnsureWorksheetDrawingPart(worksheetPart);
            var chartPart = drawingsPart.AddNewPart<ChartPart>();
            BuildChartPart(
                chartPart,
                chartType,
                title,
                string.IsNullOrWhiteSpace(categoryRange) ? null : QualifyRangeFormula(categoryRange, fallbackDataSheet),
                seriesRequests.Select(s => s with
                {
                    ValuesRange = QualifyRangeFormula(s.ValuesRange, fallbackDataSheet),
                    NameCell = string.IsNullOrWhiteSpace(s.NameCell) ? null : QualifyRangeFormula(s.NameCell!, fallbackDataSheet),
                    XValuesRange = string.IsNullOrWhiteSpace(s.XValuesRange) ? null : QualifyRangeFormula(s.XValuesRange!, fallbackDataSheet),
                }).ToList(),
                options);
            var worksheetDrawing = drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
            var nextId = GetNextWorksheetObjectId(worksheetDrawing);
            worksheetDrawing.Append(BuildChartAnchor(drawingsPart.GetIdOfPart(chartPart), nextId, title ?? $"{chartType} chart", topLeftCell, widthColumns, heightRows));
            worksheetDrawing.Save();
            worksheetPart.Worksheet.Save();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename = Path.GetFileName(path),
                targetSheet,
                chartType,
                topLeftCell,
                widthColumns,
                heightRows,
            }, s_json));
        }
        catch (Exception ex) { return ToolResult.Error($"Failed to add chart: {ex.Message}"); }
    }

    private static ToolResult CreatePivotReport(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"Workbook not found: {Path.GetFileName(path)}");
        if (!root.TryGetProperty("sourceRange", out var sourceRangeEl) || sourceRangeEl.GetString() is not { Length: > 0 } sourceRangeAddress)
            return ToolResult.Error("sourceRange is required.");
        if (!root.TryGetProperty("reportSheet", out var reportSheetEl) || reportSheetEl.GetString() is not { Length: > 0 } reportSheetName)
            return ToolResult.Error("reportSheet is required.");
        if (!root.TryGetProperty("rowFields", out var rowFieldsEl) || rowFieldsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("rowFields must be an array.");
        if (!root.TryGetProperty("values", out var valueSpecsEl) || valueSpecsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("values must be an array.");

        try
        {
            using var wb = new XLWorkbook(path);
            var sourceWs = root.TryGetProperty("sourceSheet", out var sourceSheetEl) && sourceSheetEl.GetString() is { Length: > 0 } sourceSheetName
                ? wb.Worksheet(sourceSheetName)
                : wb.Worksheets.First();

            if (string.Equals(sourceWs.Name, reportSheetName, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("reportSheet must be different from the source sheet.");

            var sourceRange = sourceWs.Range(sourceRangeAddress);
            if (sourceRange.RowCount() < 2 || sourceRange.ColumnCount() < 1)
                return ToolResult.Error("sourceRange must include a header row and at least one data row.");

            var headers = Enumerable.Range(1, sourceRange.ColumnCount())
                .ToDictionary(i => i, i => sourceRange.Cell(1, i).GetString().Trim());

            var rowFields = rowFieldsEl.EnumerateArray()
                .Select(el => el.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new PivotFieldSpec(
                    value!,
                    ResolveSourceColumnIndex(sourceRange, headers, value!),
                    headers[ResolveSourceColumnIndex(sourceRange, headers, value!)]))
                .ToList();
            if (rowFields.Count == 0) return ToolResult.Error("At least one row field is required.");

            PivotFieldSpec? columnField = null;
            if (root.TryGetProperty("columnField", out var columnFieldEl) && columnFieldEl.GetString() is { Length: > 0 } columnFieldName)
            {
                var columnIndex = ResolveSourceColumnIndex(sourceRange, headers, columnFieldName);
                columnField = new PivotFieldSpec(columnFieldName, columnIndex, headers[columnIndex]);
            }

            var valueSpecs = new List<PivotValueSpec>();
            foreach (var valueSpecEl in valueSpecsEl.EnumerateArray())
            {
                var fieldName = valueSpecEl.TryGetProperty("field", out var fieldEl) ? fieldEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(fieldName)) return ToolResult.Error("Each values entry requires a field.");
                var columnIndex = ResolveSourceColumnIndex(sourceRange, headers, fieldName!);
                var summary = valueSpecEl.TryGetProperty("summary", out var summaryEl) ? (summaryEl.GetString() ?? "sum").Trim().ToLowerInvariant() : "sum";
                if (summary is not ("sum" or "count" or "average" or "min" or "max"))
                    return ToolResult.Error($"Unsupported summary '{summary}'.");

                var label = valueSpecEl.TryGetProperty("label", out var labelEl) && labelEl.GetString() is { Length: > 0 } customLabel
                    ? customLabel
                    : $"{summary.ToUpperInvariant()} {headers[columnIndex]}";
                var numberFormat = valueSpecEl.TryGetProperty("numberFormat", out var numberFormatEl) ? numberFormatEl.GetString() : null;
                valueSpecs.Add(new PivotValueSpec(fieldName!, columnIndex, headers[columnIndex], summary, label, numberFormat));
            }
            if (valueSpecs.Count == 0) return ToolResult.Error("At least one value definition is required.");

            var records = new List<PivotRecord>();
            for (int rowIndex = 2; rowIndex <= sourceRange.RowCount(); rowIndex++)
            {
                var row = sourceRange.Row(rowIndex);
                var rowKeyValues = rowFields.Select(field => GetPivotDisplayValue(row.Cell(field.RelativeColumnIndex))).ToArray();
                var columnValue = columnField is null ? null : GetPivotDisplayValue(row.Cell(columnField.RelativeColumnIndex));
                var measures = new List<PivotMeasureInput>(valueSpecs.Count);
                foreach (var valueSpec in valueSpecs)
                    measures.Add(ReadPivotMeasureInput(row.Cell(valueSpec.RelativeColumnIndex), valueSpec));
                records.Add(new PivotRecord(rowKeyValues, columnValue, measures));
            }

            if (wb.TryGetWorksheet(reportSheetName, out var existingReport)) existingReport.Delete();
            var reportWs = wb.AddWorksheet(reportSheetName);
            var includeGrandTotal = !root.TryGetProperty("includeGrandTotal", out var includeGrandTotalEl) || includeGrandTotalEl.GetBoolean();

            var writtenRange = columnField is null
                ? WritePivotTabularReport(reportWs, rowFields, valueSpecs, records, includeGrandTotal)
                : WritePivotMatrixReport(reportWs, rowFields, columnField, valueSpecs, records, includeGrandTotal);

            StylePivotReport(writtenRange);
            wb.SaveAs(path);

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename = Path.GetFileName(path),
                reportSheet = reportSheetName,
                rowCount = writtenRange.RowCount(),
                columnCount = writtenRange.ColumnCount(),
                groupedByColumns = columnField is not null,
            }, s_json));
        }
        catch (Exception ex) { return ToolResult.Error($"Failed to create pivot report: {ex.Message}"); }
    }

    private static ToolResult CreatePivotTable(JsonElement root, ToolContext ctx)
    {
        var path = ResolveFile(root, ctx);
        if (!File.Exists(path)) return ToolResult.Error($"Workbook not found: {Path.GetFileName(path)}");
        if (!root.TryGetProperty("sourceRange", out var sourceRangeEl) || sourceRangeEl.GetString() is not { Length: > 0 } sourceRangeAddress)
            return ToolResult.Error("sourceRange is required.");
        if (!root.TryGetProperty("targetSheet", out var targetSheetEl) || targetSheetEl.GetString() is not { Length: > 0 } targetSheetName)
            return ToolResult.Error("targetSheet is required.");
        if (!root.TryGetProperty("targetCell", out var targetCellEl) || targetCellEl.GetString() is not { Length: > 0 } targetCellAddress)
            return ToolResult.Error("targetCell is required.");
        if (!root.TryGetProperty("rowFields", out var rowFieldsEl) || rowFieldsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("rowFields must be an array.");
        if (!root.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("values must be an array.");

        try
        {
            using var wb = new XLWorkbook(path);
            var sourceWs = root.TryGetProperty("sourceSheet", out var sourceSheetEl) && sourceSheetEl.GetString() is { Length: > 0 } sourceSheetName
                ? wb.Worksheet(sourceSheetName)
                : wb.Worksheets.First();
            var sourceRange = sourceWs.Range(sourceRangeAddress);
            if (sourceRange.RowCount() < 2 || sourceRange.ColumnCount() < 1)
                return ToolResult.Error("sourceRange must include a header row and at least one data row.");

            var targetWs = wb.TryGetWorksheet(targetSheetName, out var existingTarget)
                ? existingTarget
                : wb.AddWorksheet(targetSheetName);

            var rowFields = ReadStringArray(rowFieldsEl);
            if (rowFields.Count == 0)
                return ToolResult.Error("At least one row field is required.");

            var columnFields = root.TryGetProperty("columnFields", out var columnFieldsEl) && columnFieldsEl.ValueKind == JsonValueKind.Array
                ? ReadStringArray(columnFieldsEl)
                : new List<string>();
            var filterFields = root.TryGetProperty("filterFields", out var filterFieldsEl) && filterFieldsEl.ValueKind == JsonValueKind.Array
                ? ReadStringArray(filterFieldsEl)
                : new List<string>();
            var valueSpecs = ParseNativePivotValueSpecs(valuesEl);
            if (valueSpecs.Count == 0)
                return ToolResult.Error("At least one value definition is required.");

            var pivotName = root.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } suppliedName
                ? suppliedName
                : $"PivotTable{targetWs.PivotTables.Count() + 1}";

            if (targetWs.PivotTables.Any(pt => string.Equals(pt.Title, pivotName, StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error($"PivotTable '{pivotName}' already exists on sheet '{targetSheetName}'.");

            var pivotCache = wb.PivotCaches.Add(sourceRange)
                .Refresh()
                .SetRefreshDataOnOpen(!root.TryGetProperty("refreshOnOpen", out var refreshEl) || refreshEl.GetBoolean())
                .SetSaveSourceData(!root.TryGetProperty("saveSourceData", out var saveSourceDataEl) || saveSourceDataEl.GetBoolean())
                .SetItemsToRetainPerField(XLItemsToRetain.None);

            var pivot = targetWs.PivotTables.Add(pivotName, targetWs.Cell(targetCellAddress), pivotCache)
                .SetAutofitColumns(!root.TryGetProperty("autofitColumns", out var autoFitEl) || autoFitEl.GetBoolean())
                .SetClassicPivotTableLayout(!root.TryGetProperty("classicLayout", out var classicLayoutEl) || classicLayoutEl.GetBoolean())
                .SetRepeatRowLabels(!root.TryGetProperty("repeatRowLabels", out var repeatRowLabelsEl) || repeatRowLabelsEl.GetBoolean())
                .SetShowGrandTotalsRows(!root.TryGetProperty("showGrandTotalsRows", out var showGrandTotalsRowsEl) || showGrandTotalsRowsEl.GetBoolean())
                .SetShowGrandTotalsColumns(!root.TryGetProperty("showGrandTotalsColumns", out var showGrandTotalsColumnsEl) || showGrandTotalsColumnsEl.GetBoolean())
                .SetShowColumnHeaders(true)
                .SetShowRowHeaders(true);

            if (root.TryGetProperty("title", out var titleEl) && titleEl.GetString() is { Length: > 0 } title)
                pivot.SetTitle(title);

            foreach (var field in rowFields)
                pivot.RowLabels.Add(field);
            foreach (var field in columnFields)
                pivot.ColumnLabels.Add(field);
            foreach (var field in filterFields)
                pivot.ReportFilters.Add(field);

            foreach (var valueSpec in valueSpecs)
            {
                var pivotValue = string.IsNullOrWhiteSpace(valueSpec.Label)
                    ? pivot.Values.Add(valueSpec.Field)
                    : pivot.Values.Add(valueSpec.Field, valueSpec.Label!);
                pivotValue.SetSummaryFormula(ParsePivotSummary(valueSpec.Summary));
                if (!string.IsNullOrWhiteSpace(valueSpec.NumberFormat))
                    pivotValue.NumberFormat.SetFormat(valueSpec.NumberFormat!);
            }

            wb.SaveAs(path);
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename = Path.GetFileName(path),
                pivotTable = pivotName,
                targetSheet = targetSheetName,
                targetCell = targetCellAddress,
                rowFields,
                columnFields,
                filterFields,
                valueCount = valueSpecs.Count,
            }, s_json));
        }
        catch (Exception ex) { return ToolResult.Error($"Failed to create native PivotTable: {ex.Message}"); }
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

    private static IEnumerable<ExcelChartSeriesSpec> ParseChartSeries(JsonElement seriesEl)
    {
        foreach (var seriesSpec in seriesEl.EnumerateArray())
        {
            var valuesRange = seriesSpec.TryGetProperty("valuesRange", out var valuesRangeEl) ? valuesRangeEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(valuesRange)) continue;
            yield return new ExcelChartSeriesSpec(
                seriesSpec.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                seriesSpec.TryGetProperty("nameCell", out var nameCellEl) ? nameCellEl.GetString() : null,
                valuesRange!,
                seriesSpec.TryGetProperty("chartType", out var seriesChartTypeEl) ? seriesChartTypeEl.GetString() : null,
                seriesSpec.TryGetProperty("secondaryAxis", out var secondaryAxisEl) && secondaryAxisEl.GetBoolean(),
                seriesSpec.TryGetProperty("xValuesRange", out var xValuesRangeEl) ? xValuesRangeEl.GetString() : null,
                seriesSpec.TryGetProperty("color", out var colorEl) ? colorEl.GetString() : null);
        }
    }

    private static WorksheetPart? GetWorksheetPart(WorkbookPart workbookPart, string sheetName)
    {
        var sheet = workbookPart.Workbook.Sheets?.Elements<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
        return sheet?.Id?.Value is { Length: > 0 } relationshipId
            ? workbookPart.GetPartById(relationshipId) as WorksheetPart
            : null;
    }

    private static DrawingsPart EnsureWorksheetDrawingPart(WorksheetPart worksheetPart)
    {
        if (worksheetPart.DrawingsPart is { } existingPart)
        {
            existingPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
            return existingPart;
        }

        var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
        drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing();
        var drawing = new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) };
        worksheetPart.Worksheet.Append(drawing);
        return drawingsPart;
    }

    private static void BuildChartPart(ChartPart chartPart, string chartType, string? title, string? categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        var chartSpace = chartPart.ChartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });

        var chart = chartSpace.AppendChild(new C.Chart());
        if (!string.IsNullOrWhiteSpace(title)) chart.Append(CreateChartTitle(title!));

        var plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.AppendChild(new C.Layout());

        switch (chartType)
        {
            case "area":
                BuildAreaChart(plotArea, categoryFormula!, series, options);
                break;
            case "doughnut":
                BuildDoughnutChart(plotArea, categoryFormula!, series, options);
                break;
            case "line":
                BuildLineChart(plotArea, categoryFormula!, series, options);
                break;
            case "pie":
                BuildPieChart(plotArea, categoryFormula!, series, options);
                break;
            case "scatter":
                BuildScatterChart(plotArea, categoryFormula, series, options);
                break;
            case "stackedbar":
                BuildBarChart(plotArea, categoryFormula!, series, horizontal: true, C.BarGroupingValues.Stacked, options);
                break;
            case "stackedcolumn":
                BuildBarChart(plotArea, categoryFormula!, series, horizontal: false, C.BarGroupingValues.Stacked, options);
                break;
            case "combo":
                BuildComboChart(plotArea, categoryFormula!, series, options);
                break;
            default:
                BuildBarChart(plotArea, categoryFormula!, series, chartType == "bar", C.BarGroupingValues.Clustered, options);
                break;
        }

        if (options.ShowLegend)
            chart.Append(new C.Legend(new C.LegendPosition { Val = ParseLegendPosition(options.LegendPosition) }, new C.Layout()));

        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });
        chartSpace.Save();
    }

    private static C.Title CreateChartTitle(string title)
        => new(
            new C.ChartText(
                new C.RichText(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(
                        new A.Run(
                            new A.RunProperties { Language = "en-US" },
                            new A.Text(title)),
                        new A.EndParagraphRunProperties { Language = "en-US" }))),
            new C.Layout(),
            new C.Overlay { Val = false });

    private static void BuildBarChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, bool horizontal, C.BarGroupingValues grouping, ExcelChartOptions options)
    {
        const uint catAxisId = 48650112U;
        const uint valAxisId = 48672768U;

        var barChart = plotArea.AppendChild(new C.BarChart(
            new C.BarDirection { Val = horizontal ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column },
            new C.BarGrouping { Val = grouping },
            new C.VaryColors { Val = false }));

        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var seriesElement = new C.BarChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index });
            AppendSeriesText(seriesElement, seriesSpec, index);
            seriesElement.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            seriesElement.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(seriesElement, seriesSpec);
            barChart.Append(seriesElement);
            index++;
        }

        if (options.ShowDataLabels)
            barChart.Append(CreateDataLabels(showCategoryName: false));

        barChart.Append(new C.AxisId { Val = catAxisId });
        barChart.Append(new C.AxisId { Val = valAxisId });

        var categoryAxis = new C.CategoryAxis(
            new C.AxisId { Val = catAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Left : C.AxisPositionValues.Bottom },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = valAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 });
        if (!string.IsNullOrWhiteSpace(options.CategoryAxisTitle))
            categoryAxis.Append(CreateChartTitle(options.CategoryAxisTitle!));
        plotArea.Append(categoryAxis);

        var valueAxis = new C.ValueAxis(
            new C.AxisId { Val = valAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = catAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.ValueAxisTitle))
            valueAxis.Append(CreateChartTitle(options.ValueAxisTitle!));
        plotArea.Append(valueAxis);
    }

    private static void BuildLineChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        const uint catAxisId = 48670112U;
        const uint valAxisId = 48692768U;

        var lineChart = plotArea.AppendChild(new C.LineChart(
            new C.Grouping { Val = C.GroupingValues.Standard },
            new C.VaryColors { Val = false }));

        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var lineSeries = new C.LineChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index },
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }));
            AppendSeriesText(lineSeries, seriesSpec, index);
            lineSeries.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            lineSeries.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(lineSeries, seriesSpec);
            lineChart.Append(lineSeries);
            index++;
        }

        if (options.ShowDataLabels)
            lineChart.Append(CreateDataLabels(showCategoryName: false));

        lineChart.Append(new C.AxisId { Val = catAxisId });
        lineChart.Append(new C.AxisId { Val = valAxisId });
        lineChart.Append(new C.Smooth { Val = false });

        var categoryAxis = new C.CategoryAxis(
            new C.AxisId { Val = catAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = valAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 });
        if (!string.IsNullOrWhiteSpace(options.CategoryAxisTitle))
            categoryAxis.Append(CreateChartTitle(options.CategoryAxisTitle!));
        plotArea.Append(categoryAxis);

        var valueAxis = new C.ValueAxis(
            new C.AxisId { Val = valAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = catAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.ValueAxisTitle))
            valueAxis.Append(CreateChartTitle(options.ValueAxisTitle!));
        plotArea.Append(valueAxis);
    }

    private static void BuildPieChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        var pieChart = plotArea.AppendChild(new C.PieChart(new C.VaryColors { Val = true }));
        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var pieSeries = new C.PieChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index });
            AppendSeriesText(pieSeries, seriesSpec, index);
            pieSeries.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            pieSeries.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(pieSeries, seriesSpec);
            pieChart.Append(pieSeries);
            index++;
        }
        if (options.ShowDataLabels)
            pieChart.Append(CreateDataLabels(showCategoryName: true));
        pieChart.Append(new C.FirstSliceAngle { Val = 0 });
    }

    private static void BuildDoughnutChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        var doughnutChart = plotArea.AppendChild(new C.DoughnutChart(new C.VaryColors { Val = true }));
        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var doughnutSeries = new C.PieChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index });
            AppendSeriesText(doughnutSeries, seriesSpec, index);
            doughnutSeries.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            doughnutSeries.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(doughnutSeries, seriesSpec);
            doughnutChart.Append(doughnutSeries);
            index++;
        }

        if (options.ShowDataLabels)
            doughnutChart.Append(CreateDataLabels(showCategoryName: true));
        doughnutChart.Append(new C.FirstSliceAngle { Val = 0 });
        doughnutChart.Append(new C.HoleSize { Val = 55 });
    }

    private static void BuildAreaChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        const uint catAxisId = 48710112U;
        const uint valAxisId = 48732768U;

        var areaChart = plotArea.AppendChild(new C.AreaChart(
            new C.Grouping { Val = C.GroupingValues.Standard },
            new C.VaryColors { Val = false }));

        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var areaSeries = new C.AreaChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index });
            AppendSeriesText(areaSeries, seriesSpec, index);
            areaSeries.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            areaSeries.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(areaSeries, seriesSpec);
            areaChart.Append(areaSeries);
            index++;
        }

        if (options.ShowDataLabels)
            areaChart.Append(CreateDataLabels(showCategoryName: false));

        areaChart.Append(new C.AxisId { Val = catAxisId });
        areaChart.Append(new C.AxisId { Val = valAxisId });

        var categoryAxis = new C.CategoryAxis(
            new C.AxisId { Val = catAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = valAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 });
        if (!string.IsNullOrWhiteSpace(options.CategoryAxisTitle))
            categoryAxis.Append(CreateChartTitle(options.CategoryAxisTitle!));
        plotArea.Append(categoryAxis);

        var valueAxis = new C.ValueAxis(
            new C.AxisId { Val = valAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = catAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.ValueAxisTitle))
            valueAxis.Append(CreateChartTitle(options.ValueAxisTitle!));
        plotArea.Append(valueAxis);
    }

    private static void BuildScatterChart(C.PlotArea plotArea, string? categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        const uint xAxisId = 48750112U;
        const uint yAxisId = 48772768U;

        var scatterChart = plotArea.AppendChild(new C.ScatterChart(
            new C.ScatterStyle { Val = C.ScatterStyleValues.LineMarker },
            new C.VaryColors { Val = false }));

        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var xFormula = string.IsNullOrWhiteSpace(seriesSpec.XValuesRange) ? categoryFormula : seriesSpec.XValuesRange;
            if (string.IsNullOrWhiteSpace(xFormula))
                throw new ArgumentException("Scatter series require xValuesRange or categoryRange.");

            var scatterSeries = new C.ScatterChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index },
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }));
            AppendSeriesText(scatterSeries, seriesSpec, index);
            scatterSeries.Append(new C.XValues(new C.NumberReference(new C.Formula(xFormula))));
            scatterSeries.Append(new C.YValues(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(scatterSeries, seriesSpec);
            scatterChart.Append(scatterSeries);
            index++;
        }

        if (options.ShowDataLabels)
            scatterChart.Append(CreateDataLabels(showCategoryName: false));

        scatterChart.Append(new C.AxisId { Val = xAxisId });
        scatterChart.Append(new C.AxisId { Val = yAxisId });

        var xAxis = new C.ValueAxis(
            new C.AxisId { Val = xAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = yAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.CategoryAxisTitle))
            xAxis.Append(CreateChartTitle(options.CategoryAxisTitle!));
        plotArea.Append(xAxis);

        var yAxis = new C.ValueAxis(
            new C.AxisId { Val = yAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = xAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.ValueAxisTitle))
            yAxis.Append(CreateChartTitle(options.ValueAxisTitle!));
        plotArea.Append(yAxis);
    }

    private static void BuildComboChart(C.PlotArea plotArea, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, ExcelChartOptions options)
    {
        const uint catAxisId = 48810112U;
        const uint primaryValAxisId = 48832768U;
        const uint secondaryValAxisId = 48842768U;

        var primaryBarSeries = series.Where(spec => ResolveComboSeriesType(spec) is "column" or "bar" && !spec.SecondaryAxis).ToList();
        var primaryLineSeries = series.Where(spec => ResolveComboSeriesType(spec) == "line" && !spec.SecondaryAxis).ToList();
        var secondarySeries = series.Where(spec => spec.SecondaryAxis).ToList();

        if (primaryBarSeries.Count > 0)
        {
            var barChart = plotArea.AppendChild(new C.BarChart(
                new C.BarDirection { Val = C.BarDirectionValues.Column },
                new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
                new C.VaryColors { Val = false }));
            AppendComboBarSeries(barChart, categoryFormula, primaryBarSeries, options.ShowDataLabels);
            barChart.Append(new C.AxisId { Val = catAxisId });
            barChart.Append(new C.AxisId { Val = primaryValAxisId });
        }

        if (primaryLineSeries.Count > 0)
        {
            var lineChart = plotArea.AppendChild(new C.LineChart(
                new C.Grouping { Val = C.GroupingValues.Standard },
                new C.VaryColors { Val = false }));
            AppendComboLineSeries(lineChart, categoryFormula, primaryLineSeries, options.ShowDataLabels);
            lineChart.Append(new C.AxisId { Val = catAxisId });
            lineChart.Append(new C.AxisId { Val = primaryValAxisId });
            lineChart.Append(new C.Smooth { Val = false });
        }

        if (secondarySeries.Count > 0)
        {
            var secondaryLineChart = plotArea.AppendChild(new C.LineChart(
                new C.Grouping { Val = C.GroupingValues.Standard },
                new C.VaryColors { Val = false }));
            AppendComboLineSeries(secondaryLineChart, categoryFormula, secondarySeries, options.ShowDataLabels);
            secondaryLineChart.Append(new C.AxisId { Val = catAxisId });
            secondaryLineChart.Append(new C.AxisId { Val = secondaryValAxisId });
            secondaryLineChart.Append(new C.Smooth { Val = false });
        }

        var categoryAxis = new C.CategoryAxis(
            new C.AxisId { Val = catAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = primaryValAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 });
        if (!string.IsNullOrWhiteSpace(options.CategoryAxisTitle))
            categoryAxis.Append(CreateChartTitle(options.CategoryAxisTitle!));
        plotArea.Append(categoryAxis);

        var primaryValueAxis = new C.ValueAxis(
            new C.AxisId { Val = primaryValAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = catAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        if (!string.IsNullOrWhiteSpace(options.ValueAxisTitle))
            primaryValueAxis.Append(CreateChartTitle(options.ValueAxisTitle!));
        plotArea.Append(primaryValueAxis);

        if (secondarySeries.Count > 0)
        {
            var secondaryValueAxis = new C.ValueAxis(
                new C.AxisId { Val = secondaryValAxisId },
                new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                new C.Delete { Val = false },
                new C.AxisPosition { Val = C.AxisPositionValues.Right },
                new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
                new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
                new C.CrossingAxis { Val = catAxisId },
                new C.Crosses { Val = C.CrossesValues.Maximum },
                new C.CrossBetween { Val = C.CrossBetweenValues.Between });
            if (!string.IsNullOrWhiteSpace(options.SecondaryValueAxisTitle))
                secondaryValueAxis.Append(CreateChartTitle(options.SecondaryValueAxisTitle!));
            plotArea.Append(secondaryValueAxis);
        }
    }

    private static void AppendComboBarSeries(C.BarChart barChart, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, bool showDataLabels)
    {
        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var seriesElement = new C.BarChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index });
            AppendSeriesText(seriesElement, seriesSpec, index);
            seriesElement.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            seriesElement.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(seriesElement, seriesSpec);
            barChart.Append(seriesElement);
            index++;
        }

        if (showDataLabels)
            barChart.Append(CreateDataLabels(showCategoryName: false));
    }

    private static void AppendComboLineSeries(C.LineChart lineChart, string categoryFormula, IReadOnlyList<ExcelChartSeriesSpec> series, bool showDataLabels)
    {
        uint index = 0;
        foreach (var seriesSpec in series)
        {
            var lineSeries = new C.LineChartSeries(
                new C.Index { Val = index },
                new C.Order { Val = index },
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }));
            AppendSeriesText(lineSeries, seriesSpec, index);
            lineSeries.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoryFormula))));
            lineSeries.Append(new C.Values(new C.NumberReference(new C.Formula(seriesSpec.ValuesRange))));
            AppendSeriesShapeProperties(lineSeries, seriesSpec);
            lineChart.Append(lineSeries);
            index++;
        }

        if (showDataLabels)
            lineChart.Append(CreateDataLabels(showCategoryName: false));
    }

    private static void AppendSeriesText(OpenXmlCompositeElement seriesElement, ExcelChartSeriesSpec seriesSpec, uint index)
    {
        if (!string.IsNullOrWhiteSpace(seriesSpec.NameCell))
        {
            seriesElement.Append(new C.SeriesText(new C.StringReference(new C.Formula(seriesSpec.NameCell))));
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(seriesSpec.Name) ? $"Series {index + 1}" : seriesSpec.Name;
        seriesElement.Append(new C.SeriesText(new C.NumericValue(displayName)));
    }

    private static void AppendSeriesShapeProperties(OpenXmlCompositeElement seriesElement, ExcelChartSeriesSpec seriesSpec)
    {
        if (string.IsNullOrWhiteSpace(seriesSpec.Color))
            return;

        seriesElement.Append(new C.ChartShapeProperties(
            new A.SolidFill(new A.RgbColorModelHex { Val = NormalizeHex(seriesSpec.Color) }),
            new A.Outline(new A.NoFill())));
    }

    private static C.DataLabels CreateDataLabels(bool showCategoryName)
        => new(
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = true },
            new C.ShowCategoryName { Val = showCategoryName },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = false },
            new C.ShowBubbleSize { Val = false });

    private static C.LegendPositionValues ParseLegendPosition(string legendPosition)
        => legendPosition.Trim().ToLowerInvariant() switch
        {
            "left" => C.LegendPositionValues.Left,
            "top" => C.LegendPositionValues.Top,
            "bottom" => C.LegendPositionValues.Bottom,
            _ => C.LegendPositionValues.Right,
        };

    private static string ResolveComboSeriesType(ExcelChartSeriesSpec seriesSpec)
        => seriesSpec.ChartType?.Trim().ToLowerInvariant() switch
        {
            "line" => "line",
            _ => "column",
        };

    private static Xdr.TwoCellAnchor BuildChartAnchor(string chartRelationshipId, uint objectId, string name, string topLeftCell, int widthColumns, int heightRows)
    {
        var (startColumn, startRow) = ParseCellAddress(topLeftCell);
        var endColumn = startColumn + Math.Max(1, widthColumns);
        var endRow = startRow + Math.Max(6, heightRows);

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId(startColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(startRow.ToString()),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId(endColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(endRow.ToString()),
                new Xdr.RowOffset("0")),
            new Xdr.GraphicFrame(
                new Xdr.NonVisualGraphicFrameProperties(
                    new Xdr.NonVisualDrawingProperties { Id = objectId, Name = name },
                    new Xdr.NonVisualGraphicFrameDrawingProperties()),
                new Xdr.Transform(
                    new A.Offset { X = 0L, Y = 0L },
                    new A.Extents { Cx = 0L, Cy = 0L }),
                new A.Graphic(
                    new A.GraphicData(
                        new C.ChartReference { Id = chartRelationshipId })
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" })),
            new Xdr.ClientData());
    }

    private static uint GetNextWorksheetObjectId(Xdr.WorksheetDrawing worksheetDrawing)
        => worksheetDrawing.Descendants<Xdr.NonVisualDrawingProperties>()
            .Select(p => p.Id?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;

    private static XLPicturePlacement ParsePicturePlacement(string placement)
        => placement.Trim().ToLowerInvariant() switch
        {
            "move" => XLPicturePlacement.Move,
            "freefloating" => XLPicturePlacement.FreeFloating,
            _ => XLPicturePlacement.MoveAndSize,
        };

    private static XLPictureFormat ParsePictureFormat(string filePath)
        => Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant() switch
        {
            "bmp" => XLPictureFormat.Bmp,
            "gif" => XLPictureFormat.Gif,
            "png" => XLPictureFormat.Png,
            "tif" or "tiff" => XLPictureFormat.Tiff,
            "ico" => XLPictureFormat.Icon,
            "pcx" => XLPictureFormat.Pcx,
            "jpg" or "jpeg" => XLPictureFormat.Jpeg,
            "emf" => XLPictureFormat.Emf,
            "wmf" => XLPictureFormat.Wmf,
            "webp" => XLPictureFormat.Webp,
            _ => XLPictureFormat.Unknown,
        };

    private static ToolResult AddDrawingShape(JsonElement root, ToolContext ctx, string path, string name, string shapeType, string? text, bool isTextBox)
    {
        var topLeftCell = root.GetProperty("topLeftCell").GetString()
            ?? throw new ArgumentException("topLeftCell is required.");
        var sheetName = root.TryGetProperty("sheet", out var sheetEl) && sheetEl.GetString() is { Length: > 0 } providedSheetName
            ? providedSheetName
            : "Sheet1";
        var widthColumns = root.TryGetProperty("widthColumns", out var widthEl) && widthEl.TryGetInt32(out var parsedWidth) && parsedWidth > 0 ? parsedWidth : 4;
        var heightRows = root.TryGetProperty("heightRows", out var heightEl) && heightEl.TryGetInt32(out var parsedHeight) && parsedHeight > 0 ? parsedHeight : 3;
        var fillColor = root.TryGetProperty("fillColor", out var fillColorEl) ? fillColorEl.GetString() : (isTextBox ? "#FFFFFF" : "#D9E2F3");
        var lineColor = root.TryGetProperty("borderColor", out var borderColorEl) ? borderColorEl.GetString() : root.TryGetProperty("lineColor", out var lineColorEl) ? lineColorEl.GetString() : "#5B6B82";
        var fontColor = root.TryGetProperty("fontColor", out var fontColorEl) ? fontColorEl.GetString() : "#1F2937";
        var bold = root.TryGetProperty("bold", out var boldEl) && boldEl.GetBoolean();
        var fontSize = root.TryGetProperty("fontSize", out var fontSizeEl) && fontSizeEl.TryGetInt32(out var parsedFontSize) && parsedFontSize > 0 ? parsedFontSize : 1200;

        using (var wb = OpenOrCreate(path))
        {
            GetSheetByNameOrCreate(wb, sheetName);
            wb.SaveAs(path);
        }

        try
        {
            using var workbook = SpreadsheetDocument.Open(path, true);
            var workbookPart = workbook.WorkbookPart ?? throw new InvalidOperationException("Workbook is missing a workbook part.");
            var worksheetPart = GetWorksheetPart(workbookPart, sheetName);
            if (worksheetPart is null) return ToolResult.Error($"Sheet '{sheetName}' not found.");

            var drawingsPart = EnsureWorksheetDrawingPart(worksheetPart);
            var worksheetDrawing = drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
            var nextId = GetNextWorksheetObjectId(worksheetDrawing);
            var shape = CreateWorksheetShape(nextId, name, shapeType, text, fillColor, lineColor, fontColor, bold, fontSize, isTextBox);
            worksheetDrawing.Append(BuildWorksheetShapeAnchor(shape, topLeftCell, widthColumns, heightRows));
            worksheetDrawing.Save();
            worksheetPart.Worksheet.Save();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filename = Path.GetFileName(path),
                sheet = sheetName,
                topLeftCell,
                shapeType,
                name,
            }, s_json));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to add drawing shape: {ex.Message}");
        }
    }

    private static IXLWorksheet GetSheetByNameOrCreate(XLWorkbook wb, string sheetName)
    {
        if (wb.TryGetWorksheet(sheetName, out var worksheet))
            return worksheet;
        return wb.AddWorksheet(sheetName);
    }

    private static Xdr.TwoCellAnchor BuildWorksheetShapeAnchor(Xdr.Shape shape, string topLeftCell, int widthColumns, int heightRows)
    {
        var (startColumn, startRow) = ParseCellAddress(topLeftCell);
        var endColumn = startColumn + Math.Max(1, widthColumns);
        var endRow = startRow + Math.Max(1, heightRows);

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId(startColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(startRow.ToString()),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId(endColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(endRow.ToString()),
                new Xdr.RowOffset("0")),
            shape,
            new Xdr.ClientData());
    }

    private static Xdr.TwoCellAnchor BuildWorksheetPictureAnchor(Xdr.Picture picture, string topLeftCell, int widthColumns, int heightRows, int xOffsetPixels, int yOffsetPixels)
    {
        var (startColumn, startRow) = ParseCellAddress(topLeftCell);
        var endColumn = startColumn + Math.Max(1, widthColumns);
        var endRow = startRow + Math.Max(1, heightRows);

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId(startColumn.ToString()),
                new Xdr.ColumnOffset((xOffsetPixels * 9525L).ToString()),
                new Xdr.RowId(startRow.ToString()),
                new Xdr.RowOffset((yOffsetPixels * 9525L).ToString())),
            new Xdr.ToMarker(
                new Xdr.ColumnId(endColumn.ToString()),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(endRow.ToString()),
                new Xdr.RowOffset("0")),
            picture,
            new Xdr.ClientData());
    }

    private static Xdr.Picture CreateWorksheetPicture(uint objectId, string name, string relationshipId)
        => new(
            new Xdr.NonVisualPictureProperties(
                new Xdr.NonVisualDrawingProperties { Id = objectId, Name = name },
                new Xdr.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true })),
            new Xdr.BlipFill(
                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                new A.Stretch(new A.FillRectangle())),
            new Xdr.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = 0L, Y = 0L },
                    new A.Extents { Cx = 0L, Cy = 0L }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

    private static Xdr.Shape CreateWorksheetShape(uint objectId, string name, string shapeType, string? text, string? fillColor, string? lineColor, string? fontColor, bool bold, int fontSize, bool isTextBox)
    {
        var preset = shapeType.Trim().ToLowerInvariant() switch
        {
            "ellipse" => A.ShapeTypeValues.Ellipse,
            "line" => A.ShapeTypeValues.Line,
            _ => A.ShapeTypeValues.Rectangle,
        };

        var shapeProperties = new Xdr.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 0L, Y = 0L },
                new A.Extents { Cx = 0L, Cy = 0L }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = preset });

        if (!string.IsNullOrWhiteSpace(fillColor) && preset != A.ShapeTypeValues.Line)
            shapeProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = NormalizeHex(fillColor) }));
        else
            shapeProperties.Append(new A.NoFill());

        shapeProperties.Append(CreateOutline(lineColor));

        var shape = new Xdr.Shape(
            new Xdr.NonVisualShapeProperties(
                new Xdr.NonVisualDrawingProperties { Id = objectId, Name = name },
                new Xdr.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true })),
            shapeProperties);

        if (!string.IsNullOrWhiteSpace(text) || isTextBox)
            shape.Append(CreateSpreadsheetTextBody(text ?? string.Empty, fontColor, bold, fontSize));

        return shape;
    }

    private static A.Outline CreateOutline(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return new A.Outline(new A.NoFill());

        return new A.Outline(
            new A.SolidFill(new A.RgbColorModelHex { Val = NormalizeHex(color) }));
    }

    private static A.TextBody CreateSpreadsheetTextBody(string text, string? fontColor, bool bold, int fontSize)
    {
        var body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var run = new A.Run(new A.Text(line));
            run.RunProperties = new A.RunProperties { Language = "en-US", FontSize = fontSize };
            if (bold) run.RunProperties.Bold = true;
            if (!string.IsNullOrWhiteSpace(fontColor))
                run.RunProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = NormalizeHex(fontColor) }));
            body.Append(new A.Paragraph(run, new A.EndParagraphRunProperties { Language = "en-US", FontSize = fontSize }));
        }

        return body;
    }

    private static string NormalizeHex(string color)
        => color.Trim().TrimStart('#');

    private static string QualifyRangeFormula(string range, string fallbackSheet)
    {
        var trimmed = range.Trim();
        return trimmed.Contains('!')
            ? trimmed
            : $"{QuoteSheetName(fallbackSheet)}!{trimmed}";
    }

    private static string QuoteSheetName(string sheetName)
        => $"'{sheetName.Replace("'", "''", StringComparison.Ordinal)}'";

    private static (int Column, int Row) ParseCellAddress(string address)
    {
        var match = Regex.Match(address.Trim().ToUpperInvariant(), "^([A-Z]+)([0-9]+)$");
        if (!match.Success) throw new ArgumentException($"Invalid cell address '{address}'.");

        var column = ColumnLetterToNumber(match.Groups[1].Value) - 1;
        var row = int.Parse(match.Groups[2].Value) - 1;
        return (column, row);
    }

    private static int ResolveSourceColumnIndex(IXLRange sourceRange, IReadOnlyDictionary<int, string> headers, string field)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Value, field.Trim(), StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        }

        if (Regex.IsMatch(field.Trim(), "^[A-Za-z]+$"))
        {
            var absoluteColumn = ColumnLetterToNumber(field.Trim().ToUpperInvariant());
            var firstColumn = sourceRange.FirstColumn().ColumnNumber();
            var relativeColumn = absoluteColumn - firstColumn + 1;
            if (relativeColumn >= 1 && relativeColumn <= sourceRange.ColumnCount())
                return relativeColumn;
        }

        throw new ArgumentException($"Field '{field}' was not found in the source range header row.");
    }

    private static int ColumnLetterToNumber(string columnLetters)
    {
        var value = 0;
        foreach (var ch in columnLetters)
        {
            value *= 26;
            value += ch - 'A' + 1;
        }
        return value;
    }

    private static string GetPivotDisplayValue(IXLCell cell)
    {
        var value = GetCellValue(cell);
        if (value is null) return "(blank)";
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? "(blank)" : text;
    }

    private static PivotMeasureInput ReadPivotMeasureInput(IXLCell cell, PivotValueSpec spec)
    {
        if (cell.IsEmpty()) return new PivotMeasureInput(false, null);
        if (spec.Summary == "count") return new PivotMeasureInput(true, null);

        if (cell.DataType is XLDataType.Number or XLDataType.DateTime)
            return new PivotMeasureInput(true, cell.GetDouble());

        if (double.TryParse(cell.GetString(), out var parsed))
            return new PivotMeasureInput(true, parsed);

        throw new ArgumentException($"Field '{spec.Field}' contains non-numeric data, which cannot be used with summary '{spec.Summary}'.");
    }

    private static IXLRange WritePivotTabularReport(
        IXLWorksheet worksheet,
        IReadOnlyList<PivotFieldSpec> rowFields,
        IReadOnlyList<PivotValueSpec> valueSpecs,
        IReadOnlyList<PivotRecord> records,
        bool includeGrandTotal)
    {
        var grouped = records
            .GroupBy(record => string.Join('\u001F', record.RowKeyValues), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rowIndex = 1;
        var columnIndex = 1;
        foreach (var field in rowFields) worksheet.Cell(rowIndex, columnIndex++).Value = field.Header;
        foreach (var valueSpec in valueSpecs) worksheet.Cell(rowIndex, columnIndex++).Value = valueSpec.Label;

        rowIndex = 2;
        foreach (var group in grouped)
        {
            columnIndex = 1;
            var firstRecord = group.First();
            foreach (var rowKeyValue in firstRecord.RowKeyValues) worksheet.Cell(rowIndex, columnIndex++).Value = rowKeyValue;
            for (var valueIndex = 0; valueIndex < valueSpecs.Count; valueIndex++)
            {
                var aggregate = ComputeAggregate(group.Select(record => record.Measures[valueIndex]), valueSpecs[valueIndex].Summary);
                WriteAggregateValue(worksheet.Cell(rowIndex, columnIndex++), aggregate, valueSpecs[valueIndex].NumberFormat);
            }
            rowIndex++;
        }

        if (includeGrandTotal)
        {
            worksheet.Cell(rowIndex, 1).Value = "Grand Total";
            var writeColumn = rowFields.Count + 1;
            for (var valueIndex = 0; valueIndex < valueSpecs.Count; valueIndex++)
            {
                var aggregate = ComputeAggregate(records.Select(record => record.Measures[valueIndex]), valueSpecs[valueIndex].Summary);
                WriteAggregateValue(worksheet.Cell(rowIndex, writeColumn++), aggregate, valueSpecs[valueIndex].NumberFormat);
            }
        }

        return worksheet.Range(1, 1, Math.Max(rowIndex, 2), rowFields.Count + valueSpecs.Count);
    }

    private static IXLRange WritePivotMatrixReport(
        IXLWorksheet worksheet,
        IReadOnlyList<PivotFieldSpec> rowFields,
        PivotFieldSpec? columnField,
        IReadOnlyList<PivotValueSpec> valueSpecs,
        IReadOnlyList<PivotRecord> records,
        bool includeGrandTotal)
    {
        var columnValues = records
            .Select(record => record.ColumnValue ?? "(blank)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var grouped = records
            .GroupBy(record => string.Join('\u001F', record.RowKeyValues), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rowIndex = 1;
        var columnIndex = 1;
        foreach (var field in rowFields) worksheet.Cell(rowIndex, columnIndex++).Value = field.Header;
        foreach (var columnValue in columnValues)
        {
            foreach (var valueSpec in valueSpecs)
                worksheet.Cell(rowIndex, columnIndex++).Value = $"{columnValue} - {valueSpec.Label}";
        }
        if (includeGrandTotal)
        {
            foreach (var valueSpec in valueSpecs)
                worksheet.Cell(rowIndex, columnIndex++).Value = $"Grand Total - {valueSpec.Label}";
        }

        rowIndex = 2;
        foreach (var group in grouped)
        {
            columnIndex = 1;
            var firstRecord = group.First();
            foreach (var rowKeyValue in firstRecord.RowKeyValues) worksheet.Cell(rowIndex, columnIndex++).Value = rowKeyValue;

            foreach (var columnValue in columnValues)
            {
                var columnGroup = group.Where(record => string.Equals(record.ColumnValue ?? "(blank)", columnValue, StringComparison.OrdinalIgnoreCase)).ToList();
                for (var valueIndex = 0; valueIndex < valueSpecs.Count; valueIndex++)
                {
                    var aggregate = ComputeAggregate(columnGroup.Select(record => record.Measures[valueIndex]), valueSpecs[valueIndex].Summary);
                    WriteAggregateValue(worksheet.Cell(rowIndex, columnIndex++), aggregate, valueSpecs[valueIndex].NumberFormat);
                }
            }

            if (includeGrandTotal)
            {
                for (var valueIndex = 0; valueIndex < valueSpecs.Count; valueIndex++)
                {
                    var aggregate = ComputeAggregate(group.Select(record => record.Measures[valueIndex]), valueSpecs[valueIndex].Summary);
                    WriteAggregateValue(worksheet.Cell(rowIndex, columnIndex++), aggregate, valueSpecs[valueIndex].NumberFormat);
                }
            }

            rowIndex++;
        }

        return worksheet.Range(1, 1, Math.Max(rowIndex, 2), worksheet.LastColumnUsed()?.ColumnNumber() ?? 1);
    }

    private static object? ComputeAggregate(IEnumerable<PivotMeasureInput> inputs, string summary)
    {
        var materialized = inputs.ToList();
        return summary switch
        {
            "count" => materialized.Count(input => input.HasValue),
            "average" => materialized.Where(input => input.NumericValue.HasValue).Select(input => input.NumericValue!.Value).DefaultIfEmpty().Average(),
            "min" => materialized.Where(input => input.NumericValue.HasValue).Select(input => input.NumericValue!.Value).DefaultIfEmpty().Min(),
            "max" => materialized.Where(input => input.NumericValue.HasValue).Select(input => input.NumericValue!.Value).DefaultIfEmpty().Max(),
            _ => materialized.Where(input => input.NumericValue.HasValue).Select(input => input.NumericValue!.Value).Sum(),
        };
    }

    private static void WriteAggregateValue(IXLCell cell, object? value, string? numberFormat)
    {
        if (value is null)
            cell.Value = Blank.Value;
        else if (value is int intValue)
            cell.Value = intValue;
        else if (value is double doubleValue)
            cell.Value = doubleValue;
        else if (value is float floatValue)
            cell.Value = floatValue;
        else if (value is decimal decimalValue)
            cell.Value = decimalValue;
        else if (value is string stringValue)
            cell.Value = stringValue;
        else if (value is bool boolValue)
            cell.Value = boolValue;
        else
            cell.Value = value.ToString();

        if (!string.IsNullOrWhiteSpace(numberFormat))
            cell.Style.NumberFormat.Format = numberFormat;
    }

    private static void StylePivotReport(IXLRange range)
    {
        var worksheet = range.Worksheet;
        var headerRow = worksheet.Row(range.FirstRow().RowNumber());
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E2F3");
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.SetAutoFilter();
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns(range.FirstColumn().ColumnNumber(), range.LastColumn().ColumnNumber()).AdjustToContents();
    }

    private static XLCalculateMode ParseCalculationMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "manual" => XLCalculateMode.Manual,
            "autonotable" => XLCalculateMode.AutoNoTable,
            _ => XLCalculateMode.Auto,
        };

    private static List<string> ReadStringArray(JsonElement values)
        => values.EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

    private static List<NativePivotValueSpec> ParseNativePivotValueSpecs(JsonElement values)
    {
        var result = new List<NativePivotValueSpec>();
        foreach (var valueSpecEl in values.EnumerateArray())
        {
            var field = valueSpecEl.TryGetProperty("field", out var fieldEl) ? fieldEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(field)) continue;
            var summary = valueSpecEl.TryGetProperty("summary", out var summaryEl) && summaryEl.GetString() is { Length: > 0 } summaryText
                ? summaryText
                : "sum";
            var label = valueSpecEl.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;
            var numberFormat = valueSpecEl.TryGetProperty("numberFormat", out var numberFormatEl) ? numberFormatEl.GetString() : null;
            result.Add(new NativePivotValueSpec(field!, summary, label, numberFormat));
        }

        return result;
    }

    private static XLPivotSummary ParsePivotSummary(string summary)
        => summary.Trim().ToLowerInvariant() switch
        {
            "count" => XLPivotSummary.Count,
            "average" => XLPivotSummary.Average,
            "min" => XLPivotSummary.Minimum,
            "max" => XLPivotSummary.Maximum,
            _ => XLPivotSummary.Sum,
        };

    private static object? ConvertCellValue(XLCellValue value)
    {
        if (value.IsBlank) return null;
        if (value.IsBoolean) return value.GetBoolean();
        if (value.IsNumber) return value.GetNumber();
        if (value.IsDateTime) return value.GetDateTime().ToString("yyyy-MM-dd");
        if (value.IsTimeSpan) return value.GetTimeSpan().ToString(@"hh\:mm\:ss");
        if (value.IsError) return $"#{value.GetError()}";
        return value.GetText();
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

    private static IXLRange ResolveNamedRange(XLWorkbook wb, string name)
    {
        var namedRange = wb.NamedRanges.FirstOrDefault(nr =>
            string.Equals(nr.Name, name, StringComparison.OrdinalIgnoreCase));
        if (namedRange is null)
            throw new ArgumentException($"Named range '{name}' not found.");

        var ranges = namedRange.Ranges.ToList();
        if (ranges.Count == 0)
            throw new ArgumentException($"Named range '{name}' has no cells.");
        if (ranges.Count > 1)
            throw new ArgumentException($"Named range '{name}' must refer to a single contiguous range.");
        return ranges[0];
    }

    private sealed record ExcelChartSeriesSpec(string? Name, string? NameCell, string ValuesRange, string? ChartType, bool SecondaryAxis, string? XValuesRange, string? Color);

    private sealed record ExcelChartOptions(bool ShowLegend, string LegendPosition, bool ShowDataLabels, string? CategoryAxisTitle, string? ValueAxisTitle, string? SecondaryValueAxisTitle);

    private sealed record NativePivotValueSpec(string Field, string Summary, string? Label, string? NumberFormat);

    private sealed record PivotFieldSpec(string OriginalField, int RelativeColumnIndex, string Header);

    private sealed record PivotValueSpec(string Field, int RelativeColumnIndex, string Header, string Summary, string Label, string? NumberFormat);

    private readonly record struct PivotMeasureInput(bool HasValue, double? NumericValue);

    private sealed record PivotRecord(string[] RowKeyValues, string? ColumnValue, IReadOnlyList<PivotMeasureInput> Measures);
}
