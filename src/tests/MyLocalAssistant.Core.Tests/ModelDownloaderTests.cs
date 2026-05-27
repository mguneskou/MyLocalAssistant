using System.Net;
using MyLocalAssistant.Core.Download;

namespace MyLocalAssistant.Core.Tests;

public class ModelDownloaderTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _prefix;
    private readonly byte[] _payload;
    private readonly string _payloadSha;

    public ModelDownloaderTests()
    {
        _payload = new byte[64 * 1024]; // 64 KB deterministic
        for (int i = 0; i < _payload.Length; i++) _payload[i] = (byte)(i & 0xFF);

        using var sha = System.Security.Cryptography.SHA256.Create();
        _payloadSha = Convert.ToHexString(sha.ComputeHash(_payload)).ToLowerInvariant();

        _listener = new HttpListener();
        var port = GetFreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeLoopAsync);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task ServeLoopAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            long start = 0;
            long end = _payload.Length - 1;
            var rangeHeader = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var spec = rangeHeader.Substring("bytes=".Length);
                var parts = spec.Split('-');
                start = long.Parse(parts[0]);
                if (parts.Length > 1 && long.TryParse(parts[1], out var e)) end = e;
                ctx.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                ctx.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{_payload.Length}");
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            }
            var len = (int)(end - start + 1);
            ctx.Response.ContentLength64 = len;
            await ctx.Response.OutputStream.WriteAsync(_payload.AsMemory((int)start, len));
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    [Fact]
    public async Task Download_FromScratch_Succeeds()
    {
        var dl = new ModelDownloader();
        var dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        try
        {
            await dl.DownloadAsync(_prefix + "file.bin", dest, _payload.Length, _payloadSha, null, CancellationToken.None);
            Assert.True(File.Exists(dest));
            Assert.Equal(_payload.Length, new FileInfo(dest).Length);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task Download_ResumesFromPartial()
    {
        var dl = new ModelDownloader();
        var dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        var partial = dest + ".partial";
        try
        {
            // Pre-seed half the file
            await File.WriteAllBytesAsync(partial, _payload.Take(_payload.Length / 2).ToArray());
            await dl.DownloadAsync(_prefix + "file.bin", dest, _payload.Length, _payloadSha, null, CancellationToken.None);
            var actual = await File.ReadAllBytesAsync(dest);
            Assert.Equal(_payload, actual);
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    [Fact]
    public async Task Download_ShaMismatch_DeletesFile_AndThrows()
    {
        var dl = new ModelDownloader();
        var dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
        try
        {
            await Assert.ThrowsAsync<DownloadFailedException>(() =>
                dl.DownloadAsync(_prefix + "file.bin", dest, _payload.Length, new string('a', 64), null, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
