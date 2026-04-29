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
