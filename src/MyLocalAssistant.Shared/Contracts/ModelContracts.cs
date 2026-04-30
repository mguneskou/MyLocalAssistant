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
    DownloadStatusDto? Download,
    /// <summary>"Local", "OpenAi" or "Anthropic". Cloud rows are admin-only and have no Download/Delete actions.</summary>
    string Source = "Local",
    /// <summary>True for cloud entries that need a configured API key. False for local GGUFs.</summary>
    bool IsCloud = false,
    /// <summary>True for cloud entries when the matching provider key is set on the server. Always false for local.</summary>
    bool IsCloudConfigured = false);

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
