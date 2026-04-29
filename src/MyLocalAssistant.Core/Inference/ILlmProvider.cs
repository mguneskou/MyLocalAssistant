namespace MyLocalAssistant.Core.Inference;

public interface ILlmProvider : IAsyncDisposable
{
    string? LoadedModelId { get; }
    string Backend { get; }

    Task LoadAsync(string modelPath, string modelId, int contextSize, CancellationToken ct = default);

    IAsyncEnumerable<string> GenerateAsync(string prompt, int maxTokens, CancellationToken ct = default);

    Task UnloadAsync();
}
