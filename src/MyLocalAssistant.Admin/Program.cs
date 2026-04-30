using MyLocalAssistant.Admin.Forms;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Admin.UI;

namespace MyLocalAssistant.Admin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new UiTheme.ModernRenderer();

        var store = new AdminSettingsStore();

        // Loop allows Sign out to return to login.
        while (true)
        {
            ServerClient? client;
            using (var login = new LoginForm(store))
            {
                if (login.ShowDialog() != DialogResult.OK || login.AuthenticatedClient is null) return;
                client = login.AuthenticatedClient;
            }

            if (client.CurrentUser?.MustChangePassword == true)
            {
                using var force = new ChangePasswordForm(client, forced: true);
                if (force.ShowDialog() != DialogResult.OK)
                {
                    client.Logout();
                    client.Dispose();
                    continue; // back to login
                }
            }

            DialogResult main;
            using (var form = new MainForm(client))
            {
                main = form.ShowDialog();
            }
            client.Dispose();
            if (main != DialogResult.Retry) return; // Cancel/OK -> exit; Retry means "sign out"
        }
    }
}
