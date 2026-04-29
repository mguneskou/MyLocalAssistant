namespace MyLocalAssistant.Core;

/// <summary>
/// Resolves portable folders relative to the running executable.
/// </summary>
public static class Paths
{
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    public static string ModelsDirectory { get; } = Path.Combine(AppDirectory, "models");

    public static string ConfigDirectory { get; } = Path.Combine(AppDirectory, "config");

    public static string LogsDirectory { get; } = Path.Combine(AppDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(ConfigDirectory, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
