using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class AgentsTab : UserControl
{
    private const string NoModelOverride = "(use active model)";

    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripLabel _hint;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<AgentRow> _rows = new();
    private DataGridViewComboBoxColumn _modelCol = null!;
    private DataGridViewButtonColumn _collectionsCol = null!;
    private DataGridViewButtonColumn _promptCol = null!;
    private readonly ToolStripButton _globalPromptBtn;
    private List<RagCollectionDto> _allCollections = new();
    private bool _suppressEvents;

    public AgentsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _globalPromptBtn = new ToolStripButton("Edit global system prompt…");
        _hint = new ToolStripLabel("  Toggle Enabled/RAG, pick a default model, click RAG to choose collections, click Prompt to edit.")
        {
            ForeColor = SystemColors.GrayText,
        };
        _toolbar.Items.AddRange(new ToolStripItem[] { _refreshBtn, _globalPromptBtn, new ToolStripSeparator(), _hint });

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
        _modelCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Default model",
            DataPropertyName = nameof(AgentRow.DefaultModelDisplay),
            Width = 240,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        _collectionsCol = new DataGridViewButtonColumn
        {
            HeaderText = "RAG collections",
            DataPropertyName = nameof(AgentRow.CollectionsDisplay),
            Width = 200,
            UseColumnTextForButtonValue = false,
            FlatStyle = FlatStyle.Standard,
        };
        _promptCol = new DataGridViewButtonColumn
        {
            HeaderText = "System prompt",
            Text = "Edit…",
            UseColumnTextForButtonValue = true,
            Width = 80,
            FlatStyle = FlatStyle.Standard,
        };
        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Agent", DataPropertyName = nameof(AgentRow.Name), Width = 180, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Category", DataPropertyName = nameof(AgentRow.Category), Width = 100, ReadOnly = true },
            new DataGridViewCheckBoxColumn { HeaderText = "Generic", DataPropertyName = nameof(AgentRow.IsGeneric), Width = 60, ReadOnly = true },
            new DataGridViewCheckBoxColumn { HeaderText = "Enabled", DataPropertyName = nameof(AgentRow.Enabled), Width = 60 },
            _modelCol,
            new DataGridViewCheckBoxColumn { HeaderText = "RAG", DataPropertyName = nameof(AgentRow.RagEnabled), Width = 50 },
            _collectionsCol,
            _promptCol,
            new DataGridViewTextBoxColumn { HeaderText = "Description", DataPropertyName = nameof(AgentRow.Description), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true },
        });
        _grid.DataSource = _rows;
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
        _globalPromptBtn.Click += async (_, _) => await EditGlobalPromptAsync();
        Load += async (_, _) => await ReloadAsync();
    }

    private async Task EditGlobalPromptAsync()
    {
        try
        {
            var current = await _client.GetGlobalSystemPromptAsync();
            using var dlg = new PromptEditorForm("Global system prompt",
                "Prepended to every chat (before each agent's own system prompt). Max 8 KB. Empty = disabled.",
                current, 8 * 1024);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var saved = await _client.SetGlobalSystemPromptAsync(dlg.PromptText);
            _statusLabel.Text = saved.Length == 0 ? "Global system prompt cleared." : $"Global system prompt saved ({saved.Length} chars).";
        }
        catch (Exception ex) { ShowError("Global prompt", ex); }
    }

    private async Task ReloadAsync()
    {
        SetBusy(true, "Loading…");
        try
        {
            var modelTask = _client.ListModelsAsync();
            var collTask = _client.ListCollectionsAsync();
            var agents = await _client.ListAgentsAsync();
            var models = await modelTask;
            _allCollections = await collTask;
            var modelChoices = new List<string> { NoModelOverride };
            modelChoices.AddRange(models.Where(m => m.IsInstalled).Select(m => m.Id));
            _modelCol.DataSource = modelChoices;

            _suppressEvents = true;
            _rows.Clear();
            foreach (var a in agents) _rows.Add(AgentRow.From(a, modelChoices, _allCollections));
            _suppressEvents = false;
            _statusLabel.Text = $"{agents.Count} agent(s); {models.Count(m => m.IsInstalled)} installed model(s); {_allCollections.Count} collection(s).";
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private void OnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        if (e.ColumnIndex == _promptCol.Index)
        {
            using var dlg = new PromptEditorForm($"System prompt \u2014 {row.Name}",
                "Sent for every chat with this agent (after the global system prompt). Max 8 KB.",
                row.SystemPrompt, 8 * 1024);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            row.SystemPrompt = dlg.PromptText;
            _ = SaveRowAsync(row);
            return;
        }
        if (e.ColumnIndex != _collectionsCol.Index) return;
        using (var dlg = new CollectionPickerForm(_allCollections, row.RagCollectionIds))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            row.RagCollectionIds = dlg.SelectedIds;
        }
        row.RecomputeCollectionsDisplay(_allCollections);
        _ = SaveRowAsync(row);
        _grid.InvalidateRow(e.RowIndex);
    }

    private async Task SaveRowAsync(AgentRow row)
    {
        try
        {
            var defaultModelId = row.DefaultModelDisplay == NoModelOverride ? null : row.DefaultModelDisplay;
            var updated = await _client.UpdateAgentAsync(row.Id,
                new AgentUpdateRequest(row.Enabled, defaultModelId, row.RagEnabled, row.RagCollectionIds, row.SystemPrompt));
            _statusLabel.Text = $"Saved {updated.Name} (rag={updated.RagEnabled}, collections={updated.RagCollectionIds.Count}).";
        }
        catch (Exception ex) { ShowError("Save failed", ex); await ReloadAsync(); }
    }

    private async void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressEvents || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        if (e.ColumnIndex == _collectionsCol.Index || e.ColumnIndex == _promptCol.Index) return; // handled by content-click
        await SaveRowAsync(_rows[e.RowIndex]);
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

    private sealed class AgentRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsGeneric { get; set; }
        public bool Enabled { get; set; }
        public string DefaultModelDisplay { get; set; } = NoModelOverride;
        public bool RagEnabled { get; set; }
        public IReadOnlyList<Guid> RagCollectionIds { get; set; } = Array.Empty<Guid>();
        public string CollectionsDisplay { get; set; } = "(none)";
        public string SystemPrompt { get; set; } = "";

        public static AgentRow From(AgentDto a, IReadOnlyList<string> modelChoices, IReadOnlyList<RagCollectionDto> allCollections)
        {
            var display = a.DefaultModelId is null
                ? NoModelOverride
                : modelChoices.Contains(a.DefaultModelId) ? a.DefaultModelId : NoModelOverride;
            var row = new AgentRow
            {
                Id = a.Id,
                Name = a.Name,
                Category = a.Category,
                Description = a.Description,
                IsGeneric = a.IsGeneric,
                Enabled = a.Enabled,
                DefaultModelDisplay = display,
                RagEnabled = a.RagEnabled,
                RagCollectionIds = a.RagCollectionIds,
                SystemPrompt = a.SystemPrompt,
            };
            row.RecomputeCollectionsDisplay(allCollections);
            return row;
        }

        public void RecomputeCollectionsDisplay(IReadOnlyList<RagCollectionDto> all)
        {
            if (RagCollectionIds.Count == 0) { CollectionsDisplay = "(choose…)"; return; }
            var names = RagCollectionIds
                .Select(id => all.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString())
                .ToList();
            CollectionsDisplay = string.Join(", ", names);
        }
    }
}

internal sealed class CollectionPickerForm : Form
{
    private readonly CheckedListBox _list;
    private readonly List<RagCollectionDto> _all;

    public IReadOnlyList<Guid> SelectedIds { get; private set; } = Array.Empty<Guid>();

    public CollectionPickerForm(IReadOnlyList<RagCollectionDto> all, IReadOnlyList<Guid> initiallySelected)
    {
        _all = all.ToList();
        Text = "Select RAG collections";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 360);
        Font = new Font("Segoe UI", 9F);

        _list = new CheckedListBox
        {
            Left = 12, Top = 12, Width = 356, Height = 290,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        var initialSet = initiallySelected.ToHashSet();
        foreach (var c in _all)
        {
            var label = $"{c.Name}  ({c.DocumentCount} doc(s))";
            _list.Items.Add(label, initialSet.Contains(c.Id));
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 200, Top = 312, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 288, Top = 312, Width = 80 };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            var picks = new List<Guid>();
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.GetItemChecked(i)) picks.Add(_all[i].Id);
            SelectedIds = picks;
        };
        Controls.AddRange(new Control[] { _list, ok, cancel });
    }
}
