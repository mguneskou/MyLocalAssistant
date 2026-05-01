namespace MyLocalAssistant.Plugin.Shared;

/// <summary>Per-invocation context provided by the server to the plugin process.</summary>
public sealed record PluginContext(
    string UserId,
    string Username,
    bool IsAdmin,
    string AgentId,
    string ConversationId,
    /// <summary>Per-conversation working directory. The plugin may read/write files here freely.</summary>
    string WorkDirectory);
