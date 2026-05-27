using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// <see cref="IChatProvider"/> implementation that delegates to the long-standing
/// in-process LLamaSharp executor for local GGUF models.
/// </summary>
public sealed class LocalChatProvider : IChatProvider
{
    private readonly LLamaSharpProvider _llama;

    public LocalChatProvider(LLamaSharpProvider llama) { _llama = llama; }

    public ModelSource Source => ModelSource.Local;

    public bool IsReady(CatalogEntry entry) =>
        string.Equals(_llama.LoadedModelId, entry.Id, StringComparison.OrdinalIgnoreCase);

    public string? UnavailableReason(CatalogEntry entry) =>
        IsReady(entry) ? null : "Model is not loaded.";

    public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localFilePath))
            throw new InvalidOperationException("Local models require a file path.");
        return _llama.LoadAsync(localFilePath, entry.Id, entry.RecommendedContextSize, ct);
    }

    public Task UnloadAsync() => _llama.UnloadAsync();

    public IAsyncEnumerable<string> GenerateAsync(
        CatalogEntry entry, string prompt, int maxTokens, IReadOnlyList<string> stops, CancellationToken ct)
        => _llama.GenerateAsync(prompt, maxTokens, stops, ct);
}
