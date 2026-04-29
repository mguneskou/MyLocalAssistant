using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Core.Catalog;

public sealed class ModelCatalogService
{
    public const string EmbeddedResourceName = "MyLocalAssistant.App.Resources.model-catalog.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyList<CatalogEntry> _entries;

    public ModelCatalogService(IReadOnlyList<CatalogEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<CatalogEntry> Entries => _entries;

    public CatalogEntry? FindById(string id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Loads catalog JSON from a stream.
    /// </summary>
    public static ModelCatalogService LoadFromStream(Stream stream)
    {
        var entries = JsonSerializer.Deserialize<List<CatalogEntry>>(stream, s_json)
            ?? throw new InvalidDataException("Catalog JSON deserialized to null.");
        ValidateNoDuplicates(entries);
        return new ModelCatalogService(entries);
    }

    /// <summary>
    /// Loads from an embedded resource on the given assembly. Defaults to a search across loaded assemblies.
    /// </summary>
    public static ModelCatalogService LoadEmbedded(Assembly? assembly = null, string? resourceName = null)
    {
        resourceName ??= EmbeddedResourceName;
        var asm = assembly ?? AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetManifestResourceNames().Contains(resourceName))
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found in any loaded assembly.");
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return LoadFromStream(stream);
    }

    private static void ValidateNoDuplicates(IEnumerable<CatalogEntry> entries)
    {
        var dup = entries.GroupBy(e => e.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            throw new InvalidDataException($"Duplicate catalog entry id: '{dup.Key}'");
        }
    }

    /// <summary>
    /// Scans <paramref name="modelsDir"/> for installed models matching catalog entries.
    /// Matching is by primary file name; SHA verification is the caller's responsibility.
    /// </summary>
    public IReadOnlyList<InstalledModel> GetInstalled(string modelsDir)
    {
        var result = new List<InstalledModel>();
        if (!Directory.Exists(modelsDir)) return result;

        foreach (var entry in _entries)
        {
            if (entry.Files.Count == 0) continue;
            var primary = entry.Files[0];
            var path = Path.Combine(modelsDir, entry.Id, primary.FileName);
            if (!File.Exists(path)) continue;

            // All files must exist for multi-shard models.
            if (entry.Files.Skip(1).Any(f => !File.Exists(Path.Combine(modelsDir, entry.Id, f.FileName))))
            {
                continue;
            }

            var size = entry.Files.Sum(f =>
            {
                var p = Path.Combine(modelsDir, entry.Id, f.FileName);
                return File.Exists(p) ? new FileInfo(p).Length : 0;
            });

            result.Add(new InstalledModel
            {
                Catalog = entry,
                PrimaryFilePath = path,
                SizeOnDisk = size,
            });
        }
        return result;
    }

    public static string ResolveDestinationPath(string modelsDir, CatalogEntry entry, CatalogFile file)
        => Path.Combine(modelsDir, entry.Id, file.FileName);
}
