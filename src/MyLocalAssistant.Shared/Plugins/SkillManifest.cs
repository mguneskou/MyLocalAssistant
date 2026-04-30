using System.Text.Json.Serialization;

namespace MyLocalAssistant.Shared.Plugins;

/// <summary>
/// Plug-in metadata shipped at <c>&lt;install&gt;/plugins/&lt;id&gt;/manifest.json</c>. The
/// manifest is the only file that is signed (with ed25519); each binary it lists carries
/// its own SHA-256 hash so tampering with a DLL after signing is detected on load.
/// </summary>
public sealed class SkillManifest
{
    /// <summary>Stable, lower-case skill id (matches the catalog/SkillState row).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Plugin";
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    /// <summary>Human-readable publisher name (informational; trust comes from <see cref="KeyId"/>).</summary>
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    /// <summary>Filename of the trusted public key (without extension) used to sign this manifest.</summary>
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";
    /// <summary>Tool-call protocol the skill expects (see <c>ToolCallProtocols</c>).</summary>
    [JsonPropertyName("toolMode")] public string ToolMode { get; set; } = "tags";
    [JsonPropertyName("minContextK")] public int MinContextK { get; set; } = 4;

    /// <summary>Executable command line. <c>{plugin}</c> is replaced with the plug-in folder.</summary>
    [JsonPropertyName("entry")] public ManifestEntry Entry { get; set; } = new();

    /// <summary>SHA-256 hex hashes (lower-case) of every file in the plug-in folder that is
    /// part of the signed payload. Loader recomputes and rejects on mismatch.</summary>
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();

    /// <summary>The tools this plug-in exposes. Mirrors the in-process <c>SkillToolDto</c>.</summary>
    [JsonPropertyName("tools")] public List<ManifestTool> Tools { get; set; } = new();
}

public sealed class ManifestEntry
{
    /// <summary>Executable to launch (relative to plug-in folder).</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    /// <summary>Optional argv passed to the executable.</summary>
    [JsonPropertyName("args")] public List<string> Args { get; set; } = new();
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class ManifestTool
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>JSON-Schema (raw string) describing the arguments object.</summary>
    [JsonPropertyName("argumentsSchemaJson")] public string ArgumentsSchemaJson { get; set; } = "{}";
}
