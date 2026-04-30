using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class ModelsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _downloadBtn;
    private readonly ToolStripButton _cancelBtn;
    private readonly ToolStripButton _activateBtn;
    private readonly ToolStripButton _deleteBtn;
    private readonly ToolStripLabel _statusLbl;
    private readonly ToolStripLabel _embeddingLbl;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly BindingList<ModelRow> _rows = new();

    public ModelsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _downloadBtn = new ToolStripButton("Download") { Enabled = false };
        _cancelBtn = new ToolStripButton("Cancel download") { Enabled = false };
        _activateBtn = new ToolStripButton("Activate") { Enabled = false };
        _deleteBtn = new ToolStripButton("Delete files") { Enabled = false };
        _statusLbl = new ToolStripLabel("  Chat: (none)") { ForeColor = SystemColors.GrayText };
        _embeddingLbl = new ToolStripLabel("  Embedding: (none)") { ForeColor = SystemColors.GrayText };
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _refreshBtn, new ToolStripSeparator(), _downloadBtn, _cancelBtn, _activateBtn, _deleteBtn,
            new ToolStripSeparator(), _statusLbl, _embeddingLbl,
        });

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };
        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Model", DataPropertyName = nameof(ModelRow.DisplayName), Width = 220 },
            new DataGridViewTextBoxColumn { HeaderText = "Tier", DataPropertyName = nameof(ModelRow.Tier), Width = 90 },
            new DataGridViewTextBoxColumn { HeaderText = "Quant", DataPropertyName = nameof(ModelRow.Quantization), Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Size", DataPropertyName = nameof(ModelRow.SizeText), Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "RAM", DataPropertyName = nameof(ModelRow.MinRamGb), Width = 50 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(ModelRow.StatusText), Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "License", DataPropertyName = nameof(ModelRow.License), Width = 110 },
        });
        _grid.DataSource = _rows;
        MyLocalAssistant.Admin.UI.UiTheme.StyleGrid(_grid);

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _downloadBtn.Click += async (_, _) => await OnDownloadAsync();
        _cancelBtn.Click += async (_, _) => await OnCancelAsync();
        _activateBtn.Click += async (_, _) => await OnActivateAsync();
        _deleteBtn.Click += async (_, _) => await OnDeleteAsync();
        _grid.SelectionChanged += (_, _) => UpdateButtonState();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += async (_, _) => await PollAsync();

        Load += async (_, _) => { await ReloadAsync(); _pollTimer.Start(); };
        Disposed += (_, _) => _pollTimer.Dispose();
    }

    private ModelRow? Selected =>
        _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].DataBoundItem as ModelRow;

    private void UpdateButtonState()
    {
        var sel = Selected;
        var dlActive = sel?.IsDownloading == true;
        _downloadBtn.Enabled = sel is { IsInstalled: false, IsDownloading: false };
        _cancelBtn.Enabled = dlActive;
        _activateBtn.Enabled = sel is { IsInstalled: true, IsActive: false, IsActiveEmbedding: false };
        _deleteBtn.Enabled = sel is { IsInstalled: true, IsActive: false, IsActiveEmbedding: false, IsDownloading: false };
    }

    private async Task ReloadAsync()
    {
        try
        {
            var models = await _client.ListModelsAsync();
            var status = await _client.GetModelStatusAsync();
            var emb = await _client.GetEmbeddingStatusAsync();
            _rows.Clear();
            foreach (var m in models) _rows.Add(ModelRow.From(m));
            _statusLbl.Text = $"  Chat: {status.ActiveModelId ?? "(none)"} — {status.Status}" +
                (string.IsNullOrEmpty(status.LastError) ? "" : $" — {status.LastError}") +
                $" — {status.Backend}";
            _embeddingLbl.Text = $"  Embedding: {emb.ActiveModelId ?? "(none)"} — {emb.Status}" +
                (emb.EmbeddingDimension > 0 ? $" (dim={emb.EmbeddingDimension})" : "") +
                (string.IsNullOrEmpty(emb.LastError) ? "" : $" — {emb.LastError}");
            _statusLabel.Text = $"{models.Count} model(s).";
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { UpdateButtonState(); }
    }

    private async Task PollAsync()
    {
        // Refresh only when something is in flight (downloading or loading), to keep traffic low.
        var anyBusy = _rows.Any(r => r.IsDownloading) ||
                      (_statusLbl.Text?.Contains("Loading", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!anyBusy) return;
        try
        {
            var models = await _client.ListModelsAsync();
            var status = await _client.GetModelStatusAsync();
            var emb = await _client.GetEmbeddingStatusAsync();
            // In-place update to preserve selection.
            for (int i = 0; i < _rows.Count; i++)
            {
                var fresh = models.FirstOrDefault(m => m.Id == _rows[i].Id);
                if (fresh is not null) _rows[i] = ModelRow.From(fresh);
            }
            _statusLbl.Text = $"  Chat: {status.ActiveModelId ?? "(none)"} — {status.Status}" +
                (string.IsNullOrEmpty(status.LastError) ? "" : $" — {status.LastError}") +
                $" — {status.Backend}";
            _embeddingLbl.Text = $"  Embedding: {emb.ActiveModelId ?? "(none)"} — {emb.Status}" +
                (emb.EmbeddingDimension > 0 ? $" (dim={emb.EmbeddingDimension})" : "") +
                (string.IsNullOrEmpty(emb.LastError) ? "" : $" — {emb.LastError}");
        }
        catch { /* swallow during polling */ }
    }

    private async Task OnDownloadAsync()
    {
        var sel = Selected; if (sel is null) return;
        if (MessageBox.Show(this,
            $"Download '{sel.DisplayName}'?\n\nThis will fetch ~{FormatBytes(sel.TotalBytes)} from {sel.HostName}.",
            "Download model", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            await _client.StartDownloadAsync(sel.Id);
            _statusLabel.Text = $"Started download of {sel.Id}.";
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError("Download start failed", ex); }
    }

    private async Task OnCancelAsync()
    {
        var sel = Selected; if (sel is null) return;
        try { await _client.CancelDownloadAsync(sel.Id); _statusLabel.Text = $"Cancel requested for {sel.Id}."; await ReloadAsync(); }
        catch (Exception ex) { ShowError("Cancel failed", ex); }
    }

    private async Task OnActivateAsync()
    {
        var sel = Selected; if (sel is null) return;
        try
        {
            if (string.Equals(sel.Tier, "Embedding", StringComparison.OrdinalIgnoreCase))
                await _client.ActivateEmbeddingAsync(sel.Id);
            else
                await _client.ActivateModelAsync(sel.Id);
            _statusLabel.Text = $"Activating {sel.Id}…";
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError("Activate failed", ex); }
    }

    private async Task OnDeleteAsync()
    {
        var sel = Selected; if (sel is null) return;
        if (MessageBox.Show(this, $"Delete local files for '{sel.DisplayName}'?",
            "Delete model files", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        try { await _client.DeleteModelAsync(sel.Id); _statusLabel.Text = $"Deleted {sel.Id}."; await ReloadAsync(); }
        catch (Exception ex) { ShowError("Delete failed", ex); }
    }

    private void ShowError(string title, Exception ex)
    {
        _statusLabel.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        double v = b;
        string[] u = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return $"{v:0.#} {u[i]}";
    }

    private sealed class ModelRow
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Quantization { get; set; } = "";
        public long TotalBytes { get; set; }
        public string SizeText { get; set; } = "";
        public int MinRamGb { get; set; }
        public string License { get; set; } = "";
        public string HostName { get; set; } = "";
        public bool IsInstalled { get; set; }
        public bool IsActive { get; set; }
        public bool IsActiveEmbedding { get; set; }
        public bool IsDownloading { get; set; }
        public string StatusText { get; set; } = "";

        public static ModelRow From(ModelDto m)
        {
            var row = new ModelRow
            {
                Id = m.Id,
                DisplayName = m.DisplayName,
                Tier = m.Tier,
                Quantization = m.Quantization,
                TotalBytes = m.TotalBytes,
                SizeText = FormatBytes(m.TotalBytes),
                MinRamGb = m.MinRamGb,
                License = m.License,
                IsInstalled = m.IsInstalled,
                IsActive = m.IsActive,
                IsActiveEmbedding = m.IsActiveEmbedding,
            };
            // Best-effort host name from any HuggingFace URL pattern.
            row.HostName = m.LicenseUrl.StartsWith("http") ? new Uri(m.LicenseUrl).Host : "huggingface.co";

            if (m.Download is { } d)
            {
                bool stillRunning = d.Stage is "Queued" or "Downloading" or "Verifying";
                row.IsDownloading = stillRunning;
                if (d.Stage == "Downloading" && d.TotalBytes > 0)
                {
                    var pct = (int)(d.Bytes * 100.0 / d.TotalBytes);
                    var mbps = d.BytesPerSecond / (1024 * 1024);
                    row.StatusText = $"Downloading {pct}% ({mbps:0.0} MB/s)";
                }
                else if (d.Stage == "Verifying")
                    row.StatusText = "Verifying…";
                else if (d.Stage == "Failed")
                    row.StatusText = $"Failed: {d.Error}";
                else if (d.Stage == "Cancelled")
                    row.StatusText = "Cancelled";
                else if (d.Stage == "Completed")
                    row.StatusText = "Installed";
                else
                    row.StatusText = d.Stage;
            }
            else if (m.IsActive)
                row.StatusText = "Installed (active chat)";
            else if (m.IsActiveEmbedding)
                row.StatusText = "Installed (active embedding)";
            else if (m.IsInstalled)
                row.StatusText = "Installed";
            else
                row.StatusText = "Available";
            return row;
        }
    }
}
