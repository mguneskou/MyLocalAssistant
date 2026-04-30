using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class SettingsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripButton _saveBtn;
    private readonly TableLayoutPanel _form;
    private readonly Label _listenLbl;
    private readonly Label _issuerLbl;
    private readonly Label _audienceLbl;
    private readonly Label _modelLbl;
    private readonly Label _embedLbl;
    private readonly NumericUpDown _accessMinutes;
    private readonly NumericUpDown _refreshDays;
    private readonly NumericUpDown _bodyDays;
    private readonly NumericUpDown _auditDays;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;

    public SettingsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _refreshBtn.Click += async (_, _) => await LoadAsync();
        _saveBtn = new ToolStripButton("Save") { Enabled = false };
        _saveBtn.Click += async (_, _) => await SaveAsync();
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _refreshBtn, _saveBtn, new ToolStripSeparator(),
            new ToolStripLabel("  Read-only fields require editing config\\server.json and a service restart.")
            {
                ForeColor = SystemColors.GrayText,
            },
        });

        // Global admin only: cloud LLM key management lives behind a dialog so it doesn't
        // pollute the main settings form for regular admins.
        if (_client.CurrentUser?.IsGlobalAdmin == true)
        {
            var cloudBtn = new ToolStripButton("Cloud keys\u2026");
            cloudBtn.Click += async (_, _) => await OpenCloudKeysAsync();
            _toolbar.Items.Insert(2, cloudBtn);
        }

        _form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = false,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        _form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        _form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _listenLbl = MakeReadOnly();
        _issuerLbl = MakeReadOnly();
        _audienceLbl = MakeReadOnly();
        _modelLbl = MakeReadOnly();
        _embedLbl = MakeReadOnly();
        _accessMinutes = MakeNumeric(1, 1440);
        _refreshDays = MakeNumeric(1, 365);
        _bodyDays = MakeNumeric(1, 3650);
        _auditDays = MakeNumeric(1, 3650);

        AddRow("Listen URL", _listenLbl);
        AddRow("JWT issuer", _issuerLbl);
        AddRow("JWT audience", _audienceLbl);
        AddRow("Active LLM", _modelLbl);
        AddRow("Active embedding", _embedLbl);
        AddSeparator();
        AddRow("Access token (minutes)", _accessMinutes, "Lifetime of an issued JWT. Lower = tighter revocation, more refreshes.");
        AddRow("Refresh token (days)", _refreshDays, "Sliding window during which the client can renew without re-login.");
        AddRow("Message body retention (days)", _bodyDays, "After this, message bodies are nulled (metadata kept).");
        AddRow("Audit retention (days)", _auditDays, "After this, audit rows are deleted entirely.");

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(_form);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await LoadAsync();
    }

    private static Label MakeReadOnly() => new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 6),
    };

    private static NumericUpDown MakeNumeric(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 110,
        Margin = new Padding(0, 4, 0, 4),
    };

    private void AddRow(string label, Control value, string? hint = null)
    {
        var l = new Label { Text = label + ":", AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
        if (hint is null)
        {
            _form.RowCount++;
            _form.Controls.Add(l);
            _form.Controls.Add(value);
        }
        else
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Margin = Padding.Empty,
            };
            panel.Controls.Add(value);
            panel.Controls.Add(new Label
            {
                Text = "  " + hint,
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                Margin = new Padding(8, 7, 0, 0),
            });
            _form.RowCount++;
            _form.Controls.Add(l);
            _form.Controls.Add(panel);
        }
        if (value is NumericUpDown n) n.ValueChanged += (_, _) => _saveBtn.Enabled = true;
    }

    private void AddSeparator()
    {
        var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 8) };
        _form.RowCount++;
        _form.Controls.Add(new Label { Text = "" });
        _form.Controls.Add(sep);
    }

    private async Task LoadAsync()
    {
        try
        {
            _statusLabel.Text = "Loading\u2026";
            var s = await _client.GetServerSettingsAsync();
            _listenLbl.Text = s.ListenUrl;
            _issuerLbl.Text = s.JwtIssuer;
            _audienceLbl.Text = s.JwtAudience;
            _modelLbl.Text = s.DefaultModelId ?? "(none)";
            _embedLbl.Text = s.EmbeddingModelId ?? "(none)";
            _accessMinutes.Value = Math.Clamp(s.AccessTokenMinutes, (int)_accessMinutes.Minimum, (int)_accessMinutes.Maximum);
            _refreshDays.Value = Math.Clamp(s.RefreshTokenDays, (int)_refreshDays.Minimum, (int)_refreshDays.Maximum);
            _bodyDays.Value = Math.Clamp(s.MessageBodyRetentionDays, (int)_bodyDays.Minimum, (int)_bodyDays.Maximum);
            _auditDays.Value = Math.Clamp(s.AuditRetentionDays, (int)_auditDays.Minimum, (int)_auditDays.Maximum);
            _saveBtn.Enabled = false;
            _statusLabel.Text = "Loaded.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Load failed: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            _saveBtn.Enabled = false;
            _statusLabel.Text = "Saving\u2026";
            var req = new UpdateServerSettingsRequest(
                (int)_accessMinutes.Value,
                (int)_refreshDays.Value,
                (int)_bodyDays.Value,
                (int)_auditDays.Value);
            await _client.UpdateServerSettingsAsync(req);
            _statusLabel.Text = "Saved. New token lifetimes apply to the next login; retention applies on the next pass (within 6h).";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Save failed: " + ex.Message;
            _saveBtn.Enabled = true;
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OpenCloudKeysAsync()
    {
        try
        {
            var status = await _client.GetCloudKeysAsync();
            using var dlg = new CloudKeysDialog(_client, status);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Cloud keys failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Cloud keys", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
