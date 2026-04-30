using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class AuditTab : UserControl
{
    private const int PageSize = 200;

    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;
    private readonly ToolStripComboBox _actionCombo;
    private readonly ToolStripTextBox _userBox;
    private readonly ToolStripComboBox _successCombo;
    private readonly ToolStripButton _searchBtn;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _exportBtn;
    private readonly ToolStripButton _prevBtn;
    private readonly ToolStripButton _nextBtn;
    private readonly ToolStripLabel _pageLabel;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<AuditEntryDto> _rows = new();
    private int _skip;
    private int _total;

    public AuditTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _from = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm",
            ShowUpDown = false,
            Width = 130,
            Value = DateTime.Now.AddDays(-7),
        };
        _to = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm",
            ShowUpDown = false,
            Width = 130,
            Value = DateTime.Now.AddMinutes(5),
        };
        _actionCombo = new ToolStripComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _actionCombo.Items.Add("(any action)");
        _actionCombo.SelectedIndex = 0;
        _userBox = new ToolStripTextBox { Width = 140 };
        _userBox.ToolTipText = "User name (substring) or user id (Guid)";
        _successCombo = new ToolStripComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        _successCombo.Items.AddRange(new object[] { "any", "ok", "fail" });
        _successCombo.SelectedIndex = 0;

        _searchBtn = new ToolStripButton("Search");
        _searchBtn.Click += async (_, _) => { _skip = 0; await ReloadAsync(); };
        _refreshBtn = new ToolStripButton("Refresh");
        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _exportBtn = new ToolStripButton("Export CSV\u2026");
        _exportBtn.Click += async (_, _) => await OnExportAsync();
        _prevBtn = new ToolStripButton("\u25C0 Prev") { Enabled = false };
        _prevBtn.Click += async (_, _) => { _skip = Math.Max(0, _skip - PageSize); await ReloadAsync(); };
        _nextBtn = new ToolStripButton("Next \u25B6") { Enabled = false };
        _nextBtn.Click += async (_, _) => { _skip += PageSize; await ReloadAsync(); };
        _pageLabel = new ToolStripLabel("");

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("From:"),
            new ToolStripControlHost(_from),
            new ToolStripLabel("To:"),
            new ToolStripControlHost(_to),
            new ToolStripSeparator(),
            new ToolStripLabel("Action:"),
            _actionCombo,
            new ToolStripLabel("User:"),
            _userBox,
            new ToolStripLabel("Success:"),
            _successCombo,
            new ToolStripSeparator(),
            _searchBtn, _refreshBtn, _exportBtn,
            new ToolStripSeparator(),
            _prevBtn, _pageLabel, _nextBtn,
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
            new DataGridViewTextBoxColumn { HeaderText = "When", DataPropertyName = nameof(AuditEntryDto.Timestamp), Width = 150,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm:ss" } },
            new DataGridViewTextBoxColumn { HeaderText = "User", DataPropertyName = nameof(AuditEntryDto.Username), Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "Action", DataPropertyName = nameof(AuditEntryDto.Action), Width = 150 },
            new DataGridViewTextBoxColumn { HeaderText = "Agent", DataPropertyName = nameof(AuditEntryDto.AgentId), Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "OK", DataPropertyName = nameof(AuditEntryDto.Success), Width = 40 },
            new DataGridViewTextBoxColumn { HeaderText = "IP", DataPropertyName = nameof(AuditEntryDto.IpAddress), Width = 110 },
            new DataGridViewTextBoxColumn { HeaderText = "Detail", DataPropertyName = nameof(AuditEntryDto.Detail), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill },
        });
        _grid.DataSource = _rows;
        MyLocalAssistant.Admin.UI.UiTheme.StyleGrid(_grid);
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            var r = _rows[e.RowIndex];
            var msg = $"When: {r.Timestamp:O}\nUser: {r.Username} ({r.UserId})\nAction: {r.Action}\nAgent: {r.AgentId}\nIP: {r.IpAddress}\nSuccess: {r.Success}\n\nDetail:\n{r.Detail}";
            MessageBox.Show(this, msg, $"Audit row #{r.Id}", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var actions = await _client.ListAuditActionsAsync();
            _actionCombo.Items.Clear();
            _actionCombo.Items.Add("(any action)");
            foreach (var a in actions) _actionCombo.Items.Add(a);
            _actionCombo.SelectedIndex = 0;
        }
        catch (Exception ex) { _statusLabel.Text = "Failed to load actions: " + ex.Message; }
        await ReloadAsync();
    }

    private (DateTimeOffset? from, DateTimeOffset? to, string? action, string? user, bool? success) CurrentFilter()
    {
        DateTimeOffset? from = new DateTimeOffset(_from.Value);
        DateTimeOffset? to = new DateTimeOffset(_to.Value);
        string? action = _actionCombo.SelectedIndex > 0 ? _actionCombo.SelectedItem as string : null;
        string? user = string.IsNullOrWhiteSpace(_userBox.Text) ? null : _userBox.Text.Trim();
        bool? success = _successCombo.SelectedIndex switch { 1 => true, 2 => false, _ => (bool?)null };
        return (from, to, action, user, success);
    }

    private async Task ReloadAsync()
    {
        try
        {
            _statusLabel.Text = "Loading\u2026";
            var (from, to, action, user, success) = CurrentFilter();
            var page = await _client.ListAuditAsync(from, to, action, user, success, _skip, PageSize);
            _total = page.Total;
            _rows.RaiseListChangedEvents = false;
            _rows.Clear();
            foreach (var r in page.Items) _rows.Add(r);
            _rows.RaiseListChangedEvents = true;
            _rows.ResetBindings();

            var first = page.Total == 0 ? 0 : _skip + 1;
            var last = Math.Min(_skip + page.Items.Count, page.Total);
            _pageLabel.Text = $"{first}\u2013{last} of {page.Total}";
            _prevBtn.Enabled = _skip > 0;
            _nextBtn.Enabled = _skip + PageSize < page.Total;
            _statusLabel.Text = $"{page.Items.Count} row(s) shown.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Load failed: " + ex.Message;
        }
    }

    private async Task OnExportAsync()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|All files|*.*",
            FileName = $"audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExt = "csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _statusLabel.Text = "Exporting\u2026";
            var (from, to, action, user, success) = CurrentFilter();
            await _client.DownloadAuditCsvAsync(dlg.FileName, from, to, action, user, success);
            _statusLabel.Text = "Saved " + dlg.FileName;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Export failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
