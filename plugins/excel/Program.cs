using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using MyLocalAssistant.Shared.Plugins;

// Excel plug-in. Speaks JSON-RPC 2.0 (LSP-style framing) over stdin/stdout.
//
// Tools (see manifest.template.json for argument schemas):
//   read    : excel.list_sheets, excel.read_range, excel.read_table, excel.find,
//             excel.describe, excel.pivot, excel.evaluate
//   write   : excel.write_cells, excel.append_row, excel.create_workbook,
//             excel.set_format, excel.set_formula, excel.recalculate
//
// Path policy: every path argument must resolve under one of the allowed roots.
// Allowed roots come from (1) the per-skill admin config JSON's "allowedRoots"
// array, (2) the per-call workDirectory passed via the invoke RPC context,
// (3) the MLA_EXCEL_ALLOWED_ROOTS env var (semicolon-separated). If none are set,
// only the workDirectory is permitted.
//
// Writes are gated by config.allowWrites (default false).
//
// Output is bounded by config.maxRowsPerCall (default 5000) and
// config.maxCellBytes (default 25000) so the agent never receives megabytes of
// JSON. Excess rows are reported as { truncated: true, totalRows: N }.

await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();

PluginConfig config = PluginConfig.Default;
var ct = CancellationToken.None;

while (true)
{
    byte[]? frame;
    try { frame = await JsonRpcFraming.ReadFrameAsync(stdin, ct); }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync("[excel] read error: " + ex.Message);
        return 1;
    }
    if (frame is null) return 0;

    RpcRequest? req;
    try { req = JsonSerializer.Deserialize<RpcRequest>(frame, JsonRpcFraming.Json); }
    catch (Exception ex)
    {
        await WriteErrorAsync(stdout, null, -32700, "Parse error: " + ex.Message);
        continue;
    }
    if (req is null || string.IsNullOrEmpty(req.Method))
    {
        await WriteErrorAsync(stdout, req?.Id, -32600, "Invalid Request");
        continue;
    }

    try
    {
        switch (req.Method)
        {
            case "initialize":
                config = PluginConfig.From(req.Params);
                await WriteResultAsync(stdout, req.Id, new { ok = true, name = "excel", version = "1.0.0" });
                break;
            case "invoke":
                var resp = HandleInvoke(req.Params, config);
                await WriteResultAsync(stdout, req.Id, resp);
                break;
            case "shutdown":
                await WriteResultAsync(stdout, req.Id, new { ok = true });
                return 0;
            default:
                await WriteErrorAsync(stdout, req.Id, -32601, $"Method not found: {req.Method}");
                break;
        }
    }
    catch (Exception ex)
    {
        await WriteErrorAsync(stdout, req.Id, -32603, "Internal error: " + ex.Message);
    }
}

static object HandleInvoke(JsonElement? p, PluginConfig config)
{
    if (p is null || p.Value.ValueKind != JsonValueKind.Object)
        return Toolbox.Err("Invalid params: expected object.");

    var tool = p.Value.TryGetProperty("tool", out var tEl) ? tEl.GetString() ?? "" : "";
    var args = p.Value.TryGetProperty("arguments", out var aEl) ? aEl : default;
    var ctx  = p.Value.TryGetProperty("context", out var cEl) ? cEl : default;

    var workDir = ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty("workDirectory", out var wd)
        ? wd.GetString() ?? ""
        : "";

    var policy = new PathPolicy(config, workDir);

