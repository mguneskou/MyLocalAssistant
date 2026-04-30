using MyLocalAssistant.Client.Forms;
using MyLocalAssistant.Client.Services;
using MyLocalAssistant.Client.UI;

namespace MyLocalAssistant.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new UiTheme.ModernRenderer();

        var store = new ClientSettingsStore();

        // Loop allows Sign out to return to login.
        while (true)
        {
            ChatApiClient? client;
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
                    continue;
                }
            }

            DialogResult main;
            using (var form = new ChatForm(client, store))
            {
                main = form.ShowDialog();
            }
            client.Dispose();
            if (main != DialogResult.Retry) return; // Retry == Sign out
        }
    }
}
