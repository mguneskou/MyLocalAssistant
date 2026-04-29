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
    private bool _suppressEvents;

    public AgentsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _hint = new ToolStripLabel("  Agent prompts are sealed; admins can toggle Enabled and pick a Default Model.")
        {
            ForeColor = SystemColors.GrayText,
        };
        _toolbar.Items.AddRange(new ToolStripItem[] { _refreshBtn, new ToolStripSeparator(), _hint });

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
        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Agent", DataPropertyName = nameof(AgentRow.Name), Width = 200, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Category", DataPropertyName = nameof(AgentRow.Category), Width = 110, ReadOnly = true },
            new DataGridViewCheckBoxColumn { HeaderText = "Generic", DataPropertyName = nameof(AgentRow.IsGeneric), Width = 70, ReadOnly = true },
            new DataGridViewCheckBoxColumn { HeaderText = "Enabled", DataPropertyName = nameof(AgentRow.Enabled), Width = 70 },
            _modelCol,
            new DataGridViewTextBoxColumn { HeaderText = "Description", DataPropertyName = nameof(AgentRow.Description), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true },
        });
        _grid.DataSource = _rows;
        _grid.CellValueChanged += OnCellValueChanged;
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
        Load += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        SetBusy(true, "Loading…");
        try
        {
            var modelTask = _client.ListModelsAsync();
            var agents = await _client.ListAgentsAsync();
            var models = await modelTask;
            var modelChoices = new List<string> { NoModelOverride };
            modelChoices.AddRange(models.Where(m => m.IsInstalled).Select(m => m.Id));
            _modelCol.DataSource = modelChoices;

            _suppressEvents = true;
            _rows.Clear();
            foreach (var a in agents) _rows.Add(AgentRow.From(a, modelChoices));
            _suppressEvents = false;
            _statusLabel.Text = $"{agents.Count} agent(s); {models.Count(m => m.IsInstalled)} installed model(s).";
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private async void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressEvents || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        try
        {
            var defaultModelId = row.DefaultModelDisplay == NoModelOverride ? null : row.DefaultModelDisplay;
            var updated = await _client.UpdateAgentAsync(row.Id, new AgentUpdateRequest(row.Enabled, defaultModelId));
            _statusLabel.Text = $"Saved {updated.Name} (enabled={updated.Enabled}, model={updated.DefaultModelId ?? "—"}).";
        }
        catch (Exception ex)
        {
            ShowError("Save failed", ex);
            await ReloadAsync(); // resync to server state
        }
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

        public static AgentRow From(AgentDto a, IReadOnlyList<string> modelChoices)
        {
            var display = a.DefaultModelId is null
                ? NoModelOverride
                : modelChoices.Contains(a.DefaultModelId) ? a.DefaultModelId : NoModelOverride;
            return new AgentRow
            {
                Id = a.Id,
                Name = a.Name,
                Category = a.Category,
                Description = a.Description,
                IsGeneric = a.IsGeneric,
                Enabled = a.Enabled,
                DefaultModelDisplay = display,
            };
        }
    }
}
