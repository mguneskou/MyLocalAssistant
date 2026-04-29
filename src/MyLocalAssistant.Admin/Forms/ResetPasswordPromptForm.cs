using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class ResetPasswordPromptForm : Form
{
    private readonly TextBox _password;
    private readonly TextBox _confirm;
    private readonly Label _status;

    public string? NewPassword { get; private set; }

    public ResetPasswordPromptForm(string username)
    {
        Text = $"Reset password — {username}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(420, 250);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(20);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        layout.Controls.Add(new Label { Text = "New password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        _password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_password, 1, 0);

        layout.Controls.Add(new Label { Text = "Confirm password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        _confirm = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_confirm, 1, 1);

        var hint = new Label
        {
            Text = "Min 8 chars. The user will be forced to change on next login.",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 2);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 3);

        Controls.Add(layout);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        var ok = new Button { Text = "Reset password", Width = 130, Height = 30 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, Margin = new Padding(8, 0, 0, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_password.Text.Length < 8) { _status.Text = "Password must be at least 8 characters."; return; }
            if (_password.Text != _confirm.Text) { _status.Text = "Passwords do not match."; return; }
            NewPassword = _password.Text;
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _password;
    }
}
