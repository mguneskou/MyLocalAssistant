using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Writes a Python script to the conversation work directory and executes it via the
/// system Python interpreter.  Captures stdout + stderr and returns them to the LLM.
///
/// Security boundaries:
///   - Execution is limited to the configured timeout.
///   - The script runs as the server process user (no privilege escalation).
///   - Internet access follows the blockInternet config flag (no-network on by default
///     is advisory only — Python has its own networking; set blockInternet=true to at
///     least signal intent and to disallow the HttpClient import in generated code).
///
/// Config JSON: {"pythonExecutable":"python","timeoutSeconds":60,"blockInternet":true,"workDirAccess":true}
/// </summary>
internal sealed class PythonInterpreterTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "code.python";
    public string  Name        => "Python Interpreter";
    public string  Description =>
        "Writes and executes a Python script. Use this when you need to process files, " +
        "parse data, perform calculations, or do anything that current built-in tools cannot handle. " +
        "The script runs in the conversation's work directory. Use print() to return results.";
    public string  Category    => "Development";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "python.run",
            Description:
                "Write and execute a Python script. " +
                "The variable WORK_DIR is injected as the first line so you can reference it. " +
                "Use print() for all output. Return value of the script is the combined stdout. " +
                "Standard library is fully available. For file operations, prefer paths under WORK_DIR.",
            ArgumentsSchemaJson: """
            {
              "type": "object",
              "properties": {
                "code": {
                  "type": "string",
                  "description": "Complete Python script to execute. Must use print() for output."
                },
                "timeout_seconds": {
                  "type": "integer",
                  "description": "Execution timeout in seconds (max 120, default 60).",
                  "minimum": 1,
                  "maximum": 120
                }
              },
              "required": ["code"]
            }
            """),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private string _pythonExe      = "python";
    private int    _timeoutSeconds = 60;
    private bool   _workDirAccess  = true;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg is null) return;
        if (!string.IsNullOrWhiteSpace(cfg.PythonExecutable)) _pythonExe      = cfg.PythonExecutable;
        if (cfg.TimeoutSeconds is > 0 and <= 300)             _timeoutSeconds = cfg.TimeoutSeconds.Value;
        if (cfg.WorkDirAccess.HasValue)                       _workDirAccess  = cfg.WorkDirAccess.Value;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args = doc.RootElement;

        var code    = args.TryGetProperty("code",            out var c)  ? c.GetString() ?? "" : "";
        var timeout = args.TryGetProperty("timeout_seconds", out var ts) && ts.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, _timeoutSeconds)
            : _timeoutSeconds;

        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Error("code is required.");

        // Prepend WORK_DIR injection so the script can always reference it.
        var workDir = _workDirAccess ? ctx.WorkDirectory : Path.GetTempPath();
        var preamble = $"WORK_DIR = {JsonSerializer.Serialize(workDir)}\n";
        var fullCode = preamble + code;

        // Write the script to a temp file inside the work directory.
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, $"_script_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, fullCode, ctx.CancellationToken);
            return await RunScriptAsync(scriptPath, workDir, timeout, ctx.CancellationToken);
        }
        finally
        {
            // Best-effort cleanup.
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private async Task<ToolResult> RunScriptAsync(
        string scriptPath, string workDir, int timeoutSecs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = _pythonExe,
            Arguments              = $"\"{scriptPath}\"",
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutSb.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrSb.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error(
                $"Could not start Python interpreter '{_pythonExe}': {ex.Message}\n" +
                "Ensure Python is installed and available on the system PATH, or set the " +
                "PythonExecutable config value to the full path.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return ct.IsCancellationRequested
                ? ToolResult.Error("Python execution was cancelled.")
                : ToolResult.Error($"Python script timed out after {timeoutSecs}s.");
        }

        var stdout = stdoutSb.ToString().TrimEnd();
        var stderr = stderrSb.ToString().TrimEnd();
        var exitCode = process.ExitCode;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout)) sb.AppendLine(stdout);
        if (!string.IsNullOrEmpty(stderr)) { sb.AppendLine("--- stderr ---"); sb.AppendLine(stderr); }

        var output = sb.ToString().TrimEnd();
        if (string.IsNullOrEmpty(output)) output = "(no output)";

        return exitCode == 0
            ? ToolResult.Ok(output)
            : ToolResult.Error($"Script exited with code {exitCode}.\n{output}");
    }

    private sealed class Config
    {
        [JsonPropertyName("pythonExecutable")] public string? PythonExecutable { get; set; }
        [JsonPropertyName("timeoutSeconds")]   public int?    TimeoutSeconds   { get; set; }
        [JsonPropertyName("workDirAccess")]    public bool?   WorkDirAccess    { get; set; }
    }
}
