using MyLocalAssistant.Client.Services;

namespace MyLocalAssistant.Client.Forms;

internal sealed class ChangePasswordForm : Form
{
    private readonly ChatApiClient _client;
    private readonly bool _forced;
    private readonly TextBox _current;
    private readonly TextBox _next;
    private readonly TextBox _confirm;
    private readonly Label _status;
    private readonly Button _ok;
    private readonly Button _cancel;

    public ChangePasswordForm(ChatApiClient client, bool forced)
    {
        _client = client;
        _forced = forced;

        Text = forced ? "Change password (required)" : "Change password";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(420, 280);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        layout.Controls.Add(new Label { Text = "Current password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        _current = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_current, 1, 0);

        layout.Controls.Add(new Label { Text = "New password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        _next = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_next, 1, 1);

        layout.Controls.Add(new Label { Text = "Confirm new password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        _confirm = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_confirm, 1, 2);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        _ok = new Button { Text = "Change", Width = 100, Height = 30 };
        _cancel = new Button { Text = forced ? "Sign out" : "Cancel", Width = 100, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        _ok.Click += async (_, _) => await DoChangeAsync();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(_cancel);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 4);

        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = _cancel;
        ActiveControl = _current;
    }

    private async Task DoChangeAsync()
    {
        _status.Text = "";
        if (string.IsNullOrEmpty(_current.Text) || string.IsNullOrEmpty(_next.Text)) { _status.Text = "All fields required."; return; }
        if (_next.Text != _confirm.Text) { _status.Text = "New passwords do not match."; return; }
        if (_next.Text.Length < 6) { _status.Text = "New password must be at least 6 characters."; return; }

        try
        {
            await _client.ChangePasswordAsync(_current.Text, _next.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { _status.Text = "Change failed: " + ex.Message; }
    }
}
