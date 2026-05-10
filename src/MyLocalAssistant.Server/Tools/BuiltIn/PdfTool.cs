using System.Text;
using System.Text.Json;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PigDoc = UglyToad.PdfPig.PdfDocument;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// PDF tool. Reads text from PDFs (PdfPig) and merges/splits/extracts pages (PdfSharpCore).
/// All files are scoped to the conversation WorkDirectory.
/// </summary>
internal sealed class PdfTool : ITool
{
    public string  Id          => "pdf";
    public string  Name        => "PDF Tool";
    public string  Description => "Read text from PDF files and perform page operations: merge multiple PDFs, split into individual pages, or extract a range of pages into a new file.";
    public string  Category    => "Productivity";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 8);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Tool definitions ──────────────────────────────────────────────────────

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "pdf.read",
            Description: "Extract text from a PDF file, optionally limited to a page range. Returns text per page.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"PDF filename in the work directory."},"fromPage":{"type":"integer","description":"First page to read (1-based). Default: 1."},"toPage":{"type":"integer","description":"Last page to read (1-based, inclusive). Default: last page."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "pdf.merge",
            Description: "Merge multiple PDF files into one. Provide an ordered list of input filenames and an output filename.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"inputs":{"type":"array","items":{"type":"string"},"description":"List of source PDF filenames (in merge order)."},"output":{"type":"string","description":"Output filename for the merged PDF."}},"required":["inputs","output"]}"""),

        new ToolFunctionDto(
            Name: "pdf.split",
            Description: "Split a PDF into individual single-page PDF files. Each output file is named <basename>_page_<N>.pdf.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string","description":"PDF to split."},"outputPrefix":{"type":"string","description":"Optional filename prefix. Defaults to the source filename without extension."}},"required":["filename"]}"""),

        new ToolFunctionDto(
            Name: "pdf.extract_pages",
            Description: "Extract a range of pages from a PDF into a new file.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"},"fromPage":{"type":"integer","description":"First page to extract (1-based)."},"toPage":{"type":"integer","description":"Last page to extract (1-based, inclusive)."},"output":{"type":"string","description":"Output filename for the extracted pages."}},"required":["filename","fromPage","toPage","output"]}"""),
    };

    public void Configure(string? configJson) { /* no per-instance config */ }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        return call.ToolName switch
        {
            "pdf.read"          => ReadAsync(call, ctx),
            "pdf.merge"         => MergeAsync(call, ctx),
            "pdf.split"         => SplitAsync(call, ctx),
            "pdf.extract_pages" => ExtractPagesAsync(call, ctx),
            _ => Task.FromResult(ToolResult.Error($"Unknown pdf tool: {call.ToolName}")),
        };
    }

    // ── pdf.read ─────────────────────────────────────────────────────────────

    private static Task<ToolResult> ReadAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        int fromPage = doc.TryGetProperty("fromPage", out var fpEl) && fpEl.TryGetInt32(out var fp) ? fp : 1;
        int toPage   = doc.TryGetProperty("toPage",   out var tpEl) && tpEl.TryGetInt32(out var tp) ? tp : int.MaxValue;

        try
        {
            using var pdf = PigDoc.Open(path);
            int totalPages = pdf.NumberOfPages;
            toPage = Math.Min(toPage, totalPages);
            fromPage = Math.Max(1, fromPage);

            var pages = new List<object>();
            for (int i = fromPage; i <= toPage; i++)
            {
                var page = pdf.GetPage(i);
                pages.Add(new { pageNumber = i, text = page.Text });
            }

            var result = JsonSerializer.Serialize(new
            {
                filename,
                totalPages,
                extractedPages = pages.Count,
                pages,
            }, s_json);
            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex) { return Err($"Read failed: {ex.Message}"); }
    }

    // ── pdf.merge ────────────────────────────────────────────────────────────

    private static Task<ToolResult> MergeAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("inputs", out var inputsEl) || inputsEl.ValueKind != JsonValueKind.Array)
            return Err("'inputs' must be an array of filenames.");
        if (!doc.TryGetProperty("output", out var outEl) || outEl.GetString() is not string output)
            return Err("'output' is required.");

        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        var outPath = SafePath(ctx.WorkDirectory, output);
        if (outPath is null) return Err("Invalid output filename.");

        var inputs = new List<string>();
        foreach (var el in inputsEl.EnumerateArray())
        {
            var fn = el.GetString();
            if (fn is null) return Err("Each input must be a filename string.");
            var p = SafePath(ctx.WorkDirectory, fn);
            if (p is null) return Err($"Invalid input filename: {fn}");
            if (!File.Exists(p)) return Err($"Input file not found: {fn}");
            inputs.Add(p);
        }
        if (inputs.Count == 0) return Err("No input files provided.");

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            using var outDoc = new PdfDocument();
            foreach (var inputPath in inputs)
            {
                using var inDoc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
                for (int i = 0; i < inDoc.PageCount; i++)
                    outDoc.AddPage(inDoc.Pages[i]);
            }
            outDoc.Save(outPath);
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { output, mergedFiles = inputs.Count, success = true }, s_json)));
        }
        catch (Exception ex) { return Err($"Merge failed: {ex.Message}"); }
    }

    // ── pdf.split ────────────────────────────────────────────────────────────

    private static Task<ToolResult> SplitAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");

        var prefix = doc.TryGetProperty("outputPrefix", out var pfEl) && pfEl.GetString() is { Length: > 0 } pf
            ? pf
            : Path.GetFileNameWithoutExtension(filename);

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            using var inDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            int count = inDoc.PageCount;
            var outputFiles = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var outName = $"{prefix}_page_{i + 1}.pdf";
                var outPath = SafePath(ctx.WorkDirectory, outName);
                if (outPath is null) continue;
                using var pageDoc = new PdfDocument();
                pageDoc.AddPage(inDoc.Pages[i]);
                pageDoc.Save(outPath);
                outputFiles.Add(outName);
            }
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { splitPages = count, outputFiles }, s_json)));
        }
        catch (Exception ex) { return Err($"Split failed: {ex.Message}"); }
    }

    // ── pdf.extract_pages ────────────────────────────────────────────────────

    private static Task<ToolResult> ExtractPagesAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!TryParse(call.ArgumentsJson, out var doc)) return Err("Arguments must be a JSON object.");
        if (!doc.TryGetProperty("filename", out var fnEl) || fnEl.GetString() is not string filename) return Err("'filename' is required.");
        if (!doc.TryGetProperty("fromPage", out var fpEl) || !fpEl.TryGetInt32(out var fromPage)) return Err("'fromPage' is required.");
        if (!doc.TryGetProperty("toPage",   out var tpEl) || !tpEl.TryGetInt32(out var toPage))   return Err("'toPage' is required.");
        if (!doc.TryGetProperty("output",   out var outEl) || outEl.GetString() is not string output) return Err("'output' is required.");

        var path = SafePath(ctx.WorkDirectory, filename);
        if (path is null) return Err("Invalid filename.");
        if (!File.Exists(path)) return Err($"File not found: {filename}");
        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        var outPath = SafePath(ctx.WorkDirectory, output);
        if (outPath is null) return Err("Invalid output filename.");

        try
        {
            Directory.CreateDirectory(ctx.WorkDirectory);
            using var inDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            int total = inDoc.PageCount;
            fromPage = Math.Max(1, fromPage);
            toPage   = Math.Min(toPage, total);
            if (fromPage > toPage) return Err($"fromPage ({fromPage}) > toPage ({toPage}).");

            using var outDoc = new PdfDocument();
            for (int i = fromPage - 1; i < toPage; i++)
                outDoc.AddPage(inDoc.Pages[i]);
            outDoc.Save(outPath);
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.Serialize(new { output, extractedPages = toPage - fromPage + 1, success = true }, s_json)));
        }
        catch (Exception ex) { return Err($"Extract pages failed: {ex.Message}"); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParse(string? json, out JsonElement el)
    {
        el = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { el = JsonDocument.Parse(json).RootElement; return el.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    private static string? SafePath(string workDir, string filename)
    {
        var name = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var full = Path.GetFullPath(Path.Combine(workDir, name));
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static Task<ToolResult> Err(string msg) =>
        Task.FromResult(ToolResult.Error(msg));
}
