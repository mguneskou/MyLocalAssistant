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
    ILogger<ModelManager> log)
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ModelStatus _status = ModelStatus.Unloaded;
    private string? _lastError;

    public ModelStatus Status => _status;
    public string? LoadedModelId => provider.LoadedModelId;
    public string? ActiveModelId => settings.DefaultModelId;
    public string Backend => provider.Backend;
    public string? LastError => _lastError;

    public List<ModelDto> List()
    {
        var installed = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .ToDictionary(i => i.Catalog.Id, StringComparer.OrdinalIgnoreCase);
        var active = settings.DefaultModelId;
        var result = new List<ModelDto>(catalog.Entries.Count);
        foreach (var e in catalog.Entries)
        {
            installed.TryGetValue(e.Id, out var inst);
            var dl = downloads.Get(e.Id);
            DownloadStatusDto? dlDto = dl is null ? null : new DownloadStatusDto(
                dl.Stage, dl.Bytes, dl.TotalBytes, dl.BytesPerSecond, dl.EtaSeconds, dl.Error);
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
                Download: dlDto));
        }
        return result;
    }

    public ActiveModelStatusDto GetStatus() =>
        new(ActiveModelId, _status.ToString(), _lastError, Backend);

    public DownloadStatusDto StartDownload(string modelId)
    {
        var entry = catalog.FindById(modelId)
            ?? throw new KeyNotFoundException($"Unknown model id: {modelId}");
        var de = downloads.StartAsync(entry, ServerPaths.ModelsDirectory);
        return new DownloadStatusDto(de.Stage, de.Bytes, de.TotalBytes, de.BytesPerSecond, de.EtaSeconds, de.Error);
    }

    public bool CancelDownload(string modelId) => downloads.Cancel(modelId);

    public bool DeleteModel(string modelId)
    {
        if (string.Equals(modelId, ActiveModelId, StringComparison.OrdinalIgnoreCase) && _status != ModelStatus.Unloaded)
            throw new InvalidOperationException("Cannot delete the active loaded model. Activate another or stop first.");
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
        var installed = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .FirstOrDefault(i => i.Catalog.Id == modelId)
            ?? throw new InvalidOperationException("Model is not installed. Download it first.");

        settings.DefaultModelId = modelId;
        settingsStore.Save(settings);

        _ = LoadAsync(entry, installed.PrimaryFilePath);
        return GetStatus();
    }

    /// <summary>Best-effort eager load on startup if the active model is installed.</summary>
    public Task EnsureLoadedOnStartupAsync()
    {
        if (string.IsNullOrEmpty(settings.DefaultModelId)) return Task.CompletedTask;
        var entry = catalog.FindById(settings.DefaultModelId);
        if (entry is null) return Task.CompletedTask;
        var inst = catalog.GetInstalled(ServerPaths.ModelsDirectory)
            .FirstOrDefault(i => i.Catalog.Id == entry.Id);
        if (inst is null)
        {
            log.LogInformation("Active model {Id} is configured but not installed; skipping eager load.", entry.Id);
            return Task.CompletedTask;
        }
        return LoadAsync(entry, inst.PrimaryFilePath);
    }

    private async Task LoadAsync(CatalogEntry entry, string path)
    {
        await _loadLock.WaitAsync();
        try
        {
            _status = ModelStatus.Loading;
            _lastError = null;
            await provider.LoadAsync(path, entry.Id, entry.RecommendedContextSize);
            _status = ModelStatus.Loaded;
            log.LogInformation("Loaded model {Id} ({Display})", entry.Id, entry.DisplayName);
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
