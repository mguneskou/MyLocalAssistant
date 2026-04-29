namespace MyLocalAssistant.Shared.Contracts;

public sealed record RagCollectionDto(
    Guid Id,
    string Name,
    string? Description,
    int DocumentCount,
    DateTimeOffset CreatedAt,
    string AccessMode,
    int GrantCount);

public sealed record RagDocumentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    int ChunkCount,
    DateTimeOffset IngestedAt,
    string? Sha256);

public sealed record CreateCollectionRequest(string Name, string? Description, string? AccessMode = null);
public sealed record UpdateCollectionRequest(string? Description, string AccessMode);

public sealed record RagSearchRequest(string Query, int K = 4);

public sealed record RagSearchHitDto(
    string ChunkId,
    Guid DocumentId,
    string Source,
    int Page,
    float Distance,
    string Text);

/// <summary>One read grant on a collection. Principal is User, Department or Role; PrincipalId references the corresponding entity.</summary>
public sealed record CollectionGrantDto(
    long Id,
    string PrincipalKind,
    Guid PrincipalId,
    string? PrincipalDisplayName,
    DateTimeOffset CreatedAt);

public sealed record AddCollectionGrantRequest(string PrincipalKind, Guid PrincipalId);