    try
    {
        return tool switch
        {
            "excel.list_sheets"    => ExcelTools.ListSheets(args, policy),
            "excel.read_range"     => ExcelTools.ReadRange(args, policy, config),
            "excel.read_table"     => ExcelTools.ReadTable(args, policy, config),
            "excel.find"           => ExcelTools.Find(args, policy, config),
            "excel.describe"       => ExcelTools.Describe(args, policy, config),
            "excel.pivot"          => ExcelTools.Pivot(args, policy, config),
            "excel.write_cells"    => ExcelTools.WriteCells(args, policy, config),
            "excel.append_row"     => ExcelTools.AppendRow(args, policy, config),
            "excel.create_workbook"=> ExcelTools.CreateWorkbook(args, policy, config),
            "excel.set_format"     => ExcelTools.SetFormat(args, policy, config),
            "excel.set_formula"    => ExcelTools.SetFormula(args, policy, config),
            "excel.recalculate"    => ExcelTools.Recalculate(args, policy, config),
            "excel.evaluate"       => ExcelTools.Evaluate(args, policy),
            _                      => Toolbox.Err($"Unknown tool '{tool}'."),
        };
    }
    catch (PluginException ex) { return Toolbox.Err(ex.Message); }
    catch (Exception ex)       { return Toolbox.Err("Internal error: " + ex.Message); }
}

static async Task WriteResultAsync(Stream stdout, long? id, object result)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonRpcFraming.Json);
    using var doc = JsonDocument.Parse(bytes);
    var resp = new RpcResponse { Id = id, Result = doc.RootElement.Clone() };
    await JsonRpcFraming.WriteFrameAsync(stdout, resp, CancellationToken.None);
}

static async Task WriteErrorAsync(Stream stdout, long? id, int code, string message)
{
    var resp = new RpcResponse { Id = id, Error = new RpcError { Code = code, Message = message } };
    await JsonRpcFraming.WriteFrameAsync(stdout, resp, CancellationToken.None);
}

// ============================================================================

internal sealed class PluginConfig
{
    public IReadOnlyList<string> AllowedRoots { get; init; } = Array.Empty<string>();
    public int MaxRowsPerCall { get; init; } = 5000;
    public int MaxCellBytes   { get; init; } = 25_000;
    public bool AllowWrites   { get; init; } = false;

    public static PluginConfig Default { get; } = new();

    public static PluginConfig From(JsonElement? initParams)
    {
        // initialize params: { skillId, version, configJson }
        var config = Default;
        if (initParams is null || initParams.Value.ValueKind != JsonValueKind.Object) return config;
        if (!initParams.Value.TryGetProperty("configJson", out var cj) || cj.ValueKind != JsonValueKind.String) return config;
        var raw = cj.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return config;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var roots = new List<string>();
            if (root.TryGetProperty("allowedRoots", out var ar) && ar.ValueKind == JsonValueKind.Array)
                foreach (var s in ar.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                        roots.Add(s.GetString()!);
            // Merge env-supplied roots.
            var env = Environment.GetEnvironmentVariable("MLA_EXCEL_ALLOWED_ROOTS");
            if (!string.IsNullOrWhiteSpace(env))
                foreach (var part in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    roots.Add(part);

            return new PluginConfig
            {
                AllowedRoots = roots,
                MaxRowsPerCall = root.TryGetProperty("maxRowsPerCall", out var mr) && mr.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(mr.GetInt32(), 1, 200_000) : 5000,
                MaxCellBytes = root.TryGetProperty("maxCellBytes", out var mc) && mc.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(mc.GetInt32(), 1024, 1_000_000) : 25_000,
                AllowWrites = root.TryGetProperty("allowWrites", out var aw) && aw.ValueKind == JsonValueKind.True,
            };
        }
        catch
        {
            return Default;
        }
    }
}

internal sealed class PathPolicy
{
    private readonly List<string> _roots = new();

    public PathPolicy(PluginConfig config, string workDirectory)
    {
        foreach (var r in config.AllowedRoots) Add(r);
        if (!string.IsNullOrEmpty(workDirectory)) Add(workDirectory);
    }

    private void Add(string root)
    {
        try { _roots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar); }
        catch { /* ignore unresolvable */ }
    }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PluginException("Path is required.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch { throw new PluginException("path_invalid: " + path); }

        if (_roots.Count == 0)
            throw new PluginException("path_not_allowed: no allowed roots configured. Ask your admin to set 'allowedRoots' in the Excel plug-in config.");

        foreach (var root in _roots)
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full;

        throw new PluginException($"path_not_allowed: {full} is not under any configured allowed root.");
    }
}

