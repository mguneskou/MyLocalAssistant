namespace MyLocalAssistant.Shared.Contracts;

public sealed record AgentDto(
    string Id,
    string Name,
    string Description,
    string Category,
    bool Enabled);

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
}

public sealed record TokenStreamFrame(
    TokenStreamFrameKind Kind,
    string? Text = null,
    string? ErrorMessage = null);
