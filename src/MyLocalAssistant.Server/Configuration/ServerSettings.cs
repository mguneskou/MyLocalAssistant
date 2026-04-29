using System.Security.Cryptography;
using System.Text.Json;

namespace MyLocalAssistant.Server.Configuration;

public sealed class ServerSettings
{
    public string ListenUrl { get; set; } = "http://0.0.0.0:8080";
    public string JwtIssuer { get; set; } = "MyLocalAssistant.Server";
    public string JwtAudience { get; set; } = "MyLocalAssistant.Clients";
    /// <summary>Base64-encoded 32+ byte signing key. Auto-generated on first run if empty.</summary>
    public string JwtSigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
    /// <summary>Days to keep message bodies. Metadata is kept indefinitely.</summary>
    public int MessageBodyRetentionDays { get; set; } = 90;
    /// <summary>Catalog id of the LLM used for all agents in v2.0 (single-model mode).</summary>
    public string? DefaultModelId { get; set; }
}

public sealed class ServerSettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ServerSettings Load()
    {
        ServerPaths.EnsureCreated();
        ServerSettings settings;
        if (File.Exists(ServerPaths.SettingsFilePath))
        {
            using var stream = File.OpenRead(ServerPaths.SettingsFilePath);
            settings = JsonSerializer.Deserialize<ServerSettings>(stream, s_json) ?? new ServerSettings();
        }
        else
        {
            settings = new ServerSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.JwtSigningKey))
        {
            settings.JwtSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            Save(settings);
        }
        return settings;
    }

    public void Save(ServerSettings settings)
    {
        ServerPaths.EnsureCreated();
        using var stream = File.Create(ServerPaths.SettingsFilePath);
        JsonSerializer.Serialize(stream, settings, s_json);
    }
}
