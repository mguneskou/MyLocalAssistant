using System.ComponentModel;
using System.Text.Json;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Admin.UI;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

/// <summary>
/// Global-admin-only console for the skill catalog. Phase 1: enable/disable each skill,
/// edit per-skill <c>ConfigJson</c>, inspect tools and capability requirements. Plug-in
/// install/uninstall lands in Phase 3.
/// </summary>
internal sealed class SkillsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _reloadBtn;
    private readonly ToolStripLabel _hint;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<SkillRow> _rows = new();
    private DataGridViewButtonColumn _toolsCol = null!;
    private DataGridViewButtonColumn _configCol = null!;
    private bool _suppressEvents;
    private List<SkillDto> _last = new();

    public SkillsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _reloadBtn = new ToolStripButton("Reload plug-ins") { ToolTipText = "Rescan ./plugins/ on the server. Owner-only." };
        _hint = new ToolStripLabel("  Toggle Enabled, click Tools to inspect, click Config to edit JSON. Disabled skills are not exposed to any agent.")
        {
            ForeColor = SystemColors.GrayText,
        };
        _toolbar.Items.AddRange(new ToolStripItem[] { _refreshBtn, _reloadBtn, new ToolStripSeparator(), _hint });

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        _toolsCol = new DataGridViewButtonColumn
        {
            HeaderText = "Tools",
            DataPropertyName = nameof(SkillRow.ToolsDisplay),
            Width = 200,
            UseColumnTextForButtonValue = false,
            FlatStyle = FlatStyle.Standard,
        };
        _configCol = new DataGridViewButtonColumn
        {
            HeaderText = "Config",
            Text = "Edit\u2026",
            UseColumnTextForButtonValue = true,
            Width = 70,
            FlatStyle = FlatStyle.Standard,
        };
        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Skill",       DataPropertyName = nameof(SkillRow.Name),       Width = 180, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Id",          DataPropertyName = nameof(SkillRow.Id),         Width = 140, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Source",      DataPropertyName = nameof(SkillRow.Source),     Width = 70,  ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Version",     DataPropertyName = nameof(SkillRow.Version),    Width = 70,  ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Publisher",   DataPropertyName = nameof(SkillRow.Publisher),  Width = 120, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Signed by",   DataPropertyName = nameof(SkillRow.KeyId),      Width = 90,  ReadOnly = true, ToolTipText = "Trusted-key id from config/trusted-keys/" },
            new DataGridViewCheckBoxColumn { HeaderText = "Enabled",    DataPropertyName = nameof(SkillRow.Enabled),    Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "Tool mode",   DataPropertyName = nameof(SkillRow.ToolsMode),  Width = 80,  ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Min ctx",     DataPropertyName = nameof(SkillRow.MinContextK), Width = 60,  ReadOnly = true },
            _toolsCol,
            _configCol,
            new DataGridViewTextBoxColumn { HeaderText = "Description", DataPropertyName = nameof(SkillRow.Description), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true },
        });
        _grid.DataSource = _rows;
        UiTheme.StyleGrid(_grid);
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CellContentClick += OnCellContentClick;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _reloadBtn.Click += async (_, _) =>
        {
            SetBusy(true, "Reloading plug-ins…");
            try
            {
                var n = await _client.ReloadPluginsAsync();
                await ReloadAsync();
                _statusLabel.Text = $"Reloaded: {n} plug-in(s) registered.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Reload failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetBusy(false); }
        };
        Load += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        SetBusy(true, "Loading skills\u2026");
        try
        {
            _last = await _client.ListSkillsAsync();
            _suppressEvents = true;
            _rows.Clear();
            foreach (var s in _last) _rows.Add(SkillRow.From(s));
            _suppressEvents = false;
            _statusLabel.Text = $"{_last.Count} skill(s); {_last.Count(s => s.Enabled)} enabled.";
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private void OnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var dto = _last.FirstOrDefault(s => s.Id == row.Id);
        if (dto is null) return;

        if (e.ColumnIndex == _toolsCol.Index)
        {
            ShowToolsDialog(dto);
            return;
        }
        if (e.ColumnIndex == _configCol.Index)
        {
            using var dlg = new PromptEditorForm($"Config JSON \u2014 {dto.Name}",
                "Free-form JSON the skill validates on save. Empty = no config. Max 32 KB.",
                row.ConfigJson ?? "", 32 * 1024);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var newConfig = string.IsNullOrWhiteSpace(dlg.PromptText) ? null : dlg.PromptText;
            if (!string.IsNullOrWhiteSpace(newConfig))
            {
                try { using var _ = JsonDocument.Parse(newConfig); }
                catch (JsonException jex) { ShowError("Invalid JSON", jex); return; }
            }
            row.ConfigJson = newConfig;
            _ = SaveRowAsync(row);
        }
    }

    private async void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressEvents || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        if (e.ColumnIndex == _toolsCol.Index || e.ColumnIndex == _configCol.Index) return;
        await SaveRowAsync(_rows[e.RowIndex]);
    }

    private async Task SaveRowAsync(SkillRow row)
    {
        try
        {
            var updated = await _client.UpdateSkillAsync(row.Id, new SkillUpdateRequest(row.Enabled, row.ConfigJson));
            _statusLabel.Text = $"Saved {updated.Name} (enabled={updated.Enabled}, configChars={updated.ConfigJson?.Length ?? 0}).";
            // Refresh cached DTO so subsequent dialogs see the persisted value.
            var idx = _last.FindIndex(s => s.Id == row.Id);
            if (idx >= 0) _last[idx] = updated;
        }
        catch (Exception ex) { ShowError("Save failed", ex); await ReloadAsync(); }
    }

    private void ShowToolsDialog(SkillDto dto)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Skill: {dto.Name} ({dto.Id})");
        sb.AppendLine($"Source: {dto.Source}{(dto.Version is null ? "" : $" v{dto.Version}")}");
        sb.AppendLine($"Requires: tool-mode={dto.Requires.Tools}, min context={dto.Requires.MinContextK}k");
        sb.AppendLine();
        sb.AppendLine($"Tools ({dto.Tools.Count}):");
        foreach (var t in dto.Tools)
        {
            sb.AppendLine();
            sb.AppendLine($"  {t.Name}");
            sb.AppendLine($"    {t.Description}");
            sb.AppendLine("    Arguments schema:");
            foreach (var line in (t.ArgumentsSchemaJson ?? "").Replace("\r", "").Split('\n'))
                sb.AppendLine("      " + line);
        }
        using var f = new Form
        {
            Text = $"Tools \u2014 {dto.Name}",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(720, 520),
            MinimumSize = new Size(520, 360),
        };
        UiTheme.ApplyDialog(f);
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = UiTheme.Mono,
            WordWrap = false,
            Text = sb.ToString(),
        };
        var ok = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        ok.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        ok.Location = new Point(bottom.Width - ok.Width - 8, 8);
        bottom.Controls.Add(ok);
        f.AcceptButton = ok;
        f.CancelButton = ok;
        f.Controls.Add(box);
        f.Controls.Add(bottom);
        f.ShowDialog(this);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _toolbar.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (message is not null) _statusLabel.Text = message;
    }

    private void ShowError(string title, Exception ex)
    {
        _statusLabel.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private sealed class SkillRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? KeyId { get; set; }
        public bool Enabled { get; set; }
        public string ToolsMode { get; set; } = "";
        public int MinContextK { get; set; }
        public string ToolsDisplay { get; set; } = "";
        public string? ConfigJson { get; set; }
        public string Description { get; set; } = "";

        public static SkillRow From(SkillDto s) => new()
        {
            Id = s.Id,
            Name = s.Name,
            Source = s.Source,
            Version = s.Version,
            Publisher = s.Publisher,
            KeyId = s.KeyId,
            Enabled = s.Enabled,
            ToolsMode = s.Requires.Tools,
            MinContextK = s.Requires.MinContextK,
            ToolsDisplay = s.Tools.Count == 0
                ? "(no tools)"
                : (s.Tools.Count == 1 ? s.Tools[0].Name : $"{s.Tools.Count} tools\u2026"),
            ConfigJson = s.ConfigJson,
            Description = s.Description,
        };
    }
}
