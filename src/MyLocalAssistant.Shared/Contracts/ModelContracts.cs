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