internal sealed class PluginException : Exception
{
    public PluginException(string message) : base(message) { }
}

// ============================================================================

internal static class Toolbox
{
    public static object Ok(string content, object? structured = null)
        => structured is null
            ? new { isError = false, content }
            : new { isError = false, content, structured };

    public static object Err(string content) => new { isError = true, content };

    public static string GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    public static string? GetOptString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static int GetInt(JsonElement obj, string name, int defaultValue)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : defaultValue;

    public static bool GetBool(JsonElement obj, string name, bool defaultValue)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    public static IXLWorksheet ResolveSheet(IXLWorkbook wb, string? sheet)
    {
        if (!string.IsNullOrWhiteSpace(sheet))
        {
            if (!wb.TryGetWorksheet(sheet, out var ws))
                throw new PluginException($"sheet_not_found: '{sheet}'. Available: {string.Join(", ", wb.Worksheets.Select(w => w.Name))}");
            return ws;
        }
        var first = wb.Worksheets.FirstOrDefault();
        return first ?? throw new PluginException("workbook_has_no_sheets");
    }

    /// <summary>Convert a cell to a JSON-friendly value (string, double, bool, ISO date, or null).</summary>
    public static object? CellValue(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsBlank) return null;
        if (v.IsBoolean) return v.GetBoolean();
        if (v.IsDateTime) return v.GetDateTime().ToString("o", CultureInfo.InvariantCulture);
        if (v.IsTimeSpan) return v.GetTimeSpan().ToString();
        if (v.IsNumber) return v.GetNumber();
        if (v.IsError) return "#" + v.GetError();
        if (v.IsText) return v.GetText();
        return cell.GetFormattedString();
    }
}

// ============================================================================

internal static class ExcelTools
{
    private static readonly LoadOptions s_load = new() { RecalculateAllFormulas = false };

    private static IXLWorkbook OpenRead(string fullPath)
    {
        if (!File.Exists(fullPath)) throw new PluginException("file_not_found: " + fullPath);
        try { return new XLWorkbook(fullPath, s_load); }
        catch (Exception ex) { throw new PluginException("open_failed: " + ex.Message); }
    }

    private static IXLWorkbook OpenWrite(string fullPath, PluginConfig config)
    {
        if (!config.AllowWrites)
            throw new PluginException("writes_disabled: ask your admin to set 'allowWrites': true in the Excel plug-in config.");
        return OpenRead(fullPath);
    }

    private static void SaveQuiet(IXLWorkbook wb, string path)
    {
        try { wb.Save(); }
        catch (Exception ex) { throw new PluginException("save_failed (" + path + "): " + ex.Message); }
    }

    // ---- read tools --------------------------------------------------------

