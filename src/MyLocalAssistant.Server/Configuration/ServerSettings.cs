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
    /// <summary>Days to keep audit entries. Older rows are deleted entirely.</summary>
    public int AuditRetentionDays { get; set; } = 365;
    /// <summary>Catalog id of the LLM used for all agents in v2.0 (single-model mode).</summary>
    public string? DefaultModelId { get; set; }
    /// <summary>Catalog id of the embedding model used for RAG. Loaded alongside the chat model.</summary>
    public string? EmbeddingModelId { get; set; }
    /// <summary>Path to a PFX file containing the TLS certificate. When set, Kestrel binds the listen URL with HTTPS.</summary>
    public string? CertificatePath { get; set; }
    /// <summary>Password for the PFX file referenced by <see cref="CertificatePath"/>. May be empty.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>LDAP/AD authentication. When disabled, only local accounts work (default).</summary>
    public LdapSettings Ldap { get; set; } = new();
}

public sealed class LdapSettings
{
    public bool Enabled { get; set; }
    /// <summary>Host or IP of the directory server (e.g. "dc01.corp.local").</summary>
    public string Server { get; set; } = "";
    public int Port { get; set; } = 389;
    /// <summary>When true, uses LDAPS on the configured port.</summary>
    public bool UseSsl { get; set; }
    /// <summary>Search base (e.g. "DC=corp,DC=local").</summary>
    public string BaseDn { get; set; } = "";
    /// <summary>Bind account DN used to look up users (read-only). Leave empty for anonymous bind.</summary>
    public string BindDn { get; set; } = "";
    public string BindPassword { get; set; } = "";
    /// <summary>LDAP filter to locate the user. {0} is replaced by the supplied username, escaped.</summary>
    public string UserFilter { get; set; } = "(&(objectClass=user)(sAMAccountName={0}))";
    /// <summary>Attribute used as the canonical username on the local side. Usually sAMAccountName.</summary>
    public string UsernameAttribute { get; set; } = "sAMAccountName";
    /// <summary>Attribute used for the display name. Usually displayName or cn.</summary>
    public string DisplayNameAttribute { get; set; } = "displayName";
    /// <summary>AD group (CN or full DN) whose members become local admins.</summary>
    public string? AdminGroup { get; set; }
    /// <summary>Map of AD group (CN or DN) -> local department name. Memberships are union, not exclusive.</summary>
    public Dictionary<string, string> GroupToDepartment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
