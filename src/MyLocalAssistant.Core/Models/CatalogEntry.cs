namespace MyLocalAssistant.Core.Models;

public enum ModelTier
{
    Lightweight,
    Mid,
    Heavy,
    Workstation,
    /// <summary>Embedding model (used for RAG vectorization, not chat).</summary>
    Embedding,
}

public sealed class CatalogFile
{
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class CatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ModelTier Tier { get; set; }
    public string HuggingFaceRepo { get; set; } = string.Empty;
    public string Quantization { get; set; } = "Q4_K_M";
    public List<CatalogFile> Files { get; set; } = new();
    public string License { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;
    public int RecommendedContextSize { get; set; } = 4096;
    public int MinRamGb { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>Output vector dimension for Embedding-tier models. 0 for chat models.</summary>
    public int EmbeddingDimension { get; set; }

    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}
