using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class CollectionPermissionsForm : Form
{
    private readonly ServerClient _client;
    private readonly RagCollectionDto _collection;
    private readonly List<UserAdminDto> _allUsers;
    private readonly List<DepartmentDto> _allDepts;
    private readonly List<RoleDto> _allRoles;

    private readonly ComboBox _accessCombo;
    private readonly ListView _grantList;
    private readonly ComboBox _kindCombo;
    private readonly ComboBox _principalCombo;
    private readonly Button _addBtn;
    private readonly Button _removeBtn;
    private readonly Button _closeBtn;
    private readonly Label _statusLabel;
    private List<CollectionGrantDto> _grants;

    public CollectionPermissionsForm(
        ServerClient client,
        RagCollectionDto collection,
        List<CollectionGrantDto> grants,
        List<UserAdminDto> users,
        List<DepartmentDto> depts,
        List<RoleDto> roles)
    {
        _client = client;
        _collection = collection;
        _grants = grants;
        _allUsers = users;
        _allDepts = depts;
        _allRoles = roles;

        Text = $"Permissions \u2014 {collection.Name}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 460);
        ClientSize = new Size(680, 500);
        Font = new Font("Segoe UI", 9F);

        var lblAccess = new Label { Text = "Access mode:", Left = 12, Top = 16, Width = 100 };
        _accessCombo = new ComboBox
        {
            Left = 116, Top = 12, Width = 160,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _accessCombo.Items.AddRange(new object[] { "Restricted", "Public" });
        _accessCombo.SelectedItem = collection.AccessMode;
        _accessCombo.SelectedIndexChanged += async (_, _) => await OnAccessChangedAsync();
        var lblHint = new Label
        {
            Left = 286, Top = 16, Width = 380,
            Text = "Restricted: only grants below + admins. Public: any signed-in user.",
            ForeColor = SystemColors.GrayText,
        };

        var grantsHeader = new Label { Text = "Grants (Restricted mode)", Left = 12, Top = 50, Width = 300, Font = new Font(Font, FontStyle.Bold) };
        _grantList = new ListView
        {
            Left = 12, Top = 72, Width = 656, Height = 280,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = true,
        };
        _grantList.Columns.Add("Kind", 100);
        _grantList.Columns.Add("Principal", 240);
        _grantList.Columns.Add("Id", 260);
        _grantList.Columns.Add("Granted", 120);

        _kindCombo = new ComboBox
        {
            Left = 12, Top = 364, Width = 110,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _kindCombo.Items.AddRange(new object[] { "User", "Department", "Role" });
        _kindCombo.SelectedIndex = 1; // Department: most common in enterprise
        _kindCombo.SelectedIndexChanged += (_, _) => RefreshPrincipalList();

        _principalCombo = new ComboBox
        {
            Left = 128, Top = 364, Width = 360,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _addBtn = new Button { Text = "Add grant", Left = 494, Top = 363, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _removeBtn = new Button { Text = "Remove", Left = 588, Top = 363, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _addBtn.Click += async (_, _) => await OnAddAsync();
        _removeBtn.Click += async (_, _) => await OnRemoveAsync();

        _statusLabel = new Label
        {
            Left = 12, Top = 400, Width = 560,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };
        _closeBtn = new Button { Text = "Close", Left = 588, Top = 432, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
        AcceptButton = _closeBtn;

        Controls.AddRange(new Control[] { lblAccess, _accessCombo, lblHint, grantsHeader, _grantList, _kindCombo, _principalCombo, _addBtn, _removeBtn, _statusLabel, _closeBtn });

        RefreshPrincipalList();
        RefreshGrantsList();
        UpdateGrantsEnablement();
    }

    private void UpdateGrantsEnablement()
    {
        var restricted = (_accessCombo.SelectedItem as string) == "Restricted";
        _grantList.Enabled = restricted;
        _kindCombo.Enabled = restricted;
        _principalCombo.Enabled = restricted;
        _addBtn.Enabled = restricted;
        _removeBtn.Enabled = restricted;
    }

    private async Task OnAccessChangedAsync()
    {
        try
        {
            var mode = (_accessCombo.SelectedItem as string) ?? "Restricted";
            await _client.UpdateCollectionAsync(_collection.Id, new UpdateCollectionRequest(null, mode));
            _statusLabel.Text = $"Access mode set to {mode}.";
            UpdateGrantsEnablement();
        }
        catch (Exception ex)
        {
            ShowError("Update failed", ex);
            // revert UI
            _accessCombo.SelectedItem = _collection.AccessMode;
        }
    }

    private void RefreshPrincipalList()
    {
        _principalCombo.Items.Clear();
        switch (_kindCombo.SelectedItem as string)
        {
            case "User":
                foreach (var u in _allUsers.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase))
                    _principalCombo.Items.Add(new PrincipalItem(u.Id, $"{u.DisplayName} ({u.Username})"));
                break;
            case "Department":
                foreach (var d in _allDepts.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    _principalCombo.Items.Add(new PrincipalItem(d.Id, d.Name));
                break;
            case "Role":
                foreach (var r in _allRoles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    _principalCombo.Items.Add(new PrincipalItem(r.Id, r.Name));
                break;
        }
        if (_principalCombo.Items.Count > 0) _principalCombo.SelectedIndex = 0;
    }

    private void RefreshGrantsList()
    {
        _grantList.Items.Clear();
        foreach (var g in _grants.OrderBy(g => g.PrincipalKind).ThenBy(g => g.PrincipalDisplayName ?? ""))
        {
            var item = new ListViewItem(g.PrincipalKind) { Tag = g };
            item.SubItems.Add(g.PrincipalDisplayName ?? "(unknown)");
            item.SubItems.Add(g.PrincipalId.ToString());
            item.SubItems.Add(g.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
            _grantList.Items.Add(item);
        }
        _statusLabel.Text = $"{_grants.Count} grant(s).";
    }

    private async Task OnAddAsync()
    {
        if (_principalCombo.SelectedItem is not PrincipalItem p) return;
        var kind = (_kindCombo.SelectedItem as string) ?? "User";
        try
        {
            var grant = await _client.AddGrantAsync(_collection.Id, kind, p.Id);
            // server didn't fill display name; supply from local cache.
            grant = grant with { PrincipalDisplayName = p.Display };
            _grants.Add(grant);
            RefreshGrantsList();
            _statusLabel.Text = $"Granted {kind} '{p.Display}'.";
        }
        catch (ServerApiException ex) when (ex.StatusCode == 409)
        {
            _statusLabel.Text = "That principal already has a grant.";
        }
        catch (Exception ex) { ShowError("Add grant failed", ex); }
    }

    private async Task OnRemoveAsync()
    {
        if (_grantList.SelectedItems.Count == 0) return;
        var item = _grantList.SelectedItems[0];
        if (item.Tag is not CollectionGrantDto g) return;
        var confirm = MessageBox.Show(this,
            $"Remove {g.PrincipalKind} grant for '{g.PrincipalDisplayName ?? g.PrincipalId.ToString()}'?",
            "Remove grant", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;
        try
        {
            await _client.RemoveGrantAsync(_collection.Id, g.Id);
            _grants.Remove(g);
            RefreshGrantsList();
            _statusLabel.Text = "Grant removed.";
        }
        catch (Exception ex) { ShowError("Remove failed", ex); }
    }

    private void ShowError(string title, Exception ex)
    {
        _statusLabel.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private sealed record PrincipalItem(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }
}
