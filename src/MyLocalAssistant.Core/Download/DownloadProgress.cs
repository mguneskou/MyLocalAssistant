namespace MyLocalAssistant.Core.Download;

public sealed record DownloadProgress(
    string FileName,
    long BytesDownloaded,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan Eta,
    DownloadStage Stage);

public enum DownloadStage
{
    Queued,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}
