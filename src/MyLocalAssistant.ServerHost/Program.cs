using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MyLocalAssistant.ServerHost;

/// <summary>
/// Tiny tray app that owns the lifetime of MyLocalAssistant.Server.exe, so testers see
/// one icon = "the server is running" without having to keep a console window open.
///
/// Layout it expects (Velopack / xcopy install):
///     &lt;install&gt;\MyLocalAssistant.ServerHost.exe   ← this app
///     &lt;install&gt;\MyLocalAssistant.Server.exe       ← spawned as child process
///     &lt;install&gt;\MyLocalAssistant.Admin.exe        ← launched from menu (optional)
///     &lt;install&gt;\MyLocalAssistant.Client.exe       ← launched from menu (optional)
///
/// The server's working directory is set to its own folder, so its config / data /
/// logs subdirs land alongside it (same layout as `dotnet run`).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // MUST be first: Velopack handles install/update/uninstall hooks and exits early.
        VelopackApp.Build().Run();

        // Single-instance: a second launch just brings up the menu of the running one.
        using var mtx = new Mutex(initiallyOwned: true, name: "Global\\MyLocalAssistant.ServerHost", out var owned);
        if (!owned) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const string ServerExeName = "MyLocalAssistant.Server.exe";
    private const string AdminExeName = "MyLocalAssistant.Admin.exe";
    private const string ClientExeName = "MyLocalAssistant.Client.exe";
    private const string HealthUrl = "http://127.0.0.1:8080/healthz";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openAdminItem;
    private readonly ToolStripMenuItem _openClientItem;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _server;
    private bool _shuttingDown;
    private UpdateManager? _updater;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("Server: starting…") { Enabled = false };
        _openAdminItem = new ToolStripMenuItem("Open Admin", null, (_, _) => LaunchSibling(AdminExeName));
        _openClientItem = new ToolStripMenuItem("Open Client", null, (_, _) => LaunchSibling(ClientExeName));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openAdminItem);
        menu.Items.Add(_openClientItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => OpenFolder("logs")));
        menu.Items.Add(new ToolStripMenuItem("Open install folder", null, (_, _) => OpenFolder(".")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Restart server", null, async (_, _) => await RestartServerAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Check for updates\u2026", null, async (_, _) => await CheckForUpdatesAsync(interactive: true)));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit (stop server)", null, (_, _) => Quit()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // replaced below if a packaged icon is present
            Visible = true,
            Text = "MyLocalAssistant — starting…",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => LaunchSibling(AdminExeName);
        TryLoadCustomIcon();

        StartServer();

        _healthTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _healthTimer.Tick += async (_, _) => await PollHealthAsync();
        _healthTimer.Start();

        // Quietly check for updates 5 s after launch, and once an hour after that.
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); await CheckForUpdatesAsync(interactive: false); });
        _updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(interactive: false);
        _updateTimer.Start();
    }

    private void StartServer()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, ServerExeName);
            if (!File.Exists(exe))
            {
                MessageBox.Show($"{ServerExeName} not found next to ServerHost.\nLooked in: {dir}",
                    "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Quit();
                return;
            }
            _server = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            _server.Exited += (_, _) => OnServerExited();
            _server.OutputDataReceived += (_, _) => { /* drained to keep buffer clear */ };
            _server.ErrorDataReceived  += (_, _) => { /* drained to keep buffer clear */ };
            _server.Start();
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            ShowBalloon("Server failed to start", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnServerExited()
    {
        if (_shuttingDown) return;
        // Marshal back to UI thread.
        _icon.ContextMenuStrip?.BeginInvoke(new Action(() =>
        {
            SetStatus("Server: stopped (exit " + (_server?.ExitCode.ToString() ?? "?") + ")", false);
            ShowBalloon("Server stopped", "Use the tray menu to restart it.", ToolTipIcon.Warning);
        }));
    }

    private async Task PollHealthAsync()
    {
        if (_shuttingDown) return;
        bool up = false;
        try
        {
            using var resp = await _http.GetAsync(HealthUrl);
            up = resp.IsSuccessStatusCode;
        }
        catch { up = false; }

        if (_server is { HasExited: true }) up = false;
        SetStatus(up ? "Server: running" : "Server: not responding", up);
    }

    private void SetStatus(string text, bool healthy)
    {
        _statusItem.Text = text;
        // NotifyIcon.Text is capped at 127 chars on older Windows; keep it short.
        _icon.Text = healthy ? "MyLocalAssistant — running" : "MyLocalAssistant — " + text.Substring(8);
        _openAdminItem.Enabled = true;  // Admin can still launch even if the server is down (it shows a connection error)
        _openClientItem.Enabled = true;
    }

    private async Task RestartServerAsync()
    {
        await StopServerAsync();
        StartServer();
        ShowBalloon("Server", "Restarted.", ToolTipIcon.Info);
    }

    private async Task StopServerAsync()
    {
        if (_server is null || _server.HasExited) return;
        try
        {
            _server.CloseMainWindow();
            if (!_server.WaitForExit(3000))
                _server.Kill(entireProcessTree: true);
            await _server.WaitForExitAsync();
        }
        catch { /* best-effort */ }
    }

    private void Quit()
    {
        _shuttingDown = true;
        try { _healthTimer.Stop(); } catch { }
        try { StopServerAsync().GetAwaiter().GetResult(); } catch { }
        _icon.Visible = false;
        _icon.Dispose();
        _http.Dispose();
        ExitThread();
    }

    private static void LaunchSibling(string exeName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, exeName);
        if (!File.Exists(path))
        {
            MessageBox.Show($"{exeName} is not installed alongside ServerHost.",
                "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch {exeName}:\n{ex.Message}",
                "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenFolder(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        try { Directory.CreateDirectory(path); } catch { }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ShowBalloon(string title, string text, ToolTipIcon kind)
    {
        try { _icon.ShowBalloonTip(3000, title, text, kind); } catch { }
    }

    private void TryLoadCustomIcon()
    {
        // If a packaged .ico is shipped next to the exe, prefer it over the system icon.
        var custom = Path.Combine(AppContext.BaseDirectory, "MyLocalAssistant.ico");
        if (!File.Exists(custom)) return;
        try { _icon.Icon = new Icon(custom); } catch { /* keep default */ }
    }

    /// <summary>
    /// GitHub release feed used by Velopack to discover updates. Configurable via the
    /// MLA_UPDATE_REPO env var so internal/forked builds can point elsewhere without a recompile.
    /// </summary>
    private static string UpdateRepoUrl =>
        Environment.GetEnvironmentVariable("MLA_UPDATE_REPO")
        ?? "https://github.com/mguneskou/MyLocalAssistant";

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            // First call lazily creates the manager. Velopack only works once installed
            // (i.e. when launched from the Update.exe stub), so in-dev launches no-op.
            _updater ??= new UpdateManager(new GithubSource(UpdateRepoUrl, accessToken: null, prerelease: false));
            if (!_updater.IsInstalled)
            {
                if (interactive) MessageBox.Show("Auto-update only works on installed builds.\nThis appears to be a dev/portable launch.",
                    "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var info = await _updater.CheckForUpdatesAsync();
            if (info is null)
            {
                if (interactive) ShowBalloon("MyLocalAssistant", "You are on the latest version.", ToolTipIcon.Info);
                return;
            }

            if (interactive)
            {
                var ok = MessageBox.Show(
                    $"Update {info.TargetFullRelease.Version} is available. Download and install now?\n\nThe server will restart and Admin/Client must be re-opened.",
                    "MyLocalAssistant update",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (ok != DialogResult.Yes) return;
            }

            await _updater.DownloadUpdatesAsync(info);
            await StopServerAsync();
            // ApplyUpdatesAndRestart relaunches ServerHost.exe after the swap.
            _updater.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show("Update check failed:\n" + ex.Message,
                "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void ShowAbout()
    {
        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString() ?? "?";
        MessageBox.Show(
            $"MyLocalAssistant ServerHost\nVersion {ver}\n\nUpdate feed: {UpdateRepoUrl}",
            "About",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
