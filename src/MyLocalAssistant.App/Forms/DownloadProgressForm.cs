using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Download;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.App.Forms;

internal sealed class DownloadProgressForm : Form
{
    private const int MaxParallel = 2;

    private readonly List<CatalogEntry> _entries;
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, RowControls> _rows = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Button _cancelAll;
    private readonly Button _close;
    private bool _allDone;

    private sealed record RowControls(Label Status, ProgressBar Bar, Label Speed);

    public DownloadProgressForm(List<CatalogEntry> entries, IServiceProvider services)
    {
        _entries = entries;
        _services = services;

        Text = "Downloading Models";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 400);
        Size = new Size(900, Math.Min(800, 160 + entries.Count * 70));
        Font = new Font("Segoe UI", 9F);
        FormClosing += OnClosing;

        var header = new Label
        {
            Text = $"Downloading {entries.Count} model(s) to:  {Paths.ModelsDirectory}",
            Dock = DockStyle.Top,
            Padding = new Padding(16, 16, 16, 8),
            Height = 44,
            Font = new Font("Segoe UI", 10F),
        };

        var listPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(16, 8, 16, 8),
        };
        listPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var entry in entries)
        {
            var group = new GroupBox
            {
                Text = $"{entry.DisplayName}  ({entry.Quantization}, {FirstRunWizardForm.FormatSize(entry.TotalBytes)})",
                Dock = DockStyle.Top,
                Height = 78,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(8, 4, 8, 8),
            };
            var status = new Label { Text = "Queued", Dock = DockStyle.Top, Height = 18, ForeColor = Color.DimGray };
            var bar = new ProgressBar { Dock = DockStyle.Top, Height = 20, Style = ProgressBarStyle.Continuous, Maximum = 1000 };
            var speed = new Label { Text = "", Dock = DockStyle.Top, Height = 16, ForeColor = Color.DimGray };
            group.Controls.Add(speed);
            group.Controls.Add(bar);
            group.Controls.Add(status);
            listPanel.Controls.Add(group);
            _rows[entry.Id] = new RowControls(status, bar, speed);
        }

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 56,
            Padding = new Padding(16, 12, 16, 12),
        };
        _close = new Button { Text = "Continue", Width = 120, Height = 32, Enabled = false };
        _close.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _cancelAll = new Button { Text = "Cancel All", Width = 120, Height = 32, Margin = new Padding(8, 0, 0, 0) };
        _cancelAll.Click += (_, _) => _cts.Cancel();
        buttonPanel.Controls.Add(_close);
        buttonPanel.Controls.Add(_cancelAll);

        Controls.Add(listPanel);
        Controls.Add(buttonPanel);
        Controls.Add(header);

        Shown += async (_, _) => await RunDownloadsAsync();
    }

    private async Task RunDownloadsAsync()
    {
        var logger = _services.GetRequiredService<ILogger<DownloadProgressForm>>();
        var downloader = _services.GetRequiredService<ModelDownloader>();
        using var sem = new SemaphoreSlim(MaxParallel);

        var tasks = _entries.Select(async entry =>
        {
            await sem.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await DownloadEntryAsync(entry, downloader, logger).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            _allDone = true;
            BeginInvoke(() =>
            {
                _close.Enabled = true;
                _cancelAll.Enabled = false;
                Text = "Downloads Complete";
            });
        }
        catch (OperationCanceledException)
        {
            BeginInvoke(() =>
            {
                _cancelAll.Enabled = false;
                _close.Text = "Back";
                _close.Enabled = true;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download failed");
            BeginInvoke(() =>
            {
                MessageBox.Show(this, ex.Message, "Download Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _close.Text = "Back";
                _close.Enabled = true;
                _cancelAll.Enabled = false;
            });
        }
    }

    private async Task DownloadEntryAsync(CatalogEntry entry, ModelDownloader downloader, ILogger logger)
    {
        var row = _rows[entry.Id];
        long completedBytes = 0;
        long totalBytes = entry.TotalBytes;

        foreach (var file in entry.Files)
        {
            var dest = ModelCatalogService.ResolveDestinationPath(Paths.ModelsDirectory, entry, file);
            long thisFileBase = completedBytes;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                long overall = thisFileBase + p.BytesDownloaded;
                int promille = totalBytes > 0 ? (int)Math.Min(1000, overall * 1000 / totalBytes) : 0;
                BeginInvoke(() =>
                {
                    row.Bar.Value = promille;
                    row.Status.Text = p.Stage switch
                    {
                        DownloadStage.Downloading => $"Downloading {file.FileName}  ({FirstRunWizardForm.FormatSize(overall)} / {FirstRunWizardForm.FormatSize(totalBytes)})",
                        DownloadStage.Verifying => $"Verifying {file.FileName}",
                        DownloadStage.Completed => $"Completed {file.FileName}",
                        DownloadStage.Failed => $"Failed: {file.FileName}",
                        DownloadStage.Cancelled => "Cancelled",
                        _ => p.Stage.ToString(),
                    };
                    row.Speed.Text = p.BytesPerSecond > 0
                        ? $"{FirstRunWizardForm.FormatSize((long)p.BytesPerSecond)}/s   ETA {FormatEta(p.Eta)}"
                        : "";
                });
            });

            try
            {
                await downloader.DownloadAsync(file.Url, dest, file.SizeBytes, file.Sha256, progress, _cts.Token).ConfigureAwait(false);
                completedBytes += file.SizeBytes > 0 ? file.SizeBytes : (File.Exists(dest) ? new FileInfo(dest).Length : 0);
            }
            catch (OperationCanceledException)
            {
                BeginInvoke(() => { row.Status.Text = "Cancelled"; row.Status.ForeColor = Color.DimGray; });
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download failed for {File}", file.FileName);
                BeginInvoke(() => { row.Status.Text = $"Failed: {ex.Message}"; row.Status.ForeColor = Color.Firebrick; });
                throw;
            }
        }

        BeginInvoke(() =>
        {
            row.Status.Text = "Done";
            row.Status.ForeColor = Color.ForestGreen;
            row.Bar.Value = 1000;
            row.Speed.Text = "";
        });
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero) return "—";
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}h {eta.Minutes}m";
        if (eta.TotalMinutes >= 1) return $"{eta.Minutes}m {eta.Seconds}s";
        return $"{eta.Seconds}s";
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allDone && DialogResult == DialogResult.None)
        {
            var ok = MessageBox.Show(this,
                "Downloads are still in progress. Cancel and exit?",
                "Cancel downloads?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) { e.Cancel = true; return; }
            _cts.Cancel();
        }
    }
}
