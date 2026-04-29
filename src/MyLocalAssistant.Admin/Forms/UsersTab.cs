using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;
using System.ComponentModel;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class UsersTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _newBtn;
    private readonly ToolStripButton _editBtn;
    private readonly ToolStripButton _resetPwdBtn;
    private readonly ToolStripButton _toggleDisabledBtn;
    private readonly ToolStripButton _deleteBtn;
    private readonly DataGridView _grid;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<UserAdminDto> _rows = new();
    private List<DepartmentDto> _allDepartments = new();

    public UsersTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _newBtn = new ToolStripButton("New user…");
        _editBtn = new ToolStripButton("Edit…") { Enabled = false };
        _resetPwdBtn = new ToolStripButton("Reset password…") { Enabled = false };
        _toggleDisabledBtn = new ToolStripButton("Disable") { Enabled = false };
        _deleteBtn = new ToolStripButton("Delete") { Enabled = false };
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _refreshBtn, new ToolStripSeparator(), _newBtn, _editBtn,
            new ToolStripSeparator(), _resetPwdBtn, _toggleDisabledBtn,
            new ToolStripSeparator(), _deleteBtn,
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
            new DataGridViewTextBoxColumn { HeaderText = "Username", DataPropertyName = nameof(UserAdminDto.Username), Width = 140 },
            new DataGridViewTextBoxColumn { HeaderText = "Display name", DataPropertyName = nameof(UserAdminDto.DisplayName), Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "Departments", Name = "Departments", Width = 200 },
            new DataGridViewCheckBoxColumn { HeaderText = "Admin", DataPropertyName = nameof(UserAdminDto.IsAdmin), Width = 60 },
            new DataGridViewCheckBoxColumn { HeaderText = "Disabled", DataPropertyName = nameof(UserAdminDto.IsDisabled), Width = 70 },
            new DataGridViewCheckBoxColumn { HeaderText = "Must change pwd", DataPropertyName = nameof(UserAdminDto.MustChangePassword), Width = 110 },
            new DataGridViewTextBoxColumn { HeaderText = "Last login", DataPropertyName = nameof(UserAdminDto.LastLoginAt), Width = 140, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm" } },
            new DataGridViewTextBoxColumn { HeaderText = "Created", DataPropertyName = nameof(UserAdminDto.CreatedAt), Width = 140, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm" } },
        });
        _grid.DataSource = _rows;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var col = _grid.Columns[e.ColumnIndex];
            if (col.Name != "Departments") return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as UserAdminDto;
            if (row is null) return;
            e.Value = row.IsAdmin ? "(all — admin)" : string.Join(", ", row.Departments);
            e.FormattingApplied = true;
        };

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _newBtn.Click += async (_, _) => await OnNewAsync();
        _editBtn.Click += async (_, _) => await OnEditAsync();
        _resetPwdBtn.Click += async (_, _) => await OnResetPasswordAsync();
        _toggleDisabledBtn.Click += async (_, _) => await OnToggleDisabledAsync();
        _deleteBtn.Click += async (_, _) => await OnDeleteAsync();
        _grid.SelectionChanged += (_, _) => UpdateButtonState();
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await OnEditAsync();
        };

        Load += async (_, _) => await ReloadAsync();
    }

    private UserAdminDto? Selected =>
        _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].DataBoundItem as UserAdminDto;

    private bool SelectedIsSelf =>
        Selected?.Id == _client.CurrentUser?.Id;

    private void UpdateButtonState()
    {
        var sel = Selected;
        _editBtn.Enabled = sel is not null;
        _resetPwdBtn.Enabled = sel is not null;
        _toggleDisabledBtn.Enabled = sel is not null && !SelectedIsSelf;
        _toggleDisabledBtn.Text = sel?.IsDisabled == true ? "Enable" : "Disable";
        _deleteBtn.Enabled = sel is not null && !SelectedIsSelf;
    }

    private async Task ReloadAsync()
    {
        SetBusy(true, "Loading users…");
        try
        {
            var deptTask = _client.ListDepartmentsAsync();
            var users = await _client.ListUsersAsync();
            _allDepartments = await deptTask;
            _rows.Clear();
            foreach (var u in users) _rows.Add(u);
            _statusLabel.Text = $"{users.Count} user(s), {_allDepartments.Count} department(s).";
            UpdateButtonState();
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnNewAsync()
    {
        // Refresh departments so the picker is current.
        try { _allDepartments = await _client.ListDepartmentsAsync(); } catch { /* fall back to cached */ }
        using var dlg = new UserEditForm(existing: null, _allDepartments);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.CreateResult is null) return;
        SetBusy(true, "Creating user…");
        try
        {
            var created = await _client.CreateUserAsync(dlg.CreateResult);
            _rows.Add(created);
            _statusLabel.Text = $"Created '{created.Username}'.";
        }
        catch (Exception ex) { ShowError("Create failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnEditAsync()
    {
        var sel = Selected; if (sel is null) return;
        try { _allDepartments = await _client.ListDepartmentsAsync(); } catch { /* fall back to cached */ }
        using var dlg = new UserEditForm(sel, _allDepartments);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.UpdateResult is null) return;
        SetBusy(true, "Saving…");
        try
        {
            var updated = await _client.UpdateUserAsync(sel.Id, dlg.UpdateResult);
            ReplaceRow(updated);
            _statusLabel.Text = $"Saved '{updated.Username}'.";
        }
        catch (Exception ex) { ShowError("Save failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnResetPasswordAsync()
    {
        var sel = Selected; if (sel is null) return;
        using var dlg = new ResetPasswordPromptForm(sel.Username);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.NewPassword is null) return;
        SetBusy(true, "Resetting password…");
        try
        {
            await _client.ResetUserPasswordAsync(sel.Id, dlg.NewPassword);
            // server forces MustChangePassword=true; reflect locally.
            ReplaceRow(sel with { MustChangePassword = true });
            _statusLabel.Text = $"Password reset for '{sel.Username}'.";
        }
        catch (Exception ex) { ShowError("Reset failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnToggleDisabledAsync()
    {
        var sel = Selected; if (sel is null || SelectedIsSelf) return;
        var newState = !sel.IsDisabled;
        var verb = newState ? "Disable" : "Enable";
        if (MessageBox.Show(this, $"{verb} user '{sel.Username}'?", verb, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        SetBusy(true, $"{verb}ing user…");
        try
        {
            var updated = await _client.UpdateUserAsync(sel.Id, new UpdateUserRequest(null, null, null, newState));
            ReplaceRow(updated);
            _statusLabel.Text = $"{verb}d '{updated.Username}'.";
        }
        catch (Exception ex) { ShowError($"{verb} failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private async Task OnDeleteAsync()
    {
        var sel = Selected; if (sel is null || SelectedIsSelf) return;
        if (MessageBox.Show(this, $"Delete user '{sel.Username}'? This cannot be undone.",
                "Delete user", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        SetBusy(true, "Deleting user…");
        try
        {
            await _client.DeleteUserAsync(sel.Id);
            _rows.Remove(sel);
            _statusLabel.Text = $"Deleted '{sel.Username}'.";
        }
        catch (Exception ex) { ShowError("Delete failed", ex); }
        finally { SetBusy(false); UpdateButtonState(); }
    }

    private void ReplaceRow(UserAdminDto updated)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id == updated.Id) { _rows[i] = updated; return; }
        }
        _rows.Add(updated);
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
