using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

public enum ModelStatus { Unloaded, Loading, Loaded, Failed }

/// <summary>
/// Owns the single active LLM in v2 (single-model mode). Knows what is installed,
/// which catalog entry is the active default, and lazily loads it on first use.
/// Activation switches the default and (re)loads in the background.
/// </summary>
public sealed class ModelManager(
    ModelCatalogService catalog,
    DownloadCoordinator downloads,
    ServerSettings settings,
    ServerSettingsStore settingsStore,
    LLamaSharpProvider provider,
    ChatProviderRouter router,
    ILogger<ModelManager> log)
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ModelStatus _status = ModelStatus.Unloaded;
    private string? _lastError;
    private CatalogEntry? _activeEntry;

    public ModelStatus Status => _status;
    public string? LoadedModelId => _activeEntry?.Id;
    public string? ActiveModelId => settings.DefaultModelId;

    /// <summary>
    /// Backend label for the currently-loaded model. Cloud entries return their source
    /// ("OpenAi"/"Anthropic") so the Models tab footer reads sensibly without special-casing.
    /// </summary>
    public string Backend => _activeEntry is { IsCloud: true }
        ? _activeEntry.Source.ToString()
        : provider.Backend;

    public string? LastError => _lastError;

    /// <summary>
    /// Returns the catalog with per-row install/active state. When <paramref name="isGlobalAdmin"/>
    /// is false, cloud entries are filtered out so non-owners never see provider names
    /// or accidentally activate a cloud model on the shared key.
    /// </summary>
    public List<ModelDto> List(bool isGlobalAdmin = true)
    {
        var installed = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .ToDictionary(i => i.Catalog.Id, StringComparer.OrdinalIgnoreCase);
        var active = settings.DefaultModelId;
        var activeEmbed = settings.EmbeddingModelId;
        var result = new List<ModelDto>(catalog.Entries.Count);
        foreach (var e in catalog.Entries)
        {
            if (e.IsCloud && !isGlobalAdmin) continue;
            installed.TryGetValue(e.Id, out var inst);
            var dl = downloads.Get(e.Id);
            DownloadStatusDto? dlDto = dl is null ? null : new DownloadStatusDto(
                dl.Stage, dl.Bytes, dl.TotalBytes, dl.BytesPerSecond, dl.EtaSeconds, dl.Error);
            var cloudConfigured = e.Source switch
            {
                ModelSource.OpenAi    => settings.IsOpenAiConfigured,
                ModelSource.Anthropic => settings.IsAnthropicConfigured,
                ModelSource.Groq      => settings.IsGroqConfigured,
                ModelSource.Gemini    => settings.IsGeminiConfigured,
                ModelSource.Mistral   => settings.IsMistralConfigured,
                _ => false,
            };
            result.Add(new ModelDto(
                Id: e.Id,
                DisplayName: e.DisplayName,
                Tier: e.Tier.ToString(),
                Quantization: e.Quantization,
                TotalBytes: e.TotalBytes,
                RecommendedContextSize: e.RecommendedContextSize,
                MinRamGb: e.MinRamGb,
                Description: e.Description,
                License: e.License,
                LicenseUrl: e.LicenseUrl,
                IsInstalled: inst is not null,
                SizeOnDisk: inst?.SizeOnDisk,
                IsActive: string.Equals(e.Id, active, StringComparison.OrdinalIgnoreCase),
                IsActiveEmbedding: string.Equals(e.Id, activeEmbed, StringComparison.OrdinalIgnoreCase),
                Download: dlDto,
                Source: e.Source.ToString(),
                IsCloud: e.IsCloud,
                IsCloudConfigured: cloudConfigured));
        }
        return result;
    }

    public ActiveModelStatusDto GetStatus() =>
        new(ActiveModelId, _status.ToString(), _lastError, Backend);

    public DownloadStatusDto StartDownload(string modelId)
    {
        var entry = catalog.FindById(modelId)
            ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
        if (entry.IsCloud)
            throw new InvalidOperationException("Cloud models have nothing to download. Configure the API key in Server Settings instead.");
        var de = downloads.StartAsync(entry, ServerPaths.ModelsDirectory);
        return new DownloadStatusDto(de.Stage, de.Bytes, de.TotalBytes, de.BytesPerSecond, de.EtaSeconds, de.Error);
    }

    public bool CancelDownload(string modelId) => downloads.Cancel(modelId);

    public bool DeleteModel(string modelId)
    {
        if (string.Equals(modelId, ActiveModelId, StringComparison.OrdinalIgnoreCase) && _status != ModelStatus.Unloaded)
            throw new InvalidOperationException("Cannot delete the active loaded model. Activate another or stop first.");
        var entry = catalog.FindById(modelId)
            ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
        if (entry.IsCloud)
            throw new InvalidOperationException("Cloud entries have no local files to delete.");
        var dl = downloads.Get(modelId);
        if (dl is { IsRunning: true })
            throw new InvalidOperationException("Cannot delete a model while it is downloading. Cancel first.");

        var dir = Path.Combine(ServerPaths.ModelsDirectory, modelId);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        downloads.Clear(modelId);
        log.LogWarning("Deleted local files for model {Id}", modelId);
        return true;
    }

    /// <summary>
    /// Sets the active model id, persists it, and (re)loads in the background.
    /// Returns immediately. Watch <see cref="Status"/> for the result.
    /// </summary>
    public ActiveModelStatusDto Activate(string modelId)
    {
        var entry = catalog.FindById(modelId)
            ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
        if (entry.Tier == ModelTier.Embedding)
            throw new InvalidOperationException("This is an embedding model. Use /api/admin/models/embedding/{id}/activate instead.");

        string? localFile = null;
        if (!entry.IsCloud)
        {
            var installed = catalog.GetInstalled(ServerPaths.ModelsDirectory)
                .FirstOrDefault(i => i.Catalog.Id == modelId)
                ?? throw new InvalidOperationException("Model is not installed. Download it first.");
            localFile = installed.PrimaryFilePath;
        }
        else
        {
            // Friendlier upfront check than letting LoadAsync throw.
            var p = router.Get(entry);
            var reason = p.UnavailableReason(entry);
            if (reason is not null)
                throw new InvalidOperationException(reason);
        }

        settings.DefaultModelId = modelId;
        settingsStore.Save(settings);

        _ = LoadAsync(entry, localFile);
        return GetStatus();
    }

    /// <summary>Best-effort eager load on startup if the active model is installed (or a cloud entry with key).</summary>
    public Task EnsureLoadedOnStartupAsync()
    {
        if (string.IsNullOrEmpty(settings.DefaultModelId)) return Task.CompletedTask;
        var entry = catalog.FindById(settings.DefaultModelId);
        if (entry is null) return Task.CompletedTask;
        if (entry.IsCloud)
        {
            var p = router.Get(entry);
            if (!p.IsReady(entry))
            {
                log.LogInformation("Active cloud model {Id} is configured but its API key is missing; skipping eager load.", entry.Id);
                return Task.CompletedTask;
            }
            return LoadAsync(entry, null);
        }
        var inst = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .FirstOrDefault(i => i.Catalog.Id == entry.Id);
        if (inst is null)
        {
            log.LogInformation("Active model {Id} is configured but not installed; skipping eager load.", entry.Id);
            return Task.CompletedTask;
        }
        return LoadAsync(entry, inst.PrimaryFilePath);
    }

    private async Task LoadAsync(CatalogEntry entry, string? localFilePath)
    {
        await _loadLock.WaitAsync();
        try
        {
            _status = ModelStatus.Loading;
            _lastError = null;
            // Unload the previous local model when switching to a cloud entry so we
            // don't keep a multi-GB GGUF resident for nothing.
            if (entry.IsCloud && provider.LoadedModelId is not null)
                await provider.UnloadAsync().ConfigureAwait(false);
            var p = router.Get(entry);
            await p.LoadAsync(entry, localFilePath, CancellationToken.None).ConfigureAwait(false);
            _activeEntry = entry;
            _status = ModelStatus.Loaded;
            log.LogInformation("Loaded model {Id} ({Display}) via {Source}", entry.Id, entry.DisplayName, entry.Source);
        }
        catch (Exception ex)
        {
            _status = ModelStatus.Failed;
            _lastError = ex.Message;
            log.LogError(ex, "Failed to load model {Id}", entry.Id);
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
