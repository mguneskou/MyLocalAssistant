using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class UserEditForm : Form
{
    private readonly bool _isCreate;
    private readonly TextBox _username;
    private readonly TextBox _displayName;
    private readonly TextBox _department;
    private readonly TextBox _password;
    private readonly Label _passwordLabel;
    private readonly Label _passwordHint;
    private readonly CheckBox _isAdmin;
    private readonly CheckBox _isDisabled;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Label _status;

    /// <summary>Set on success: a CreateUserRequest (when creating) or an UpdateUserRequest (when editing).</summary>
    public CreateUserRequest? CreateResult { get; private set; }
    public UpdateUserRequest? UpdateResult { get; private set; }

    public UserEditForm(UserAdminDto? existing)
    {
        _isCreate = existing is null;

        Text = _isCreate ? "New user" : $"Edit user — {existing!.Username}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 360);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(16, 16, 16, 8);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        layout.Controls.Add(MakeLabel("Username"), 0, 0);
        _username = new TextBox
        {
            Text = existing?.Username ?? "",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            ReadOnly = !_isCreate, // username is immutable after creation in v2.0
        };
        layout.Controls.Add(_username, 1, 0);

        layout.Controls.Add(MakeLabel("Display name"), 0, 1);
        _displayName = new TextBox { Text = existing?.DisplayName ?? "", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_displayName, 1, 1);

        layout.Controls.Add(MakeLabel("Department"), 0, 2);
        _department = new TextBox { Text = existing?.Department ?? "", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_department, 1, 2);

        _passwordLabel = MakeLabel(_isCreate ? "Initial password" : "Password");
        layout.Controls.Add(_passwordLabel, 0, 3);
        _password = new TextBox
        {
            UseSystemPasswordChar = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Enabled = _isCreate,
        };
        if (!_isCreate) _password.Text = "(use Reset password)";
        layout.Controls.Add(_password, 1, 3);

        _passwordHint = new Label
        {
            Text = _isCreate ? "Min 8 chars. User will be required to change on first login." : "Use the toolbar's Reset password to change.",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
        };
        layout.SetColumnSpan(_passwordHint, 2);
        layout.Controls.Add(_passwordHint, 0, 4);

        _isAdmin = new CheckBox { Text = "Administrator", Checked = existing?.IsAdmin ?? false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.SetColumnSpan(_isAdmin, 2);
        layout.Controls.Add(_isAdmin, 0, 5);

        _isDisabled = new CheckBox { Text = "Disabled (cannot sign in)", Checked = existing?.IsDisabled ?? false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6), Enabled = !_isCreate };
        layout.SetColumnSpan(_isDisabled, 2);
        layout.Controls.Add(_isDisabled, 0, 6);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 7);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(16, 8, 16, 8),
        };
        _save = new Button { Text = _isCreate ? "Create" : "Save", Width = 110, Height = 30 };
        _cancel = new Button { Text = "Cancel", Width = 90, Height = 30, Margin = new Padding(8, 0, 0, 0), DialogResult = DialogResult.Cancel };
        _save.Click += (_, _) => OnSave();
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_cancel);

        // Add Bottom-docked panel BEFORE the Fill panel so the bottom area is reserved.
        Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _save;
        CancelButton = _cancel;
        ActiveControl = _isCreate ? _username : _displayName;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
    };

    private void OnSave()
    {
        _status.Text = "";
        var displayName = _displayName.Text.Trim();
        var department = string.IsNullOrWhiteSpace(_department.Text) ? null : _department.Text.Trim();

        if (_isCreate)
        {
            var username = _username.Text.Trim();
            if (string.IsNullOrEmpty(username)) { _status.Text = "Username is required."; return; }
            if (string.IsNullOrEmpty(displayName)) { _status.Text = "Display name is required."; return; }
            if (_password.Text.Length < 8) { _status.Text = "Initial password must be at least 8 characters."; return; }

            CreateResult = new CreateUserRequest(username, displayName, _password.Text, department, _isAdmin.Checked);
        }
        else
        {
            if (string.IsNullOrEmpty(displayName)) { _status.Text = "Display name is required."; return; }
            UpdateResult = new UpdateUserRequest(displayName, department ?? "", _isAdmin.Checked, _isDisabled.Checked);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
