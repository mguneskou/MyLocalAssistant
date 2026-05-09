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
        Application.Run(new TrayContext(mtx));
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
    private readonly ToolStripMenuItem _pauseUpdatesItem;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _server;
    private bool _shuttingDown;
    private UpdateManager? _updater;
    // Velopack refuses to run two operations against the same install at once
    // (it takes an exclusive file lock in the package folder). Serialize all
    // calls through this gate so the timer Tick can't collide with the menu.
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    // Held in Main(); must be released before ApplyUpdatesAndRestart so the
    // relaunched process can acquire it (otherwise new instance sees !owned and exits).
    private readonly Mutex _singleInstanceMutex;

    public TrayContext(Mutex singleInstanceMutex)
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
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => OpenAbsolute(ResolveLogsDirectory())));
        menu.Items.Add(new ToolStripMenuItem("Open install folder", null, (_, _) => OpenFolder(".")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Restart server", null, async (_, _) => await RestartServerAsync()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Check for updates\u2026", null, async (_, _) => await CheckForUpdatesAsync(interactive: true)));
        _pauseUpdatesItem = new ToolStripMenuItem("Pause auto-updates", null, (_, _) => TogglePauseUpdates()) { CheckOnClick = false };
        menu.Items.Add(_pauseUpdatesItem);
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit (stop server)", null, (_, _) => Quit()));

        _singleInstanceMutex = singleInstanceMutex;

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
        RefreshPauseUpdatesItem();

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
            // ConfigureAwait(false) so the continuation does NOT try to resume on
            // the WinForms UI thread; Quit() blocks that thread on .GetResult().
            await _server.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private void Quit()
    {
        _shuttingDown = true;
        try { _healthTimer.Stop(); } catch { }
        try { _updateTimer.Stop(); } catch { }
        // Run on a worker thread so an async continuation inside StopServerAsync
        // can never deadlock against the UI thread we're standing on.
        try { Task.Run(StopServerAsync).GetAwaiter().GetResult(); } catch { }
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

    private static void OpenAbsolute(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>
    /// The Server writes Serilog files to <c>&lt;state&gt;\logs</c>, where <c>state</c>
    /// is the sibling of the Velopack <c>current\</c> folder so it survives upgrades
    /// (see <c>ServerPaths</c>). For dev/portable runs the state folder is the install
    /// folder itself. Mirror that resolution here so the tray opens the folder that
    /// actually contains <c>server-*.log</c>.
    /// </summary>
    private static string ResolveLogsDirectory()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isVelopack = string.Equals(Path.GetFileName(appDir), "current", StringComparison.OrdinalIgnoreCase);
        var stateDir = isVelopack
            ? Path.Combine(Directory.GetParent(appDir)?.FullName ?? appDir, "state")
            : appDir;
        return Path.Combine(stateDir, "logs");
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
        if (AutoUpdatesPaused())
        {
            if (interactive)
            {
                MessageBox.Show("Auto-updates are paused on this machine.\n\nUncheck \"Pause auto-updates\" in the tray menu to resume.",
                    "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        // If a background check is already running, don't queue interactive
        // clicks behind it — just tell the user.
        if (!await _updateGate.WaitAsync(interactive ? 0 : Timeout.Infinite))
        {
            if (interactive)
            {
                MessageBox.Show("An update check is already in progress. Please try again in a moment.",
                    "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

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
            // Mark as shutting down and hide icon NOW so there's no ghost icon
            // and no timer callbacks fire during the restart handoff.
            _shuttingDown = true;
            _icon.Visible = false;
            await StopServerAsync();
            // Release the single-instance mutex BEFORE Velopack launches the new
            // process. Without this the new instance hits `!owned` and exits
            // immediately, leaving nothing in the tray after the update.
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            // ApplyUpdatesAndRestart swaps current/ and relaunches ServerHost.exe.
            _updater.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex) when (ex.Message.Contains("exclusive lock", StringComparison.OrdinalIgnoreCase))
        {
            // Stale lock from a previously-killed ServerHost, or a Velopack
            // operation still running in another process. Restarting the tray
            // (Quit + relaunch) clears it; auto-update will retry next hour.
            if (interactive)
            {
                MessageBox.Show(
                    "Another update operation is currently holding the install lock.\n\n" +
                    "This usually clears on its own within a few minutes. If it persists, " +
                    "right-click the tray icon and choose \"Quit (stop server)\", then re-launch MyLocalAssistant.",
                    "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show("Update check failed:\n" + ex.Message,
                "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    // ---- pause auto-updates -------------------------------------------------

    /// <summary>
    /// True when auto-update checks should be skipped. Either the env var
    /// <c>MLA_DISABLE_AUTO_UPDATE=1</c> is set, or the marker file
    /// <c>state\config\auto-update.disabled</c> exists. The env var is the
    /// stopgap for already-deployed builds; the tray toggle writes the marker.
    /// </summary>
    private static bool AutoUpdatesPaused()
    {
        var env = Environment.GetEnvironmentVariable("MLA_DISABLE_AUTO_UPDATE");
        if (!string.IsNullOrEmpty(env) && env != "0" && !env.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;
        try { return File.Exists(PauseMarkerPath()); } catch { return false; }
    }

    /// <summary>Marker file that disables auto-updates, kept under <c>state\config\</c> so it survives Velopack swaps.</summary>
    private static string PauseMarkerPath()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Mirror ServerPaths.cs without taking a project reference.
        string stateDir;
        if (string.Equals(Path.GetFileName(appDir), "current", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(appDir)?.FullName ?? appDir;
            stateDir = Path.Combine(parent, "state");
        }
        else
        {
            stateDir = appDir;
        }
        return Path.Combine(stateDir, "config", "auto-update.disabled");
    }

    private void TogglePauseUpdates()
    {
        try
        {
            var marker = PauseMarkerPath();
            if (File.Exists(marker))
            {
                File.Delete(marker);
                ShowBalloon("MyLocalAssistant", "Auto-updates resumed.", ToolTipIcon.Info);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, $"Paused at {DateTime.Now:O} by tray menu.\n");
                ShowBalloon("MyLocalAssistant", "Auto-updates paused.\nThe tray will not download or install updates until you uncheck this.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not toggle auto-update state:\n" + ex.Message,
                "MyLocalAssistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshPauseUpdatesItem();
    }

    private void RefreshPauseUpdatesItem()
    {
        var paused = AutoUpdatesPaused();
        _pauseUpdatesItem.Checked = paused;
        // If the env var forced the pause, the menu can't unpause it - signal that.
        var envForced = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MLA_DISABLE_AUTO_UPDATE"));
        _pauseUpdatesItem.Enabled = !envForced;
        _pauseUpdatesItem.ToolTipText = envForced
            ? "Forced off by MLA_DISABLE_AUTO_UPDATE environment variable."
            : (paused ? "Click to resume automatic update checks." : "Click to stop automatic update checks on this machine.");
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
