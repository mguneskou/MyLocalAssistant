using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class DepartmentsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripLabel _hint;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<DepartmentDto> _rows = new();

    public DepartmentsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _hint = new ToolStripLabel("  Departments mirror the built-in agent catalog and are read-only.")
        {
            ForeColor = SystemColors.GrayText,
        };
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _refreshBtn, new ToolStripSeparator(), _hint,
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
            new DataGridViewTextBoxColumn { HeaderText = "Department", DataPropertyName = nameof(DepartmentDto.Name), Width = 260 },
            new DataGridViewTextBoxColumn { HeaderText = "Members", DataPropertyName = nameof(DepartmentDto.UserCount), Width = 90 },
        });
        _grid.DataSource = _rows;

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
            var depts = await _client.ListDepartmentsAsync();
            _rows.Clear();
            foreach (var d in depts) _rows.Add(d);
            _statusLabel.Text = $"{depts.Count} department(s).";
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
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
}
