namespace MyLocalAssistant.Shared.Contracts;

public sealed record ServerSettingsDto(
    string ListenUrl,
    string JwtIssuer,
    string JwtAudience,
    int AccessTokenMinutes,
    int RefreshTokenDays,
    int MessageBodyRetentionDays,
    int AuditRetentionDays,
    string? DefaultModelId,
    string? EmbeddingModelId);

/// <summary>Subset of <see cref="ServerSettingsDto"/> that the Admin UI can update at runtime.</summary>
public sealed record UpdateServerSettingsRequest(
    int AccessTokenMinutes,
    int RefreshTokenDays,
    int MessageBodyRetentionDays,
    int AuditRetentionDays);

/// <summary>Server-wide system prompt prepended to every chat. Owner-only.</summary>
public sealed record GlobalSystemPromptDto(string SystemPrompt);

public sealed record UpdateGlobalSystemPromptRequest(string? SystemPrompt);

/// <summary>
/// Status of cloud LLM provider keys. Booleans only \u2014 the actual key strings are
/// never sent back to clients. Owner-only on the server.
/// </summary>
public sealed record CloudKeysStatusDto(
    bool OpenAiConfigured,
    bool AnthropicConfigured,
    string? OpenAiBaseUrl);

/// <summary>Replace the OpenAI/Anthropic API keys. <c>null</c> field = leave unchanged; empty string = clear.</summary>
public sealed record UpdateCloudKeysRequest(
    string? OpenAiApiKey,
    string? AnthropicApiKey,
    string? OpenAiBaseUrl);

public sealed record CloudKeyTestResultDto(bool Ok, string? Detail);
