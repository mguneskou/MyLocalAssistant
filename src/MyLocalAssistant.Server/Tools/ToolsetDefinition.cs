namespace MyLocalAssistant.Server.Tools;

/// <summary>
/// A named group of tool ids — mirrors Hermes's toolset system.
/// Toolsets allow agents to be assigned a role-based bundle of tools
/// instead of individually picking each tool id.
/// </summary>
public sealed record ToolsetDefinition(
    string Id,
    string Name,
    string Description,
    /// <summary>Direct tool ids this toolset includes.</summary>
    IReadOnlyList<string> ToolIds,
    /// <summary>Other toolset ids whose tools are transitively included.</summary>
    IReadOnlyList<string> IncludesToolsetIds,
    bool IsBuiltIn = true);
