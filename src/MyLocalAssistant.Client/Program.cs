using MyLocalAssistant.Client.Forms;
using MyLocalAssistant.Client.Services;
using MyLocalAssistant.Client.UI;

namespace MyLocalAssistant.Client;

internal static class Program
{
    private static readonly string s_logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyLocalAssistant", "client.log");

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new UiTheme.ModernRenderer();

        // Make UI-thread and background-thread crashes visible instead of silently exiting.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleFatal(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) => { HandleFatal(e.Exception, "Task"); e.SetObserved(); };

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
            try
            {
                using var form = new ChatForm(client, store);
                main = form.ShowDialog();
            }
            catch (Exception ex)
            {
                HandleFatal(ex, "ChatForm");
                client.Dispose();
                return;
            }
            client.Dispose();
            if (main != DialogResult.Retry) return; // Retry == Sign out
        }
    }

    private static void HandleFatal(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_logPath)!);
            File.AppendAllText(s_logPath,
                $"[{DateTimeOffset.Now:O}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex}\n\n");
        }
        catch { /* best-effort */ }
        try
        {
            MessageBox.Show(
                $"MyLocalAssistant Client crashed.\n\nSource: {source}\n{ex.GetType().Name}: {ex.Message}\n\nDetails written to:\n{s_logPath}",
                "MyLocalAssistant Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { /* no UI available */ }
    }
}
