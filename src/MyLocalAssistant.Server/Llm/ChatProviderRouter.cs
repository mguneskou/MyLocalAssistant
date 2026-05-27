using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Picks the right <see cref="IChatProvider"/> for a given catalog entry. One per
/// <see cref="ModelSource"/>; lookup is by source so adding a new provider is just
/// a DI registration plus a catalog entry.
/// </summary>
public sealed class ChatProviderRouter
{
    private readonly Dictionary<ModelSource, IChatProvider> _bySource;

    public ChatProviderRouter(IEnumerable<IChatProvider> providers)
    {
        _bySource = providers.ToDictionary(p => p.Source);
    }

    public IChatProvider Get(CatalogEntry entry)
    {
        if (_bySource.TryGetValue(entry.Source, out var p)) return p;
        throw new InvalidOperationException($"No provider registered for model source '{entry.Source}'.");
    }

    public IChatProvider GetForSource(ModelSource source) =>
        _bySource.TryGetValue(source, out var p)
            ? p
            : throw new InvalidOperationException($"No provider registered for model source '{source}'.");
}
