namespace MyLocalAssistant.Shared.Contracts;

public sealed record AuditEntryDto(
    long Id,
    System.DateTimeOffset Timestamp,
    System.Guid? UserId,
    string? Username,
    string Action,
    string? AgentId,
    string? Detail,
    string? IpAddress,
    bool Success,
    bool IsAdminAction = false);

public sealed record AuditPageDto(
    System.Collections.Generic.List<AuditEntryDto> Items,
    int Total,
    int Skip,
    int Take);
