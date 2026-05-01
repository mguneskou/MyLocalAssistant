using System.Text.Json;

namespace MyLocalAssistant.Plugin.Shared;

/// <summary>One tool exposed by a plugin. Each plugin may register multiple implementations.</summary>
public interface IPluginTool
{
    /// <summary>
    /// Called once on <c>initialize</c> and on any subsequent admin config change.
    /// Parse and validate <paramref name="configJson"/>; throw on invalid config —
    /// the host will surface the error and keep the previous state active.
    /// </summary>
    void Configure(string? configJson);

    Task<PluginToolResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        PluginContext context,
        CancellationToken ct);
}
