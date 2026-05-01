namespace MyLocalAssistant.Plugin.Shared;

/// <summary>Result of a single tool invocation returned by a plugin handler.</summary>
public sealed record PluginToolResult(
    bool IsError,
    string Content,
    /// <summary>Optional structured JSON payload kept for audit/UI; not shown to the LLM as text.</summary>
    string? StructuredJson = null)
{
    public static PluginToolResult Ok(string content, string? structured = null)
        => new(false, content, structured);

    public static PluginToolResult Error(string message)
        => new(true, message);
}
