namespace MyLocalAssistant.Shared.Contracts;

public sealed record AgentDto(
    string Id,
    string Name,
    string Description,
    string Category,
    bool IsGeneric,
    bool Enabled,
    string? DefaultModelId,
    bool RagEnabled,
    IReadOnlyList<Guid> RagCollectionIds,
    string SystemPrompt = "",
    IReadOnlyList<string>? ToolIds = null,
    int? MaxToolCalls = null,
    string? ScenarioNotes = null);

public sealed record AgentUpdateRequest(
    bool Enabled,
    string? DefaultModelId,
    bool RagEnabled,
    IReadOnlyList<Guid>? RagCollectionIds,
    string? SystemPrompt = null,
    string? Description = null,
    IReadOnlyList<string>? ToolIds = null,
    int? MaxToolCalls = null,
    string? ScenarioNotes = null);

public sealed record ChatTurnRequest(
    string AgentId,
    string ConversationId,
    string Message,
    IReadOnlyList<AttachmentDto>? Attachments = null);

public sealed record AttachmentDto(
    string FileName,
    string ContentType,
    byte[] Content);

public enum TokenStreamFrameKind
{
    Token,
    End,
    Error,
    Meta,
    /// <summary>The model issued a tool call (informational; UI may display it).</summary>
    ToolCall,
    /// <summary>The tool returned a result (informational; UI may display it).</summary>
    ToolResult,
    /// <summary>An agent-bound skill was skipped (disabled, missing, model lacks tool support, or context too small).</summary>
    ToolUnavailable,
    /// <summary>The request is queued behind other local-model requests. Payload: position=1-based queue position.</summary>
    Queued,
}

public sealed record TokenStreamFrame(
    TokenStreamFrameKind Kind,
    string? Text = null,
    string? ErrorMessage = null,
    System.Guid? ConversationId = null,
    string? ToolName = null,
    string? ToolJson = null,
    string? ToolReason = null,
    int? QueuePosition = null);
