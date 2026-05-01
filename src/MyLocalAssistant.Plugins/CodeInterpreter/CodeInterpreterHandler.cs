using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.CodeInterpreter;

/// <summary>
/// Executes C# code snippets using Roslyn scripting within this (already sandboxed) plugin process.
/// The plugin process runs inside the server's Windows Job Object — no Docker dependency needed.
/// Network-capable assemblies are excluded when blockInternet is true.
/// Config JSON: {"timeoutSeconds":30,"memoryLimitMb":128,"blockInternet":true,"workDirAccess":true}
/// </summary>
internal sealed class CodeInterpreterHandler : IPluginTool
{
    private int  _timeoutSeconds  = 30;
    private int  _memoryLimitMb   = 128;
    private bool _blockInternet   = true;
    private bool _workDirAccess   = true;

    // Assemblies always available to scripts.
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

    // Additional assemblies allowed when blockInternet is false.
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

    public async Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        var code    = arguments.TryGetProperty("code",            out var c)  ? c.GetString() ?? ""  : "";
        var timeout = arguments.TryGetProperty("timeout_seconds", out var ts) && ts.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, _timeoutSeconds)
            : _timeoutSeconds;

        if (string.IsNullOrWhiteSpace(code))
            return PluginToolResult.Error("code is required");

        // Capture Console output from the script.
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

            // Build import list.
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

            var refs = LoadReferenceAssemblies(_blockInternet);

            // Inject WorkDirectory as a global so scripts can read/write files there.
            var workDir = _workDirAccess ? context.WorkDirectory : "";
            var globals  = new ScriptGlobals { WorkDirectory = workDir };

            var options = ScriptOptions.Default
                .WithImports(imports)
                .WithReferences(refs)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(false)
                .WithEmitDebugInformation(false);

            var script = CSharpScript.Create(code, options, typeof(ScriptGlobals));

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
                return PluginToolResult.Error($"Compilation error:\n{diag}");
            }
            catch (OperationCanceledException)
            {
                return PluginToolResult.Error($"Execution timed out after {timeout}s.");
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
                return PluginToolResult.Error(sb.Length > 0 ? sb.ToString().TrimEnd() : scriptEx.Message);
            }
            if (returnValue is not null)
                sb.AppendLine($"Return value: {returnValue}");

            return sb.Length == 0
                ? PluginToolResult.Ok("(no output)")
                : PluginToolResult.Ok(sb.ToString().TrimEnd());
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
        // Always include core runtime references for Roslyn.
        result.Add(typeof(object).Assembly);
        result.Add(typeof(Enumerable).Assembly);
        result.Add(typeof(System.Text.StringBuilder).Assembly);
        result.Add(typeof(System.IO.Path).Assembly);
        return result.Distinct();
    }

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private sealed class Config
    {
        [JsonPropertyName("timeoutSeconds")] public int?  TimeoutSeconds { get; set; }
        [JsonPropertyName("memoryLimitMb")]  public int?  MemoryLimitMb  { get; set; }
        [JsonPropertyName("blockInternet")]  public bool? BlockInternet  { get; set; }
        [JsonPropertyName("workDirAccess")]  public bool? WorkDirAccess  { get; set; }
    }
}

/// <summary>Globals object injected into every script. Provides WorkDirectory access.</summary>
public sealed class ScriptGlobals
{
    /// <summary>Per-conversation working directory. Scripts may read/write files here.</summary>
    public string WorkDirectory { get; set; } = "";
}
