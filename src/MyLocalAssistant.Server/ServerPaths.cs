namespace MyLocalAssistant.Server;

/// <summary>
/// Resolves all server folders relative to the executable so the install stays portable.
/// </summary>
public static class ServerPaths
{
    public static string AppDirectory { get; } = AppContext.BaseDirectory;
    public static string ModelsDirectory { get; } = Path.Combine(AppDirectory, "models");
    public static string DataDirectory { get; } = Path.Combine(AppDirectory, "data");
    public static string VectorsDirectory { get; } = Path.Combine(AppDirectory, "vectors");
    public static string IngestionDirectory { get; } = Path.Combine(AppDirectory, "ingestion");
    public static string LogsDirectory { get; } = Path.Combine(AppDirectory, "logs");
    public static string ConfigDirectory { get; } = Path.Combine(AppDirectory, "config");
    public static string PluginsDirectory { get; } = Path.Combine(AppDirectory, "plugins");
    public static string OutputDirectory { get; } = Path.Combine(AppDirectory, "output");
    public static string TrustedKeysDirectory { get; } = Path.Combine(ConfigDirectory, "trusted-keys");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "app.db");
    public static string SettingsFilePath { get; } = Path.Combine(ConfigDirectory, "server.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(VectorsDirectory);
        Directory.CreateDirectory(IngestionDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(TrustedKeysDirectory);
    }
}
