using MyLocalAssistant.Client.Services;

namespace MyLocalAssistant.Client.Forms;

internal sealed class LoginForm : Form
{
    private readonly ClientSettingsStore _store;
    private readonly TextBox _serverUrl;
    private readonly TextBox _username;
    private readonly TextBox _password;
    private readonly CheckBox _rememberUser;
    private readonly Button _login;
    private readonly Button _exit;
    private readonly Label _status;

    public ChatApiClient? AuthenticatedClient { get; private set; }

    public LoginForm(ClientSettingsStore store)
    {
        _store = store;
        var settings = _store.Load();

        Text = "MyLocalAssistant — Sign in";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(420, 320);
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(20);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        layout.Controls.Add(new Label { Text = "Server URL", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        _serverUrl = new TextBox { Text = settings.ServerUrl, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_serverUrl, 1, 0);

        layout.Controls.Add(new Label { Text = "Username", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        _username = new TextBox { Text = settings.RememberUsername ? settings.LastUsername ?? "" : "", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_username, 1, 1);

        layout.Controls.Add(new Label { Text = "Password", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        _password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_password, 1, 2);

        _rememberUser = new CheckBox { Text = "Remember username", Checked = settings.RememberUsername, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_rememberUser, 1, 3);

        _status = new Label { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoEllipsis = true };
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_status, 0, 4);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        _login = new Button { Text = "Sign in", Width = 110, Height = 30 };
        _exit = new Button { Text = "Exit", Width = 90, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        _login.Click += async (_, _) => await DoLoginAsync();
        _exit.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(_login);
        buttons.Controls.Add(_exit);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 5);

        Controls.Add(layout);
        AcceptButton = _login;
        CancelButton = _exit;
        ActiveControl = string.IsNullOrEmpty(_username.Text) ? _username : _password;
    }

    private async Task DoLoginAsync()
    {
        _status.Text = "";
        if (string.IsNullOrWhiteSpace(_serverUrl.Text) || string.IsNullOrWhiteSpace(_username.Text) || string.IsNullOrEmpty(_password.Text))
        {
            _status.Text = "Server URL, username and password are required.";
            return;
        }

        SetBusy(true);
        ChatApiClient? client = null;
        try
        {
            client = new ChatApiClient(_serverUrl.Text.Trim());
            if (!await client.PingAsync())
            {
                _status.Text = "Cannot reach server. Check the URL and that the service is running.";
                client.Dispose();
                return;
            }

            await client.LoginAsync(_username.Text.Trim(), _password.Text);

            var s = _store.Load();
            s.ServerUrl = _serverUrl.Text.Trim();
            s.RememberUsername = _rememberUser.Checked;
            s.LastUsername = _rememberUser.Checked ? _username.Text.Trim() : null;
            _store.Save(s);

            AuthenticatedClient = client;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ServerApiException ex) when (ex.StatusCode == 401)
        {
            _status.Text = "Invalid username or password.";
            client?.Dispose();
        }
        catch (Exception ex)
        {
            _status.Text = "Sign in failed: " + ex.Message;
            client?.Dispose();
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _login.Enabled = !busy;
        _exit.Enabled = !busy;
        _serverUrl.Enabled = !busy;
        _username.Enabled = !busy;
        _password.Enabled = !busy;
        _rememberUser.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
