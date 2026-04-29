using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Download;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Tracks in-flight model downloads. One entry per catalog id; concurrent calls
/// to <see cref="StartAsync"/> for the same id return the running entry.
/// </summary>
public sealed class DownloadCoordinator(
    ModelDownloader downloader,
    ILogger<DownloadCoordinator> log)
{
    private readonly ConcurrentDictionary<string, DownloadEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public DownloadEntry? Get(string modelId) =>
        _entries.TryGetValue(modelId, out var e) ? e : null;

    public IReadOnlyDictionary<string, DownloadEntry> Snapshot() => _entries;

    public DownloadEntry StartAsync(CatalogEntry entry, string modelsDir)
    {
        return _entries.GetOrAdd(entry.Id, id =>
        {
            var cts = new CancellationTokenSource();
            var de = new DownloadEntry(entry);

            var progress = new Progress<DownloadProgress>(p =>
            {
                de.Stage = p.Stage.ToString();
                de.Bytes = p.BytesDownloaded;
                de.TotalBytes = p.TotalBytes;
                de.BytesPerSecond = p.BytesPerSecond;
                de.EtaSeconds = p.Eta.TotalSeconds;
            });

            de.Cts = cts;
            de.Task = Task.Run(async () =>
            {
                try
                {
                    foreach (var f in entry.Files)
                    {
                        var dest = ModelCatalogService.ResolveDestinationPath(modelsDir, entry, f);
                        await downloader.DownloadAsync(f.Url, dest, f.SizeBytes, f.Sha256, progress, cts.Token);
                    }
                    de.Stage = DownloadStage.Completed.ToString();
                    log.LogInformation("Download complete for {Id}", entry.Id);
                }
                catch (OperationCanceledException)
                {
                    de.Stage = DownloadStage.Cancelled.ToString();
                    de.Error = "Cancelled by admin.";
                    log.LogInformation("Download cancelled for {Id}", entry.Id);
                }
                catch (Exception ex)
                {
                    de.Stage = DownloadStage.Failed.ToString();
                    de.Error = ex.Message;
                    log.LogError(ex, "Download failed for {Id}", entry.Id);
                }
            }, cts.Token);

            return de;
        });
    }

    public bool Cancel(string modelId)
    {
        if (!_entries.TryGetValue(modelId, out var e)) return false;
        e.Cts?.Cancel();
        return true;
    }

    /// <summary>Removes finished/failed/cancelled entries from the tracker.</summary>
    public void Clear(string modelId) => _entries.TryRemove(modelId, out _);
}

public sealed class DownloadEntry(CatalogEntry entry)
{
    public CatalogEntry Entry { get; } = entry;
    public Task? Task { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public string Stage { get; set; } = DownloadStage.Queued.ToString();
    public long Bytes { get; set; }
    public long TotalBytes { get; set; } = entry.TotalBytes;
    public double BytesPerSecond { get; set; }
    public double EtaSeconds { get; set; }
    public string? Error { get; set; }

    public bool IsRunning => Task is { IsCompleted: false };
}
