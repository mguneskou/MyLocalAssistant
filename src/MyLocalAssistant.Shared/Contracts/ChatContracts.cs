namespace MyLocalAssistant.Shared.Contracts;

public sealed record ChatRequest(
    string AgentId,
    string Message,
    int? MaxTokens = null,
    System.Guid? ConversationId = null);

public sealed record ConversationSummaryDto(
    System.Guid Id,
    string AgentId,
    string Title,
    System.DateTimeOffset CreatedAt,
    System.DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record ConversationMessageDto(
    System.Guid Id,
    string Role,
    string? Body,
    System.DateTimeOffset CreatedAt);

public sealed record ConversationDetailDto(
    System.Guid Id,
    string AgentId,
    string Title,
    System.DateTimeOffset CreatedAt,
    System.DateTimeOffset UpdatedAt,
    System.Collections.Generic.List<ConversationMessageDto> Messages);
