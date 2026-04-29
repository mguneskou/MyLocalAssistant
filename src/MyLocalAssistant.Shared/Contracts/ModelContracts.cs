namespace MyLocalAssistant.Shared.Contracts;

public sealed record ModelDto(
    string Id,
    string DisplayName,
    string Tier,
    string Quantization,
    long TotalBytes,
    int RecommendedContextSize,
    int MinRamGb,
    string Description,
    string License,
    string LicenseUrl,
    bool IsInstalled,
    long? SizeOnDisk,
    bool IsActive,
    bool IsActiveEmbedding,
    DownloadStatusDto? Download);

public sealed record DownloadStatusDto(
    string Stage,
    long Bytes,
    long TotalBytes,
    double BytesPerSecond,
    double EtaSeconds,
    string? Error);

public sealed record ActiveModelStatusDto(
    string? ActiveModelId,
    string Status,
    string? LastError,
    string Backend);

public sealed record ActiveEmbeddingStatusDto(
    string? ActiveModelId,
    string Status,
    string? LastError,
    int EmbeddingDimension);
