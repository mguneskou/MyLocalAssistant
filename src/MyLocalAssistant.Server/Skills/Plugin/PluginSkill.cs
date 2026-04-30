using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;
using MyLocalAssistant.Shared.Plugins;

namespace MyLocalAssistant.Server.Skills.Plugin;

/// <summary>
/// Wraps a verified plug-in folder as an <see cref="ISkill"/>. Spawns the plug-in process
/// lazily on first invocation, recycles it across calls, and respawns after a crash.
/// One process per skill (per server). Per-conversation work-dir is passed in
/// <see cref="SkillContext.ConversationId"/> via the <c>invoke</c> RPC.
/// </summary>
public sealed class PluginSkill : ISkill, IAsyncDisposable
{
    private readonly SkillManifest _manifest;
    private readonly string _pluginFolder;
    private readonly string _outputRoot;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _spawnLock = new(1, 1);
    private SandboxedProcess? _proc;
    private SkillRpcChannel? _channel;
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(30);

    public PluginSkill(SkillManifest manifest, string pluginFolder, string outputRoot, ILogger log)
    {
        _manifest = manifest;
        _pluginFolder = pluginFolder;
        _outputRoot = outputRoot;
        _log = log;
        Tools = manifest.Tools.Select(t => new SkillToolDto(t.Name, t.Description, t.ArgumentsSchemaJson)).ToArray();
        Requirements = new SkillRequirementsDto(
            string.IsNullOrWhiteSpace(manifest.ToolMode) ? ToolCallProtocols.Tags : manifest.ToolMode,
            manifest.MinContextK <= 0 ? 4 : manifest.MinContextK);
    }

    public string Id => _manifest.Id;
    public string Name => string.IsNullOrWhiteSpace(_manifest.Name) ? _manifest.Id : _manifest.Name;
    public string Description => _manifest.Description;
    public string Category => string.IsNullOrWhiteSpace(_manifest.Category) ? "Plugin" : _manifest.Category;
    public string Source => SkillSources.Plugin;
    public string Version => _manifest.Version;
    public string? Publisher => string.IsNullOrWhiteSpace(_manifest.Publisher) ? null : _manifest.Publisher;
    public string? KeyId => string.IsNullOrWhiteSpace(_manifest.KeyId) ? null : _manifest.KeyId;
    public IReadOnlyList<SkillToolDto> Tools { get; }
    public SkillRequirementsDto Requirements { get; }

    /// <summary>Plug-ins receive their config via the <c>configure</c> RPC on first launch.</summary>
    private string? _configJson;
    public void Configure(string? configJson) => _configJson = configJson;

    public async Task<SkillResult> InvokeAsync(SkillInvocation invocation, SkillContext ctx)
    {
        // ctx.WorkDirectory is resolved by ChatService and already honors the user's
        // WorkRoot (v2.1.7+). Fall back to the default output root only if the host
        // somehow hands us an empty value.
        var workDir = string.IsNullOrWhiteSpace(ctx.WorkDirectory)
            ? Path.Combine(_outputRoot, ctx.ConversationId.ToString("N"))
            : ctx.WorkDirectory;
        SecureDirectory.EnsureLockedDown(workDir);
        try
        {
            var channel = await EnsureChannelAsync(ctx.CancellationToken).ConfigureAwait(false);
            var paramsObj = new
            {
                tool = invocation.ToolName,
                arguments = ParseOrEmpty(invocation.ArgumentsJson),
                context = new
                {
                    userId = ctx.UserId.ToString(),
                    username = ctx.Username,
                    isAdmin = ctx.IsAdmin,
                    agentId = ctx.AgentId,
                    conversationId = ctx.ConversationId.ToString(),
                    workDirectory = workDir,
                },
            };
            var resultElement = await channel.CallAsync("invoke", paramsObj, s_callTimeout, ctx.CancellationToken).ConfigureAwait(false);
            return ParseSkillResult(resultElement);
        }
        catch (SkillRpcException rex)
        {
            return SkillResult.Error(rex.Message);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Plug-in {Skill} invocation failed; recycling channel.", Id);
            await RecycleAsync().ConfigureAwait(false);
            return SkillResult.Error("Plug-in invocation failed: " + ex.Message);
        }
    }

    private static object ParseOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new { };
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return new { }; }
    }

    private static SkillResult ParseSkillResult(JsonElement? result)
    {
        if (result is null) return SkillResult.Error("Plug-in returned null result.");
        var root = result.Value;
        if (root.ValueKind != JsonValueKind.Object) return SkillResult.Error("Plug-in result must be a JSON object.");
        var isError = root.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";
        string? structured = null;
        if (root.TryGetProperty("structured", out var s) && s.ValueKind != JsonValueKind.Null && s.ValueKind != JsonValueKind.Undefined)
            structured = s.GetRawText();
        return new SkillResult(isError, content, structured);
    }

    private async Task<SkillRpcChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsFaulted: false }) return _channel;
        await _spawnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is { IsFaulted: false }) return _channel;
            await DisposeChannelAsync().ConfigureAwait(false);

            var exe = Path.Combine(_pluginFolder, _manifest.Entry.Command);
            if (!File.Exists(exe))
                throw new FileNotFoundException($"Plug-in '{Id}' entry executable not found: {exe}");

            // Spawn with a per-skill scratch dir; per-call working dir is passed via RPC.
            var workDir = Path.Combine(_outputRoot, "__skill", Id);
            _proc = SandboxedProcessLauncher.Launch(exe, _manifest.Entry.Args, workDir, log: _log);
            _channel = new SkillRpcChannel(Id, _proc.StandardInput, _proc.StandardOutput, _proc.StandardError, _log);

            // Initialize handshake.
            await _channel.CallAsync("initialize", new
            {
                skillId = Id,
                version = Version,
                configJson = _configJson,
            }, s_callTimeout, ct).ConfigureAwait(false);

            _log.LogInformation("Plug-in {Skill} v{Ver} launched (pid={Pid}).", Id, Version, _proc.Process.Id);
            return _channel;
        }
        finally { _spawnLock.Release(); }
    }

    private async Task RecycleAsync()
    {
        await _spawnLock.WaitAsync().ConfigureAwait(false);
        try { await DisposeChannelAsync().ConfigureAwait(false); }
        finally { _spawnLock.Release(); }
    }

    private async Task DisposeChannelAsync()
    {
        if (_channel is not null) { await _channel.DisposeAsync().ConfigureAwait(false); _channel = null; }
        _proc?.Dispose();
        _proc = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeChannelAsync().ConfigureAwait(false);
        _spawnLock.Dispose();
    }
}
