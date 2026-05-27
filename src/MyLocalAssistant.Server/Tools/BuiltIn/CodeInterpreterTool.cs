using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Executes C# code snippets using Roslyn scripting.
/// Config JSON: {"timeoutSeconds":30,"memoryLimitMb":128,"blockInternet":true,"workDirAccess":true}
/// </summary>
internal sealed class CodeInterpreterTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "code.csharp";
    public string  Name        => "C# Code Interpreter";
    public string  Description => "Executes C# code snippets using Roslyn scripting. Shares the conversation work directory for file I/O.";
    public string  Category    => "Development";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "code.run",
            Description: "Execute C# code and return the output. The global variable `WorkDirectory` (string) gives the path to the conversation's writable folder. Use Console.WriteLine to output results. The final expression value is also returned.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"code":{"type":"string","description":"C# code to execute"},"timeout_seconds":{"type":"integer","description":"Execution timeout in seconds (max 120, default 30)","minimum":1,"maximum":120}},"required":["code"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private int  _timeoutSeconds  = 30;
    private int  _memoryLimitMb   = 128;
    private bool _blockInternet   = true;
    private bool _workDirAccess   = true;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private static readonly string[] s_baseAssemblies =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Linq",
        "System.Collections",
        "System.Text.Json",
        "System.Text.RegularExpressions",
        "System.Console",
    ];

    private static readonly string[] s_netAssemblies =
    [
        "System.Net.Http",
        "System.Net.Primitives",
        "System.Net.Sockets",
    ];

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg is null) return;
        if (cfg.TimeoutSeconds is > 0 and <= 300) _timeoutSeconds = cfg.TimeoutSeconds.Value;
        if (cfg.MemoryLimitMb  is > 0)            _memoryLimitMb  = cfg.MemoryLimitMb.Value;
        if (cfg.BlockInternet.HasValue)            _blockInternet  = cfg.BlockInternet.Value;
        if (cfg.WorkDirAccess.HasValue)            _workDirAccess  = cfg.WorkDirAccess.Value;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args    = doc.RootElement;
        var ct      = ctx.CancellationToken;

        var code    = args.TryGetProperty("code",            out var c)  ? c.GetString() ?? ""  : "";
        var timeout = args.TryGetProperty("timeout_seconds", out var ts) && ts.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, _timeoutSeconds)
            : _timeoutSeconds;

        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Error("code is required");

        var outputSb  = new StringBuilder();
        var errorSb   = new StringBuilder();
        var oldOut    = Console.Out;
        var oldErr    = Console.Error;
        Console.SetOut(new StringWriter(outputSb));
        Console.SetError(new StringWriter(errorSb));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            var imports = new List<string>
            {
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.Text",
                "System.Text.Json",
                "System.Text.RegularExpressions",
                "System.IO",
                "System.Math",
                "System.Threading.Tasks",
            };
            if (!_blockInternet) imports.AddRange(["System.Net.Http", "System.Net"]);

            var refs    = LoadReferenceAssemblies(_blockInternet);
            var workDir = _workDirAccess ? ctx.WorkDirectory : "";
            var globals = new CodeScriptGlobals { WorkDirectory = workDir };

            var options = ScriptOptions.Default
                .WithImports(imports)
                .WithReferences(refs)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(false)
                .WithEmitDebugInformation(false);

            var script = CSharpScript.Create(code, options, typeof(CodeScriptGlobals));

            object? returnValue = null;
            Exception? scriptEx = null;
            try
            {
                var state = await script.RunAsync(globals, catchException: _ => true, cts.Token)
                    .ConfigureAwait(false);
                returnValue = state.ReturnValue;
                scriptEx    = state.Exception;
            }
            catch (CompilationErrorException cee)
            {
                var diag = string.Join("\n", cee.Diagnostics.Select(d => d.ToString()));
                return ToolResult.Error($"Compilation error:\n{diag}");
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Error($"Execution timed out after {timeout}s.");
            }

            var sb = new StringBuilder();
            if (outputSb.Length > 0)
            {
                sb.AppendLine("Output:");
                sb.AppendLine(outputSb.ToString().TrimEnd());
            }
            if (errorSb.Length > 0)
            {
                sb.AppendLine("Stderr:");
                sb.AppendLine(errorSb.ToString().TrimEnd());
            }
            if (scriptEx is not null)
            {
                sb.AppendLine($"Exception: {scriptEx.GetType().Name}: {scriptEx.Message}");
                return ToolResult.Error(sb.Length > 0 ? sb.ToString().TrimEnd() : scriptEx.Message);
            }
            if (returnValue is not null)
                sb.AppendLine($"Return value: {returnValue}");

            return sb.Length == 0
                ? ToolResult.Ok("(no output)")
                : ToolResult.Ok(sb.ToString().TrimEnd());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static IEnumerable<Assembly> LoadReferenceAssemblies(bool blockInternet)
    {
        var names = new List<string>(s_baseAssemblies);
        if (!blockInternet) names.AddRange(s_netAssemblies);
        var result = new List<Assembly>();
        foreach (var n in names)
        {
            try { result.Add(Assembly.Load(n)); }
            catch { /* skip unavailable */ }
        }
        result.Add(typeof(object).Assembly);
        result.Add(typeof(Enumerable).Assembly);
        result.Add(typeof(System.Text.StringBuilder).Assembly);
        result.Add(typeof(System.IO.Path).Assembly);
        return result.Distinct();
    }

    private sealed class Config
    {
        [JsonPropertyName("timeoutSeconds")] public int?  TimeoutSeconds { get; set; }
        [JsonPropertyName("memoryLimitMb")]  public int?  MemoryLimitMb  { get; set; }
        [JsonPropertyName("blockInternet")]  public bool? BlockInternet  { get; set; }
        [JsonPropertyName("workDirAccess")]  public bool? WorkDirAccess  { get; set; }
    }
}

/// <summary>Globals object injected into every script. Provides WorkDirectory access.</summary>
public sealed class CodeScriptGlobals
{
    /// <summary>Per-conversation working directory. Scripts may read/write files here.</summary>
    public string WorkDirectory { get; set; } = "";
}
