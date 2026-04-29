using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Inference;

namespace MyLocalAssistant.App.Forms;

internal sealed class MainForm : Form
{
    private readonly IServiceProvider _services;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _backendLabel;
    private readonly ToolStripStatusLabel _modelsLabel;

    public MainForm(IServiceProvider services)
    {
        _services = services;

        Text = "MyLocalAssistant";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 500);
        Size = new Size(900, 600);
        Font = new Font("Segoe UI", 9F);

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close()) { ShortcutKeys = Keys.Alt | Keys.F4 };
        fileMenu.DropDownItems.Add(exitItem);

        var modelsMenu = new ToolStripMenuItem("&Models");
        var manageItem = new ToolStripMenuItem("&Manage…", null, OnManageModels);
        modelsMenu.DropDownItems.Add(manageItem);

        var debugMenu = new ToolStripMenuItem("&Debug");
        var smokeTest = new ToolStripMenuItem("&Smoke test current model", null, OnSmokeTest)
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.T,
        };
        debugMenu.DropDownItems.Add(smokeTest);

        var helpMenu = new ToolStripMenuItem("&Help");
        var aboutItem = new ToolStripMenuItem("&About", null, (_, _) =>
            MessageBox.Show(this,
                "MyLocalAssistant\n\nLocal LLM runner powered by LLamaSharp (llama.cpp).",
                "About",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information));
        helpMenu.DropDownItems.Add(aboutItem);

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, modelsMenu, debugMenu, helpMenu });
        MainMenuStrip = menu;
        Controls.Add(menu);

        _backendLabel = new ToolStripStatusLabel("Backend: detecting…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _modelsLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_backendLabel);
        _status.Items.Add(_modelsLabel);
        Controls.Add(_status);

        var placeholder = new Label
        {
            Text = "Chat UI coming in the next iteration.\n\nUse Models → Manage… to install/remove models.\nUse Debug → Smoke test (Ctrl+Shift+T) to verify inference.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 11F),
        };
        Controls.Add(placeholder);

        Load += (_, _) => RefreshStatus();
    }

    private void RefreshStatus()
    {
        _backendLabel.Text = $"Backend: {BackendSelector.SelectedBackend}";
        var catalog = _services.GetRequiredService<ModelCatalogService>();
        var installed = catalog.GetInstalled(Paths.ModelsDirectory);
        _modelsLabel.Text = $"Installed: {installed.Count}";
    }

    private void OnManageModels(object? sender, EventArgs e)
    {
        using var dlg = new ModelsForm(_services);
        dlg.ShowDialog(this);
        RefreshStatus();
    }

    private async void OnSmokeTest(object? sender, EventArgs e)
    {
        var catalog = _services.GetRequiredService<ModelCatalogService>();
        var logger = _services.GetRequiredService<ILogger<MainForm>>();
        var installed = catalog.GetInstalled(Paths.ModelsDirectory);
        if (installed.Count == 0)
        {
            MessageBox.Show(this, "No models installed.", "Smoke Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var smallest = installed.OrderBy(m => m.SizeOnDisk).First();

        Cursor = Cursors.WaitCursor;
        try
        {
            await using var provider = new LLamaSharpProvider(_services.GetRequiredService<ILogger<LLamaSharpProvider>>());
            await provider.LoadAsync(smallest.PrimaryFilePath, smallest.Catalog.Id, Math.Min(2048, smallest.Catalog.RecommendedContextSize));
            var sb = new System.Text.StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await foreach (var token in provider.GenerateAsync("Hello! Reply with a short greeting.", maxTokens: 10, cts.Token))
            {
                sb.Append(token);
            }
            RefreshStatus();
            logger.LogInformation("Smoke test OK on {Id}: {Text}", smallest.Catalog.Id, sb);
            MessageBox.Show(this,
                $"Model: {smallest.Catalog.DisplayName}\nBackend: {BackendSelector.SelectedBackend}\n\nOutput:\n{sb}",
                "Smoke Test OK",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Smoke test failed");
            MessageBox.Show(this, ex.ToString(), "Smoke Test Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
