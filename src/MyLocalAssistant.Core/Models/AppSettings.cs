namespace MyLocalAssistant.Core.Models;

public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public string? PreferredBackend { get; set; }
    public string? LastUsedModelId { get; set; }
    public int SettingsVersion { get; set; } = 1;
}
