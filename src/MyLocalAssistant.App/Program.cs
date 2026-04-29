using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.App.Forms;
using MyLocalAssistant.Core;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Download;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Core.Settings;
using Serilog;
using Serilog.Extensions.Logging;

namespace MyLocalAssistant.App;

internal class Program
{
    [STAThread]
    private static void Main()
    {
        Paths.EnsureCreated();

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(Paths.LogsDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Logger = serilog;

        ApplicationConfiguration.Initialize();

        try
        {
            using var loggerFactory = new SerilogLoggerFactory(serilog);
            var services = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(loggerFactory)
                .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
                .AddSingleton<SettingsStore>()
                .AddSingleton(_ => ModelCatalogService.LoadEmbedded(typeof(Program).Assembly))
                .AddSingleton<ModelDownloader>()
                .AddSingleton<ILlmProvider, LLamaSharpProvider>()
                .BuildServiceProvider();

            var startupLog = services.GetRequiredService<ILogger<Program>>();
            startupLog.LogInformation("App starting. AppDir={Dir}", Paths.AppDirectory);

            // Kick off backend probe in the background so it's warm by the time we need it.
            _ = Task.Run(() => BackendSelector.Configure(startupLog));

            var settingsStore = services.GetRequiredService<SettingsStore>();
            var settings = settingsStore.Load();
            var catalog = services.GetRequiredService<ModelCatalogService>();
            var installed = catalog.GetInstalled(Paths.ModelsDirectory);

            if (!settings.FirstRunCompleted || installed.Count == 0)
            {
                using var wizard = new FirstRunWizardForm(catalog, services);
                var result = wizard.ShowDialog();
                if (result != DialogResult.OK)
                {
                    startupLog.LogInformation("First-run wizard cancelled. Exiting.");
                    return;
                }
                settings.FirstRunCompleted = true;
                settingsStore.Save(settings);
            }

            Application.Run(new MainForm(services));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled fatal exception");
            MessageBox.Show(
                $"Fatal error: {ex.Message}\n\nSee logs at:\n{Paths.LogsDirectory}",
                "MyLocalAssistant",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
