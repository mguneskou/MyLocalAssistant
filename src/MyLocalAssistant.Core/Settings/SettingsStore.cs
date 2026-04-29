using System.Text.Json;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Paths.SettingsFile;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream, s_json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, settings, s_json);
        }
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }
}
