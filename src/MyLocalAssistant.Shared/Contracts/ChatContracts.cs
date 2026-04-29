namespace MyLocalAssistant.Shared.Contracts;

public sealed record ChatRequest(
    string AgentId,
    string Message,
    int? MaxTokens = null);
