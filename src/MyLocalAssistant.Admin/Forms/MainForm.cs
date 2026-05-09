using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Admin.UI;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class MainForm : Form
{
    private readonly ServerClient _client;
    private readonly TabControl _tabs;
    private readonly ToolStripStatusLabel _statusUser;
    private readonly ToolStripStatusLabel _statusServer;
    private readonly ToolStripStatusLabel _statusRole;

    public MainForm(ServerClient client)
    {
        _client = client;

        var role = client.CurrentUser?.IsGlobalAdmin == true
            ? "Global admin"
            : client.CurrentUser?.IsAdmin == true ? "Administrator" : "User";

        Text = $"MyLocalAssistant Admin {Program.AppVersion} — {client.CurrentUser?.DisplayName ?? "?"} @ {client.BaseUrl}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1180, 760);
        UiTheme.ApplyForm(this);

        var menu = new MenuStrip { BackColor = UiTheme.SurfaceCard, Renderer = new UiTheme.ModernRenderer() };
        var fileMenu = new ToolStripMenuItem("&File");
        var changePwd = new ToolStripMenuItem("Change my &password…", null, async (_, _) => await OnChangePasswordAsync());
        var signOut = new ToolStripMenuItem("Sign &out", null, (_, _) => { _client.Logout(); DialogResult = DialogResult.Retry; Close(); });
        var exit = new ToolStripMenuItem("E&xit", null, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { changePwd, new ToolStripSeparator(), signOut, exit });
        menu.Items.Add(fileMenu);
        MainMenuStrip = menu;

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 10, 10, 0),
            BackColor = UiTheme.Surface,
        };
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 8),
            Font = UiTheme.BaseFont,
        };

        var usersPage = new TabPage("Users") { UseVisualStyleBackColor = true };
        usersPage.Controls.Add(new UsersTab(_client));
        _tabs.TabPages.Add(usersPage);

        var deptPage = new TabPage("Departments") { UseVisualStyleBackColor = true };
        deptPage.Controls.Add(new DepartmentsTab(_client));
        _tabs.TabPages.Add(deptPage);

        var agentsPage = new TabPage("Agents") { UseVisualStyleBackColor = true };
        agentsPage.Controls.Add(new AgentsTab(_client));
        // Editing agents and the global system prompt is reserved for the global admin (owner).
        if (_client.CurrentUser?.IsGlobalAdmin == true)
            _tabs.TabPages.Add(agentsPage);

        // Skill catalog (built-in + plug-in) is also owner-only — it controls what every
        // agent in the system is capable of, so a regular admin shouldn't see or change it.
        if (_client.CurrentUser?.IsGlobalAdmin == true)
        {
            var toolsPage = new TabPage("Tools") { UseVisualStyleBackColor = true };
            toolsPage.Controls.Add(new ToolsTab(_client));
            _tabs.TabPages.Add(toolsPage);
        }

        var modelsPage = new TabPage("Models") { UseVisualStyleBackColor = true };
        modelsPage.Controls.Add(new ModelsTab(_client));
        _tabs.TabPages.Add(modelsPage);

        var ragPage = new TabPage("RAG Collections") { UseVisualStyleBackColor = true };
        ragPage.Controls.Add(new CollectionsTab(_client));
        _tabs.TabPages.Add(ragPage);

        var auditPage = new TabPage("Audit") { UseVisualStyleBackColor = true };
        auditPage.Controls.Add(new AuditTab(_client));
        _tabs.TabPages.Add(auditPage);

        var statsPage = new TabPage("Usage") { UseVisualStyleBackColor = true };
        var statsTab = new StatsTab(_client);
        statsPage.Controls.Add(statsTab);
        _tabs.TabPages.Add(statsPage);

        var settingsPage = new TabPage("Server Settings") { UseVisualStyleBackColor = true };
        settingsPage.Controls.Add(new SettingsTab(_client));
        _tabs.TabPages.Add(settingsPage);

        var status = new StatusStrip { BackColor = UiTheme.SurfaceCard, SizingGrip = false };
        _statusRole = new ToolStripStatusLabel($"\u25CF {role}")
        {
            ForeColor = client.CurrentUser?.IsGlobalAdmin == true ? UiTheme.Warning : UiTheme.Success,
            Font = UiTheme.BaseBold,
            Margin = new Padding(6, 2, 12, 2),
        };
        _statusUser = new ToolStripStatusLabel($"Signed in as {_client.CurrentUser?.Username}");
        _statusServer = new ToolStripStatusLabel($"Server: {_client.BaseUrl}")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = UiTheme.TextSecondary,
        };
        status.Items.Add(_statusRole);
        status.Items.Add(new ToolStripSeparator());
        status.Items.Add(_statusUser);
        status.Items.Add(_statusServer);

        contentHost.Controls.Add(_tabs);

        // Add Fill control FIRST so it sits at the bottom of the z-order; Top/Bottom-docked
        // siblings (menu, status) then claim their edges from the remaining client area.
        Controls.Add(contentHost);
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
