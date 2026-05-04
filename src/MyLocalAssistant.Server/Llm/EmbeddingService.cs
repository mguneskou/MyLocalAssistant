using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Loads and serves an Embedding-tier GGUF model via LLamaSharp's <see cref="LLamaEmbedder"/>.
/// Independent of <see cref="ModelManager"/> so chat and embedding models stay loaded together.
/// </summary>
public sealed class EmbeddingService(
    ModelCatalogService catalog,
    ServerSettings settings,
    ServerSettingsStore settingsStore,
    ILogger<EmbeddingService> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private string? _loadedId;
    private int _dim;
    private ModelStatus _status = ModelStatus.Unloaded;
    private string? _lastError;

    public ModelStatus Status => _status;
    public string? LoadedModelId => _loadedId;
    public string? ActiveModelId => settings.EmbeddingModelId;
    public int EmbeddingDimension => _dim;
    public bool IsLoaded => _status == ModelStatus.Loaded && _embedder is not null;

    public ActiveEmbeddingStatusDto GetStatus() =>
        new(ActiveModelId, _status.ToString(), _lastError, _dim);

    public ActiveEmbeddingStatusDto Activate(string modelId)
    {
        var entry = catalog.FindById(modelId)
            ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
        if (entry.Tier != ModelTier.Embedding)
            throw new InvalidOperationException($"Model '{modelId}' is not an embedding model (tier={entry.Tier}).");
        var inst = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .FirstOrDefault(i => i.Catalog.Id == modelId)
            ?? throw new InvalidOperationException("Model is not installed. Download it first.");

        settings.EmbeddingModelId = modelId;
        settingsStore.Save(settings);
        _ = LoadAsync(entry, inst.PrimaryFilePath);
        return GetStatus();
    }

    /// <summary>Eagerly load the configured embedding model on startup if installed.</summary>
    public Task EnsureLoadedOnStartupAsync()
    {
        if (string.IsNullOrEmpty(settings.EmbeddingModelId)) return Task.CompletedTask;
        var entry = catalog.FindById(settings.EmbeddingModelId);
        if (entry is null) return Task.CompletedTask;
        var inst = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .FirstOrDefault(i => i.Catalog.Id == entry.Id);
        if (inst is null)
        {
            log.LogInformation("Embedding model {Id} configured but not installed; skipping eager load.", entry.Id);
            return Task.CompletedTask;
        }
        return LoadAsync(entry, inst.PrimaryFilePath);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!IsLoaded || _embedder is null)
            throw new InvalidOperationException("Embedding model is not loaded.");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is empty.", nameof(text));

        var vectors = await _embedder.GetEmbeddings(text, ct).ConfigureAwait(false);
        if (vectors is null || vectors.Count == 0)
            throw new InvalidOperationException("Embedder returned no vectors.");
        // bge-m3 is mean-pooled → 1 vector per call. Fallback: take first.
        return vectors[0];
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await EmbedAsync(t, ct).ConfigureAwait(false));
        }
        return result;
    }

    private async Task LoadAsync(CatalogEntry entry, string path)
    {
        await _loadLock.WaitAsync();
        try
        {
            await UnloadInternalAsync();
            _status = ModelStatus.Loading;
            _lastError = null;
            BackendSelector.Configure(log);

            log.LogInformation("Loading embedding model {Id} from {Path}", entry.Id, path);
            var ctx = entry.RecommendedContextSize > 0 ? entry.RecommendedContextSize : 8192;
            var p = new ModelParams(path)
            {
                ContextSize = (uint)ctx,
                Embeddings = true,
                PoolingType = LLama.Native.LLamaPoolingType.Mean,
                GpuLayerCount = int.MaxValue,
            };
            LLamaWeights weights;
            try
            {
                weights = await LLamaWeights.LoadFromFileAsync(p).ConfigureAwait(false);
                // Probe: force KV-cache/compute buffer allocation so an integrated-GPU
                // failure surfaces here rather than on the first real EmbedAsync call.
                if (p.GpuLayerCount != 0)
                {
                    var probeEmbedder = new LLamaEmbedder(weights, p, null);
                    await probeEmbedder.GetEmbeddings("probe", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (p.GpuLayerCount > 0)
            {
                log.LogWarning(ex, "GPU load/probe failed for embedding model {Id} — retrying with CPU-only.", entry.Id);
                p = new ModelParams(path) { ContextSize = (uint)ctx, Embeddings = true, PoolingType = LLama.Native.LLamaPoolingType.Mean, GpuLayerCount = 0 };
                weights = await LLamaWeights.LoadFromFileAsync(p).ConfigureAwait(false);
            }
            _weights = weights;
            _embedder = new LLamaEmbedder(_weights, p, null);
            _dim = _embedder.EmbeddingSize;
            _loadedId = entry.Id;
            _status = ModelStatus.Loaded;
            log.LogInformation("Embedding model {Id} loaded (dim={Dim}).", entry.Id, _dim);
        }
        catch (Exception ex)
        {
            _status = ModelStatus.Failed;
            _lastError = ex.Message;
            log.LogError(ex, "Failed to load embedding model {Id}", entry.Id);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private Task UnloadInternalAsync()
    {
        if (_embedder is not null) { _embedder.Dispose(); _embedder = null; }
        if (_weights is not null) { _weights.Dispose(); _weights = null; }
        _loadedId = null;
        _dim = 0;
        _status = ModelStatus.Unloaded;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _loadLock.WaitAsync();
        try { await UnloadInternalAsync(); }
        finally { _loadLock.Release(); }
    }
}
