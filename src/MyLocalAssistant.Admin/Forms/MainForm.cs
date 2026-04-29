using MyLocalAssistant.Admin.Services;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class MainForm : Form
{
    private readonly ServerClient _client;
    private readonly TabControl _tabs;
    private readonly ToolStripStatusLabel _statusUser;
    private readonly ToolStripStatusLabel _statusServer;

    public MainForm(ServerClient client)
    {
        _client = client;

        Text = $"MyLocalAssistant Admin — {client.CurrentUser?.DisplayName ?? "?"} @ {client.BaseUrl}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1100, 720);
        Font = new Font("Segoe UI", 9F);

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var changePwd = new ToolStripMenuItem("Change my &password…", null, async (_, _) => await OnChangePasswordAsync());
        var signOut = new ToolStripMenuItem("Sign &out", null, (_, _) => { _client.Logout(); DialogResult = DialogResult.Retry; Close(); });
        var exit = new ToolStripMenuItem("E&xit", null, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { changePwd, new ToolStripSeparator(), signOut, exit });
        menu.Items.Add(fileMenu);
        MainMenuStrip = menu;

        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };

        var usersPage = new TabPage("Users") { UseVisualStyleBackColor = true };
        usersPage.Controls.Add(new UsersTab(_client));
        _tabs.TabPages.Add(usersPage);

        var deptPage = new TabPage("Departments") { UseVisualStyleBackColor = true };
        deptPage.Controls.Add(new DepartmentsTab(_client));
        _tabs.TabPages.Add(deptPage);

        var agentsPage = new TabPage("Agents") { UseVisualStyleBackColor = true };
        agentsPage.Controls.Add(new AgentsTab(_client));
        _tabs.TabPages.Add(agentsPage);

        var modelsPage = new TabPage("Models") { UseVisualStyleBackColor = true };
        modelsPage.Controls.Add(new ModelsTab(_client));
        _tabs.TabPages.Add(modelsPage);

        var ragPage = new TabPage("RAG Collections") { UseVisualStyleBackColor = true };
        ragPage.Controls.Add(new CollectionsTab(_client));
        _tabs.TabPages.Add(ragPage);

        _tabs.TabPages.Add(MakePlaceholder("Audit", "Search audit log — Phase 4."));
        _tabs.TabPages.Add(MakePlaceholder("Server Settings", "Listen URL, retention, JWT, signing key — Phase 4."));

        var status = new StatusStrip();
        _statusUser = new ToolStripStatusLabel($"Signed in as {_client.CurrentUser?.Username}");
        _statusServer = new ToolStripStatusLabel($"Server: {_client.BaseUrl}") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        status.Items.Add(_statusUser);
        status.Items.Add(_statusServer);

        // Add Fill control FIRST so it sits at the bottom of the z-order; Top/Bottom-docked
        // siblings (menu, status) then claim their edges from the remaining client area.
        Controls.Add(_tabs);
        Controls.Add(status);
        Controls.Add(menu);
    }

    private static TabPage MakePlaceholder(string title, string body)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        var lbl = new Label
        {
            Text = body,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 11F),
        };
        page.Controls.Add(lbl);
        return page;
    }

    private async Task OnChangePasswordAsync()
    {
        using var dlg = new ChangePasswordForm(_client, forced: false);
        dlg.ShowDialog(this);
        await Task.CompletedTask;
    }
}
