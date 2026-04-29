using System.Text.Json;

namespace MyLocalAssistant.Admin.Services;

/// <summary>
/// Persisted per-user admin app settings (server URL, last username).
/// Stored under %APPDATA%\MyLocalAssistant\admin.json. Passwords are NEVER persisted.
/// </summary>
public sealed class AdminSettings
{
    public string ServerUrl { get; set; } = "http://localhost:8080";
    public string? LastUsername { get; set; }
    public bool RememberUsername { get; set; } = true;
}

public sealed class AdminSettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyLocalAssistant");
    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "admin.json");

    public AdminSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AdminSettings();
            using var s = File.OpenRead(SettingsPath);
            return JsonSerializer.Deserialize<AdminSettings>(s, s_json) ?? new AdminSettings();
        }
        catch
        {
            return new AdminSettings();
        }
    }

    public void Save(AdminSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        using var s = File.Create(SettingsPath);
        JsonSerializer.Serialize(s, settings, s_json);
    }
}
