using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class UserEditForm : Form
{
    private readonly bool _isCreate;
    private readonly TextBox _username;
    private readonly TextBox _displayName;
    private readonly CheckedListBox _departments;
    private readonly Label _departmentsHint;
    private readonly TextBox _password;
    private readonly Label _passwordHint;
    private readonly TextBox _workRoot;
    private readonly Button _workRootBrowse;
    private readonly Label _workRootHint;
    private readonly CheckBox _isAdmin;
    private readonly CheckBox _isDisabled;
    private readonly CheckBox _mustChangePwd;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Label _status;

    public CreateUserRequest? CreateResult { get; private set; }
    public UpdateUserRequest? UpdateResult { get; private set; }

    public UserEditForm(UserAdminDto? existing, IReadOnlyList<DepartmentDto> allDepartments)
    {
        _isCreate = existing is null;

        Text = _isCreate ? "New user" : $"Edit user — {existing!.Username}";
        StartPosition = FormStartPosition.CenterParent;
        MyLocalAssistant.Admin.UI.UiTheme.ApplyDialog(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(460, 648);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(16, 16, 16, 8);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // username
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // display name
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // password
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // password hint
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // departments label
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // departments list
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // departments hint
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // workRoot label
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // workRoot textbox + browse
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // workRoot hint
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // isAdmin
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // isDisabled
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // mustChangePwd
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // status

        int row = 0;
        layout.Controls.Add(MakeLabel("Username"), 0, row);
        _username = new TextBox
        {
            Text = existing?.Username ?? "",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            ReadOnly = !_isCreate,
        };
        layout.Controls.Add(_username, 1, row++);

        layout.Controls.Add(MakeLabel("Display name"), 0, row);
        _displayName = new TextBox { Text = existing?.DisplayName ?? "", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_displayName, 1, row++);

        layout.Controls.Add(MakeLabel(_isCreate ? "Initial password" : "Password"), 0, row);
        _password = new TextBox
        {
            UseSystemPasswordChar = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Enabled = _isCreate,
            Text = _isCreate ? "" : "(use Reset password)",
        };
        layout.Controls.Add(_password, 1, row++);

        _passwordHint = new Label
        {
            Text = _isCreate ? "Min 8 chars. User will be required to change on first login." : "Use the toolbar's Reset password to change.",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
        };
        layout.SetColumnSpan(_passwordHint, 2);
        layout.Controls.Add(_passwordHint, 0, row++);

        var deptLabel = MakeLabel("Departments");
        layout.SetColumnSpan(deptLabel, 2);
        layout.Controls.Add(deptLabel, 0, row++);

        _departments = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        var existingNames = new HashSet<string>(existing?.Departments ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var d in allDepartments.OrderBy(d => d.Name))
        {
            _departments.Items.Add(d.Name, existingNames.Contains(d.Name));
        }
        layout.SetColumnSpan(_departments, 2);
        layout.Controls.Add(_departments, 0, row++);

        _departmentsHint = new Label
        {
            Text = "",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
        };
        layout.SetColumnSpan(_departmentsHint, 2);
        layout.Controls.Add(_departmentsHint, 0, row++);

        var workRootLabel = MakeLabel("Work folder");
        layout.SetColumnSpan(workRootLabel, 2);
        layout.Controls.Add(workRootLabel, 0, row++);

        var workRootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 0) };
        workRootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workRootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        _workRoot = new TextBox
        {
            Text = existing?.WorkRoot ?? "",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 6, 4),
        };
        _workRootBrowse = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        _workRootBrowse.Click += (_, _) => BrowseWorkRoot();
        workRootRow.Controls.Add(_workRoot, 0, 0);
        workRootRow.Controls.Add(_workRootBrowse, 1, 0);
        layout.SetColumnSpan(workRootRow, 2);
        layout.Controls.Add(workRootRow, 0, row++);

        _workRootHint = new Label
        {
            Text = "Optional. Absolute server path (e.g. D:\\AdminScratch). Per-conversation subfolders are created here. Leave empty for the default.",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
        };
        layout.SetColumnSpan(_workRootHint, 2);
        layout.Controls.Add(_workRootHint, 0, row++);

        _isAdmin = new CheckBox
        {
            Text = "Administrator (implicit access to all departments)",
            Checked = existing?.IsAdmin ?? false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
        };
        _isAdmin.CheckedChanged += (_, _) => UpdateAdminGate();
        layout.SetColumnSpan(_isAdmin, 2);
        layout.Controls.Add(_isAdmin, 0, row++);

        _isDisabled = new CheckBox
        {
            Text = "Disabled (cannot sign in)",
            Checked = existing?.IsDisabled ?? false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            Enabled = !_isCreate,
        };
        layout.SetColumnSpan(_isDisabled, 2);
        layout.Controls.Add(_isDisabled, 0, row++);

        _mustChangePwd = new CheckBox
        {
            Text = "Must change password on next login",
            Checked = existing?.MustChangePassword ?? true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
        };
        layout.SetColumnSpan(_mustChangePwd, 2);
        layout.Controls.Add(_mustChangePwd, 0, row++);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, row++);

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

        // Bottom panel docked first so it reserves the bottom area before the Fill panel.
        Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _save;
        CancelButton = _cancel;
        ActiveControl = _isCreate ? _username : _displayName;

        UpdateAdminGate();
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
    };

    private void UpdateAdminGate()
    {
        var isAdmin = _isAdmin.Checked;
        _departments.Enabled = !isAdmin;
        _departmentsHint.Text = isAdmin
            ? "Administrators have access to all departments — selection ignored."
            : (_departments.Items.Count == 0
                ? "No departments defined yet. Use the Departments tab to add some."
                : "Select one or more. User will only see agents in these departments.");
    }

    private List<string> GetCheckedDepartments()
    {
        var result = new List<string>();
        foreach (var item in _departments.CheckedItems) result.Add(item.ToString() ?? "");
        return result;
    }

    private void BrowseWorkRoot()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the work folder on the SERVER PC where this user's conversation files will be stored.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = string.IsNullOrWhiteSpace(_workRoot.Text) ? "" : _workRoot.Text,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            _workRoot.Text = dlg.SelectedPath;
    }

    private void OnSave()
    {
        _status.Text = "";
        var displayName = _displayName.Text.Trim();
        var depts = _isAdmin.Checked ? new List<string>() : GetCheckedDepartments();

        if (_isCreate)
        {
            var username = _username.Text.Trim();
            if (string.IsNullOrEmpty(username)) { _status.Text = "Username is required."; return; }
            if (string.IsNullOrEmpty(displayName)) { _status.Text = "Display name is required."; return; }
            if (_password.Text.Length < 8) { _status.Text = "Initial password must be at least 8 characters."; return; }
            var workRoot = _workRoot.Text.Trim();
            if (workRoot.Length > 0 && !Path.IsPathFullyQualified(workRoot)) { _status.Text = "Work folder must be an absolute path (e.g. D:\\Scratch or \\\\server\\share)."; return; }

            // MustChangePassword is always true for new users (enforced server-side).
            CreateResult = new CreateUserRequest(username, displayName, _password.Text, depts, _isAdmin.Checked, workRoot.Length > 0 ? workRoot : null);
        }
        else
        {
            if (string.IsNullOrEmpty(displayName)) { _status.Text = "Display name is required."; return; }
            var workRoot = _workRoot.Text.Trim();
            if (workRoot.Length > 0 && !Path.IsPathFullyQualified(workRoot)) { _status.Text = "Work folder must be an absolute path (e.g. D:\\Scratch or \\\\server\\share)."; return; }
            UpdateResult = new UpdateUserRequest(displayName, depts, _isAdmin.Checked, _isDisabled.Checked, workRoot, _mustChangePwd.Checked);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
