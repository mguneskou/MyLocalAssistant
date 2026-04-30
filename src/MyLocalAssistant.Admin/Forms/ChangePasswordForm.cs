using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Admin.UI;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class ChangePasswordForm : Form
{
    private readonly ServerClient _client;
    private readonly bool _forced;
    private readonly TextBox _current;
    private readonly TextBox _next;
    private readonly TextBox _confirm;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Label _status;

    public ChangePasswordForm(ServerClient client, bool forced)
    {
        _client = client;
        _forced = forced;

        Text = forced ? "Change password (required on first login)" : "Change password";
        StartPosition = FormStartPosition.CenterScreen;
        UiTheme.ApplyDialog(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(440, 320);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(20);
        ControlBox = !forced;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        layout.Controls.Add(new Label { Text = "Current password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        _current = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_current, 1, 0);

        layout.Controls.Add(new Label { Text = "New password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        _next = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_next, 1, 1);

        layout.Controls.Add(new Label { Text = "Confirm new password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        _confirm = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_confirm, 1, 2);

        var hint = new Label
        {
            Text = "Minimum 8 characters.",
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 3);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 4);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        _save = new Button { Text = "Change password", Width = 140, Height = 30 };
        _cancel = new Button { Text = forced ? "Sign out" : "Cancel", Width = 100, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        _save.Click += async (_, _) => await DoSaveAsync();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_cancel);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 5);

        Controls.Add(layout);
        AcceptButton = _save;
        if (!forced) CancelButton = _cancel;
        ActiveControl = _current;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_forced && DialogResult != DialogResult.OK && DialogResult != DialogResult.Cancel)
        {
            e.Cancel = true; // can't dismiss with X when forced
        }
        base.OnFormClosing(e);
    }

    private async Task DoSaveAsync()
    {
        _status.Text = "";
        if (_next.Text != _confirm.Text)
        {
            _status.Text = "New passwords do not match.";
            return;
        }
        if (_next.Text.Length < 8)
        {
            _status.Text = "New password must be at least 8 characters.";
            return;
        }
        if (_next.Text == _current.Text)
        {
            _status.Text = "New password must differ from the current one.";
            return;
        }

        SetBusy(true);
        try
        {
            await _client.ChangePasswordAsync(_current.Text, _next.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ServerApiException ex)
        {
            _status.Text = ex.StatusCode == 400 ? "Current password is incorrect or new password rejected." : ex.Message;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _save.Enabled = !busy;
        _cancel.Enabled = !busy;
        _current.Enabled = !busy;
        _next.Enabled = !busy;
        _confirm.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
