using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class DepartmentsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _newBtn;
    private readonly ToolStripButton _renameBtn;
    private readonly ToolStripButton _deleteBtn;
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
        _newBtn = new ToolStripButton("New department…");
        _renameBtn = new ToolStripButton("Rename…") { Enabled = false };
        _deleteBtn = new ToolStripButton("Delete") { Enabled = false };
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _refreshBtn, new ToolStripSeparator(), _newBtn, _renameBtn, _deleteBtn,
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
            new DataGridViewTextBoxColumn { HeaderText = "Department", DataPropertyName = nameof(DepartmentDto.Name), Width = 240 },
            new DataGridViewTextBoxColumn { HeaderText = "Members", DataPropertyName = nameof(DepartmentDto.UserCount), Width = 80 },
        });
        _grid.DataSource = _rows;

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _newBtn.Click += async (_, _) => await OnNewAsync();
        _renameBtn.Click += async (_, _) => await OnRenameAsync();
        _deleteBtn.Click += async (_, _) => await OnDeleteAsync();
        _grid.SelectionChanged += (_, _) => UpdateButtonState();
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await OnRenameAsync(); };

        Load += async (_, _) => await ReloadAsync();
    }

    private DepartmentDto? Selected =>
        _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].DataBoundItem as DepartmentDto;

    private void UpdateButtonState()
    {
        var sel = Selected;
        _renameBtn.Enabled = sel is not null;
        _deleteBtn.Enabled = sel is not null;
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
            UpdateButtonState();
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnNewAsync()
    {
        var name = PromptForName("New department", "Department name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        SetBusy(true, "Creating…");
        try
        {
            var d = await _client.CreateDepartmentAsync(name.Trim());
            _rows.Add(d);
            _statusLabel.Text = $"Created '{d.Name}'.";
        }
        catch (Exception ex) { ShowError("Create failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnRenameAsync()
    {
        var sel = Selected; if (sel is null) return;
        var name = PromptForName("Rename department", "New name:", sel.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == sel.Name) return;
        SetBusy(true, "Renaming…");
        try
        {
            var updated = await _client.RenameDepartmentAsync(sel.Id, name.Trim());
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Id == updated.Id) { _rows[i] = updated; break; }
            _statusLabel.Text = $"Renamed to '{updated.Name}'.";
        }
        catch (Exception ex) { ShowError("Rename failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnDeleteAsync()
    {
        var sel = Selected; if (sel is null) return;
        var msg = sel.UserCount > 0
            ? $"Delete department '{sel.Name}'? It is currently assigned to {sel.UserCount} user(s); they will lose this membership."
            : $"Delete department '{sel.Name}'?";
        if (MessageBox.Show(this, msg, "Delete department", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        SetBusy(true, "Deleting…");
        try
        {
            await _client.DeleteDepartmentAsync(sel.Id);
            _rows.Remove(sel);
            _statusLabel.Text = $"Deleted '{sel.Name}'.";
        }
        catch (Exception ex) { ShowError("Delete failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private string? PromptForName(string title, string prompt, string initial)
    {
        using var dlg = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 140),
            Font = new Font("Segoe UI", 9F),
            Padding = new Padding(16),
        };
        var lbl = new Label { Text = prompt, Dock = DockStyle.Top, Height = 22 };
        var tb = new TextBox { Text = initial, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(0, 6, 0, 0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        dlg.Controls.Add(buttons);
        dlg.Controls.Add(tb);
        dlg.Controls.Add(lbl);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        tb.SelectAll();
        return dlg.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
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
