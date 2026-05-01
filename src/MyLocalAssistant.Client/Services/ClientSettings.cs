using System.Text.Json;

namespace MyLocalAssistant.Client.Services;

public sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:8080";
    public string? LastUsername { get; set; }
    public bool RememberUsername { get; set; } = true;
    public string? LastAgentId { get; set; }

    /// <summary>
    /// Local folder the client makes available to skills via the v2.2 fs.* bridge.
    /// All bridge calls are confined to this root (and subfolders the user creates under it).
    /// Null/empty means the bridge is disabled and every fs.* request returns fs.notConfigured.
    /// </summary>
    public string? BridgeRoot { get; set; }

    /// <summary>Persisted UI theme preference.</summary>
    public bool DarkTheme { get; set; }
}

public sealed class ClientSettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyLocalAssistant");
    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "client.json");

    public ClientSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ClientSettings();
            using var s = File.OpenRead(SettingsPath);
            return JsonSerializer.Deserialize<ClientSettings>(s, s_json) ?? new ClientSettings();
        }
        catch { return new ClientSettings(); }
    }

    public void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        using var s = File.Create(SettingsPath);
        JsonSerializer.Serialize(s, settings, s_json);
    }
}
