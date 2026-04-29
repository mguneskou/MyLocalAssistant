namespace MyLocalAssistant.Shared.Contracts;

public sealed record RagCollectionDto(
    Guid Id,
    string Name,
    string? Description,
    int DocumentCount,
    DateTimeOffset CreatedAt);

public sealed record RagDocumentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    int ChunkCount,
    DateTimeOffset IngestedAt,
    string? Sha256);

public sealed record CreateCollectionRequest(string Name, string? Description);

public sealed record RagSearchRequest(string Query, int K = 4);

public sealed record RagSearchHitDto(
    string ChunkId,
    Guid DocumentId,
    string Source,
    int Page,
    float Distance,
    string Text);
