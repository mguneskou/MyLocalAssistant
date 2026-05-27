using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyLocalAssistant.Core.Download;

public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Resumable HTTP downloader with SHA256 verification and progress reporting.
/// Writes to a .partial sidecar file and renames atomically on success.
/// </summary>
public sealed class ModelDownloader
{
    private const int BufferSize = 1 << 16; // 64 KB
    private const int ProgressIntervalMs = 250;
    private const int MaxRetries = 5;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(HttpClient? httpClient = null, ILogger<ModelDownloader>? logger = null)
    {
        _http = httpClient ?? CreateDefaultClient();
        _logger = logger ?? NullLogger<ModelDownloader>.Instance;
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None, // ranges + gzip don't mix
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MyLocalAssistant/1.0");
        return c;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
    /// If a .partial file exists, resumes from its size. Verifies SHA256 if provided.
    /// On verification failure both the .partial and final file are deleted.
    /// Retries up to 5 times with exponential backoff on transient errors (429, 5xx, IOException).
    /// </summary>
    public async Task DownloadAsync(
        string url,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partial = destinationPath + ".partial";
        var fileName = Path.GetFileName(destinationPath);

        if (File.Exists(destinationPath))
        {
            await VerifyAndReportAsync(destinationPath, expectedSha256, fileName, expectedSize, progress, ct).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await TryDownloadOnceAsync(url, destinationPath, partial, fileName, expectedSize, expectedSha256, progress, ct).ConfigureAwait(false);
                return; // success
            }
            catch (OperationCanceledException) { throw; }  // never retry cancellation
            catch (DownloadFailedException) { throw; }     // SHA256 mismatch is not transient
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                _logger.LogWarning("Download attempt {Attempt}/{Max} failed ({Msg}); retrying in {Delay}s.",
                    attempt + 1, MaxRetries + 1, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException
        || (ex is InvalidOperationException ioe && (ioe.Message.Contains("429") || ioe.Message.Contains("503") || ioe.Message.Contains("502") || ioe.Message.Contains("500")));

    private async Task TryDownloadOnceAsync(
        string url,
        string destinationPath,
        string partial,
        string fileName,
        long expectedSize,
        string expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            _logger.LogWarning("Partial file {Path} larger than expected; restarting", partial);
            File.Delete(partial);
            existing = 0;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server ignored Range; restart cleanly.
            _logger.LogInformation("Server did not honor Range for {Url}; restarting", url);
            resp.Dispose();
            File.Delete(partial);
            existing = 0;
            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await DownloadStreamAsync(resp2, partial, existing, expectedSize, fileName, progress, ct).ConfigureAwait(false);
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            await DownloadStreamAsync(resp, partial, existing, expectedSize, fileName, progress, ct).ConfigureAwait(false);
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(partial, destinationPath);

        await VerifyAndReportAsync(destinationPath, expectedSha256, fileName, expectedSize, progress, ct).ConfigureAwait(false);
    }

    private async Task DownloadStreamAsync(
        HttpResponseMessage resp,
        string partialPath,
        long startingOffset,
        long expectedSize,
        string fileName,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        long totalSize = expectedSize;
        if (resp.Content.Headers.ContentRange?.Length is long crl) totalSize = crl;
        else if (resp.Content.Headers.ContentLength is long cl) totalSize = startingOffset + cl;

        await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long received = startingOffset;
        var lastReport = Environment.TickCount64;
        var windowStart = lastReport;
        long windowBytes = 0;

        while (true)
        {
            int read = await net.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            windowBytes += read;

            var now = Environment.TickCount64;
            if (now - lastReport >= ProgressIntervalMs)
            {
                var elapsedSec = (now - windowStart) / 1000.0;
                var bps = elapsedSec > 0 ? windowBytes / elapsedSec : 0;
                var remaining = Math.Max(0, totalSize - received);
                var eta = bps > 0 ? TimeSpan.FromSeconds(remaining / bps) : TimeSpan.Zero;
                progress?.Report(new DownloadProgress(fileName, received, totalSize, bps, eta, DownloadStage.Downloading));
                lastReport = now;
                if (now - windowStart > 2000)
                {
                    windowStart = now;
                    windowBytes = 0;
                }
            }
        }

        progress?.Report(new DownloadProgress(fileName, received, totalSize, 0, TimeSpan.Zero, DownloadStage.Verifying));
    }

    private static async Task VerifyAndReportAsync(
        string path,
        string expectedSha256,
        string fileName,
        long expectedSize,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new DownloadProgress(fileName, expectedSize, expectedSize, 0, TimeSpan.Zero, DownloadStage.Verifying));
        var ok = await Sha256Verifier.VerifyAsync(path, expectedSha256, ct).ConfigureAwait(false);
        if (!ok)
        {
            try { File.Delete(path); } catch { /* best effort */ }
            progress?.Report(new DownloadProgress(fileName, 0, expectedSize, 0, TimeSpan.Zero, DownloadStage.Failed));
            throw new DownloadFailedException($"SHA256 mismatch for '{fileName}'. File deleted; please retry.");
        }
        progress?.Report(new DownloadProgress(fileName, expectedSize, expectedSize, 0, TimeSpan.Zero, DownloadStage.Completed));
    }
}