    public static object ListSheets(JsonElement args, PathPolicy policy)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        using var wb = OpenRead(path);
        var sheets = wb.Worksheets.Select(ws =>
        {
            var used = ws.RangeUsed();
            return new
            {
                name = ws.Name,
                rows = used?.RowCount() ?? 0,
                columns = used?.ColumnCount() ?? 0,
                firstCell = used?.FirstCell().Address.ToString(),
                lastCell = used?.LastCell().Address.ToString(),
            };
        }).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Workbook has {sheets.Count} sheet(s):");
        foreach (var s in sheets)
            sb.AppendLine($"  {s.name}: {s.rows} rows x {s.columns} cols ({s.firstCell ?? "empty"}..{s.lastCell ?? "empty"})");
        return Toolbox.Ok(sb.ToString().TrimEnd(), new { sheets });
    }

    public static object ReadRange(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetOptString(args, "sheet");
        var rangeStr = Toolbox.GetString(args, "range");
        if (string.IsNullOrWhiteSpace(rangeStr)) throw new PluginException("range is required.");
        var includeFormulas = Toolbox.GetBool(args, "includeFormulas", false);

        using var wb = OpenRead(path);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        IXLRange range;
        try { range = ws.Range(rangeStr); }
        catch { throw new PluginException("invalid_range: " + rangeStr); }

        var totalCells = range.RowCount() * range.ColumnCount();
        var maxCells = Math.Max(64, config.MaxRowsPerCall * 50);
        var truncated = totalCells > maxCells;

        var rows = new List<List<object?>>(range.RowCount());
        int taken = 0;
        foreach (var row in range.Rows())
        {
            var rowOut = new List<object?>(range.ColumnCount());
            foreach (var cell in row.Cells())
            {
                if (includeFormulas)
                {
                    rowOut.Add(new
                    {
                        value = Toolbox.CellValue(cell),
                        formula = cell.HasFormula ? "=" + cell.FormulaA1 : null,
                    });
                }
                else
                {
                    rowOut.Add(Toolbox.CellValue(cell));
                }
                taken++;
                if (taken >= maxCells) break;
            }
            rows.Add(rowOut);
            if (taken >= maxCells) break;
        }

        var preview = SummariseGrid(rows);
        return Toolbox.Ok(
            $"{ws.Name}!{rangeStr}: {rows.Count} row(s) x {(rows.Count > 0 ? rows[0].Count : 0)} col(s){(truncated ? " (truncated)" : "")}\n\n{preview}",
            new { sheet = ws.Name, range = rangeStr, rows, truncated, totalCells });
    }

    public static object ReadTable(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetOptString(args, "sheet");
        var tableName = Toolbox.GetOptString(args, "table");
        var limit = Math.Min(Toolbox.GetInt(args, "limit", 500), config.MaxRowsPerCall);

        using var wb = OpenRead(path);
        var ws = Toolbox.ResolveSheet(wb, sheetName);

        IXLTable? table = null;
        if (!string.IsNullOrEmpty(tableName))
        {
            table = wb.Worksheets.SelectMany(s => s.Tables).FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null) throw new PluginException($"table_not_found: '{tableName}'");
        }
        else
        {
            table = ws.Tables.FirstOrDefault();
        }

        IXLRange dataRange;
        IReadOnlyList<string> headers;
        if (table is not null)
        {
            dataRange = table.DataRange;
            headers = table.Fields.Select(f => f.Name).ToList();
        }
        else
        {
            var used = ws.RangeUsed() ?? throw new PluginException("sheet_is_empty");
            // Auto-detect: first row = headers.
            headers = used.FirstRow().Cells().Select(c => c.GetFormattedString()).ToList();
            dataRange = SubRangeBelowFirstRow(used);
        }

        var rows = new List<Dictionary<string, object?>>();
        int total = dataRange.RowCount();
        foreach (var row in dataRange.Rows())
        {
            if (rows.Count >= limit) break;
            var dict = new Dictionary<string, object?>(headers.Count);
            int i = 0;
            foreach (var cell in row.Cells())
            {
                var key = i < headers.Count ? headers[i] : $"col{i}";
                dict[key] = Toolbox.CellValue(cell);
                i++;
            }
            rows.Add(dict);
        }

        var preview = SummariseRows(headers, rows);
        var truncated = total > rows.Count;
        return Toolbox.Ok(
            $"{ws.Name}: {rows.Count} of {total} row(s){(truncated ? " (truncated)" : "")} · columns: {string.Join(", ", headers)}\n\n{preview}",
            new { sheet = ws.Name, headers, rows, totalRows = total, truncated });
    }

    public static object Find(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var query = Toolbox.GetString(args, "query");
        var sheetName = Toolbox.GetOptString(args, "sheet");
        var limit = Math.Min(Toolbox.GetInt(args, "limit", 50), 1000);
        if (string.IsNullOrEmpty(query)) throw new PluginException("query is required.");

        using var wb = OpenRead(path);
        var sheets = string.IsNullOrEmpty(sheetName)
            ? (IEnumerable<IXLWorksheet>)wb.Worksheets
            : new[] { Toolbox.ResolveSheet(wb, sheetName) };

        var matches = new List<object>();
        var sb = new StringBuilder();
        foreach (var ws in sheets)
        {
            var used = ws.RangeUsed();
            if (used is null) continue;
            foreach (var cell in used.Cells())
            {
                var s = cell.GetFormattedString();
                if (string.IsNullOrEmpty(s)) continue;
                if (s.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var addr = cell.Address.ToString();
                matches.Add(new { sheet = ws.Name, address = addr, value = s });
                sb.AppendLine($"  {ws.Name}!{addr}: {s}");
                if (matches.Count >= limit) break;
            }
            if (matches.Count >= limit) break;
        }
        var head = matches.Count == 0 ? $"No matches for '{query}'." : $"Found {matches.Count} match(es) for '{query}':";
        return Toolbox.Ok(head + (matches.Count > 0 ? "\n" + sb.ToString().TrimEnd() : ""), new { matches });
    }

    public static object Describe(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetOptString(args, "sheet");
        var rangeStr = Toolbox.GetOptString(args, "range");
        var hasHeaders = Toolbox.GetBool(args, "hasHeaders", true);

        using var wb = OpenRead(path);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        IXLRange range;
        if (!string.IsNullOrEmpty(rangeStr))
        {
            try { range = ws.Range(rangeStr); }
            catch { throw new PluginException("invalid_range: " + rangeStr); }
        }
        else
        {
            range = ws.RangeUsed() ?? throw new PluginException("sheet_is_empty");
        }

        var cols = range.ColumnCount();
        var headers = new List<string>(cols);
        IXLRange dataRange;
        if (hasHeaders)
        {
            for (int c = 1; c <= cols; c++) headers.Add(range.Cell(1, c).GetFormattedString());
            dataRange = range.RowCount() <= 1
                ? ws.Range(range.FirstCell().Address, range.FirstCell().Address) // empty range placeholder
                : SubRangeBelowFirstRow(range);
        }
        else
        {
            for (int c = 1; c <= cols; c++) headers.Add($"col{c}");
            dataRange = range;
        }

        var summaries = new List<object>(cols);
        var sb = new StringBuilder();
        sb.AppendLine($"{ws.Name}: {dataRange.RowCount()} row(s) x {cols} col(s)");
        for (int c = 1; c <= cols; c++)
        {
            var header = headers[c - 1];
            var cells = dataRange.Column(c).Cells();
            int total = 0, nulls = 0;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var nums = new List<double>();
            var textCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in cells)
            {
                total++;
                var v = cell.Value;
                if (v.IsBlank) { nulls++; continue; }
                if (v.IsNumber) nums.Add(v.GetNumber());
                var s = cell.GetFormattedString();
                distinct.Add(s);
                if (!v.IsNumber)
                {
                    textCounts.TryGetValue(s, out var k);
                    textCounts[s] = k + 1;
                }
            }

            object summary;
            if (nums.Count > 0 && nums.Count >= total - nulls)
            {
                nums.Sort();
                double median = nums.Count % 2 == 1 ? nums[nums.Count / 2] : (nums[nums.Count / 2 - 1] + nums[nums.Count / 2]) / 2.0;
                summary = new
                {
                    column = header, type = "number",
                    count = total, nulls, distinct = distinct.Count,
                    min = nums[0], max = nums[^1],
                    mean = nums.Average(), median,
                };
                sb.AppendLine($"  {header}: number  count={total} nulls={nulls} distinct={distinct.Count}  min={nums[0]:G} max={nums[^1]:G} mean={nums.Average():G} median={median:G}");
            }
            else
            {
                var top = textCounts.OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => new { value = kv.Key, count = kv.Value }).ToList();
                summary = new
                {
                    column = header, type = "text",
                    count = total, nulls, distinct = distinct.Count,
                    top,
                };
                sb.AppendLine($"  {header}: text   count={total} nulls={nulls} distinct={distinct.Count}  top={string.Join(", ", top.Select(t => $"{t.value}({t.count})"))}");
            }
            summaries.Add(summary);
        }
        return Toolbox.Ok(sb.ToString().TrimEnd(), new { sheet = ws.Name, columns = summaries });
    }

    public static object Pivot(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetOptString(args, "sheet");
        var rangeStr = Toolbox.GetOptString(args, "range");
        var groupBy = Toolbox.GetString(args, "groupBy");
        var valueCol = Toolbox.GetOptString(args, "value");
        var agg = (Toolbox.GetOptString(args, "agg") ?? "sum").ToLowerInvariant();
        var limit = Math.Min(Toolbox.GetInt(args, "limit", 50), 1000);
        if (string.IsNullOrEmpty(groupBy)) throw new PluginException("groupBy is required.");
        if (agg != "count" && string.IsNullOrEmpty(valueCol)) throw new PluginException("value is required for agg=" + agg);

        using var wb = OpenRead(path);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        IXLRange range = !string.IsNullOrEmpty(rangeStr) ? ws.Range(rangeStr)
                       : (ws.RangeUsed() ?? throw new PluginException("sheet_is_empty"));
        var headers = range.FirstRow().Cells().Select(c => c.GetFormattedString()).ToList();
        var groupIdx = headers.FindIndex(h => string.Equals(h, groupBy, StringComparison.OrdinalIgnoreCase));
        if (groupIdx < 0) throw new PluginException($"groupBy_column_not_found: '{groupBy}'. Available: {string.Join(", ", headers)}");
        int valueIdx = -1;
        if (!string.IsNullOrEmpty(valueCol))
        {
            valueIdx = headers.FindIndex(h => string.Equals(h, valueCol, StringComparison.OrdinalIgnoreCase));
            if (valueIdx < 0) throw new PluginException($"value_column_not_found: '{valueCol}'");
        }

        var dataRange = SubRangeBelowFirstRow(range);
        var bucket = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dataRange.Rows())
        {
            var cells = row.Cells().ToList();
            if (cells.Count <= groupIdx) continue;
            var key = cells[groupIdx].GetFormattedString();
            if (!bucket.TryGetValue(key, out var list)) bucket[key] = list = new List<double>();
            if (valueIdx >= 0 && cells.Count > valueIdx && cells[valueIdx].Value.IsNumber)
                list.Add(cells[valueIdx].Value.GetNumber());
            else if (agg == "count")
                list.Add(1);
        }

        var aggregated = bucket.Select(kv =>
        {
            double v = agg switch
            {
                "sum"   => kv.Value.Sum(),
                "avg"   => kv.Value.Count == 0 ? 0 : kv.Value.Average(),
                "min"   => kv.Value.Count == 0 ? 0 : kv.Value.Min(),
                "max"   => kv.Value.Count == 0 ? 0 : kv.Value.Max(),
                "count" => kv.Value.Count,
                _       => kv.Value.Sum(),
            };
            return new { key = kv.Key, value = v, samples = kv.Value.Count };
        }).OrderByDescending(r => r.value).Take(limit).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Pivot {agg}({valueCol ?? "*"}) by {groupBy}:");
        foreach (var r in aggregated) sb.AppendLine($"  {r.key}: {r.value:G} (n={r.samples})");
        return Toolbox.Ok(sb.ToString().TrimEnd(), new { groupBy, value = valueCol, agg, rows = aggregated });
    }

    // ---- write tools -------------------------------------------------------

    public static object WriteCells(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetString(args, "sheet");
        var rangeStr = Toolbox.GetString(args, "range");
        if (!args.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new PluginException("values must be a 2-D array.");

        using var wb = OpenWrite(path, config);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        IXLCell topLeft;
        try { topLeft = ws.Range(rangeStr).FirstCell(); }
        catch { topLeft = ws.Cell(rangeStr); }
        var startRow = topLeft.Address.RowNumber;
        var startCol = topLeft.Address.ColumnNumber;

        int rowIdx = 0, written = 0;
        foreach (var row in values.EnumerateArray())
        {
            int colIdx = 0;
            foreach (var v in row.EnumerateArray())
            {
                var cell = ws.Cell(startRow + rowIdx, startCol + colIdx);
                ApplyValue(cell, v);
                written++;
                colIdx++;
            }
            rowIdx++;
        }
        SaveQuiet(wb, path);
        return Toolbox.Ok($"Wrote {written} cell(s) starting at {ws.Name}!{topLeft.Address}.",
            new { sheet = ws.Name, startCell = topLeft.Address.ToString(), cells = written });
    }

    public static object AppendRow(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetString(args, "sheet");
        if (!args.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new PluginException("values must be an array.");

        using var wb = OpenWrite(path, config);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var rowNum = lastRow + 1;
        int colIdx = 0;
        foreach (var v in values.EnumerateArray())
        {
            ApplyValue(ws.Cell(rowNum, colIdx + 1), v);
            colIdx++;
        }
        SaveQuiet(wb, path);
        return Toolbox.Ok($"Appended row {rowNum} on {ws.Name}.", new { sheet = ws.Name, row = rowNum, columns = colIdx });
    }

    public static object CreateWorkbook(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        if (!config.AllowWrites) throw new PluginException("writes_disabled");
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var overwrite = Toolbox.GetBool(args, "overwrite", false);
        if (File.Exists(path) && !overwrite) throw new PluginException("file_exists: " + path + " (pass overwrite=true to replace)");

        var sheets = new List<string>();
        if (args.TryGetProperty("sheets", out var sh) && sh.ValueKind == JsonValueKind.Array)
            foreach (var s in sh.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    sheets.Add(s.GetString()!);
        if (sheets.Count == 0) sheets.Add("Sheet1");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wb = new XLWorkbook();
        foreach (var name in sheets) wb.Worksheets.Add(name);
        try { wb.SaveAs(path); }
        catch (Exception ex) { throw new PluginException("save_failed: " + ex.Message); }
        return Toolbox.Ok($"Created {path} with sheet(s): {string.Join(", ", sheets)}.",
            new { path, sheets });
    }

    public static object SetFormat(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetString(args, "sheet");
        var rangeStr = Toolbox.GetString(args, "range");
        var numberFormat = Toolbox.GetOptString(args, "numberFormat");
        var bold = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("bold", out var b) && b.ValueKind == JsonValueKind.True;
        var fillColor = Toolbox.GetOptString(args, "fillColor");

        using var wb = OpenWrite(path, config);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        IXLRange range;
        try { range = ws.Range(rangeStr); }
        catch { throw new PluginException("invalid_range: " + rangeStr); }

        var style = range.Style;
        if (!string.IsNullOrEmpty(numberFormat)) style.NumberFormat.Format = numberFormat;
        if (bold) style.Font.Bold = true;
        if (!string.IsNullOrEmpty(fillColor))
        {
            try
            {
                var clean = fillColor.TrimStart('#');
                style.Fill.BackgroundColor = XLColor.FromHtml("#" + clean);
            }
            catch { throw new PluginException("invalid_fillColor: " + fillColor); }
        }
        SaveQuiet(wb, path);
        return Toolbox.Ok($"Formatted {ws.Name}!{rangeStr}.", new { sheet = ws.Name, range = rangeStr });
    }

    public static object SetFormula(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        var sheetName = Toolbox.GetString(args, "sheet");
        var cellAddr = Toolbox.GetString(args, "cell");
        var formula = Toolbox.GetString(args, "formula");
        if (string.IsNullOrEmpty(formula)) throw new PluginException("formula is required.");
        if (formula.StartsWith('=')) formula = formula[1..];

        using var wb = OpenWrite(path, config);
        var ws = Toolbox.ResolveSheet(wb, sheetName);
        var cell = ws.Cell(cellAddr);
        cell.FormulaA1 = formula;
        // Force evaluation now so we can report the result back to the model.
        object? value;
        try { value = Toolbox.CellValue(cell); }
        catch (Exception ex) { throw new PluginException("formula_error: " + ex.Message); }
        SaveQuiet(wb, path);
        return Toolbox.Ok($"{ws.Name}!{cellAddr} = {formula}  →  {value}",
            new { sheet = ws.Name, cell = cellAddr, formula = "=" + formula, value });
    }

    public static object Recalculate(JsonElement args, PathPolicy policy, PluginConfig config)
    {
        var path = policy.Resolve(Toolbox.GetString(args, "path"));
        using var wb = OpenWrite(path, config);
        try { wb.RecalculateAllFormulas(); }
        catch (Exception ex) { throw new PluginException("recalc_failed: " + ex.Message); }
        SaveQuiet(wb, path);
        var n = wb.Worksheets.Sum(ws => ws.CellsUsed(c => c.HasFormula).Count());
        return Toolbox.Ok($"Recalculated {n} formula cell(s).", new { formulasRecalculated = n });
    }

    public static object Evaluate(JsonElement args, PathPolicy policy)
    {
        var formula = Toolbox.GetString(args, "formula");
        if (string.IsNullOrEmpty(formula)) throw new PluginException("formula is required.");
        if (formula.StartsWith('=')) formula = formula[1..];

        var pathArg = Toolbox.GetOptString(args, "path");
        var sheetName = Toolbox.GetOptString(args, "sheet");

        XLWorkbook wb;
        bool ownsWorkbook = false;
        if (!string.IsNullOrEmpty(pathArg))
        {
            var path = policy.Resolve(pathArg);
            wb = (XLWorkbook)OpenRead(path);
            ownsWorkbook = true;
        }
        else
        {
            wb = new XLWorkbook();
            wb.Worksheets.Add("Sheet1");
            ownsWorkbook = true;
        }

        try
        {
            var ws = Toolbox.ResolveSheet(wb, sheetName);
            object? value;
            try
            {
                var raw = ws.Evaluate(formula);
                value = ConvertScalar(raw);
            }
            catch (Exception ex)
            {
                throw new PluginException("formula_error: " + ex.Message);
            }
            return Toolbox.Ok($"= {formula}  →  {value}", new { formula = "=" + formula, value });
        }
        finally
        {
            if (ownsWorkbook) wb.Dispose();
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Build a sub-range that excludes the first row. Caller must ensure RowCount >= 2.</summary>
    private static IXLRange SubRangeBelowFirstRow(IXLRange range)
    {
        var first = range.Cell(2, 1).Address;
        var last = range.LastCell().Address;
        return range.Worksheet.Range(first, last);
    }

    private static void ApplyValue(IXLCell cell, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                cell.Clear(XLClearOptions.Contents);
                break;
            case JsonValueKind.True:  cell.Value = true; break;
            case JsonValueKind.False: cell.Value = false; break;
            case JsonValueKind.Number:
                if (v.TryGetDouble(out var d)) cell.Value = d;
                break;
            case JsonValueKind.String:
                var s = v.GetString() ?? "";
                if (s.StartsWith('=')) cell.FormulaA1 = s[1..];
                else cell.Value = s;
                break;
            default:
                cell.Value = v.GetRawText();
                break;
        }
    }

    private static object? ConvertScalar(object? raw)
    {
        if (raw is null) return null;
        return raw switch
        {
            bool b => b,
            double d => d,
            int i => (double)i,
            long l => (double)l,
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            string s => s,
            _ => raw.ToString(),
        };
    }

    private static string SummariseGrid(IList<List<object?>> rows)
    {
        if (rows.Count == 0) return "(empty)";
        var sb = new StringBuilder();
        var preview = rows.Take(20);
        foreach (var r in preview)
            sb.AppendLine("  " + string.Join(" | ", r.Select(v => v?.ToString() ?? "")));
        if (rows.Count > 20) sb.AppendLine($"  … {rows.Count - 20} more row(s)");
        return sb.ToString().TrimEnd();
    }

    private static string SummariseRows(IReadOnlyList<string> headers, IList<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return "(empty)";
        var sb = new StringBuilder();
        sb.AppendLine("  " + string.Join(" | ", headers));
        foreach (var r in rows.Take(20))
            sb.AppendLine("  " + string.Join(" | ", headers.Select(h => r.TryGetValue(h, out var v) ? v?.ToString() ?? "" : "")));
        if (rows.Count > 20) sb.AppendLine($"  … {rows.Count - 20} more row(s)");
        return sb.ToString().TrimEnd();
    }
}
