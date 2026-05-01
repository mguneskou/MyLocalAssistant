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

/// <summary>
/// Where a model runs. Local = GGUF on disk, served by LLamaSharp.
/// Cloud entries (<see cref="OpenAi"/>, <see cref="Anthropic"/>) need an API key,
/// have no <see cref="CatalogFile"/> entries, and are filtered out of the Models tab
/// for non-global-admin users.
/// </summary>
public enum ModelSource
{
    Local = 0,
    OpenAi = 1,
    Anthropic = 2,
    Groq = 3,
    Gemini = 4,
    Mistral = 5,
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

    /// <summary>Where the model runs. Defaults to <see cref="ModelSource.Local"/> for backward-compat with existing catalogs.</summary>
    public ModelSource Source { get; set; } = ModelSource.Local;
    /// <summary>Provider-side model identifier (e.g. "gpt-4o-mini"). Defaults to <see cref="Id"/> when empty.</summary>
    public string RemoteModel { get; set; } = string.Empty;

    public bool IsCloud => Source != ModelSource.Local;

    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}
