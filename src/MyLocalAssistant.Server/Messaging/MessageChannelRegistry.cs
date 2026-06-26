using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Aggregates all registered IMessageChannel adapters.
/// Provides lookup by channel id and broadcast helpers.
/// </summary>
public sealed class MessageChannelRegistry
{
    private readonly IReadOnlyDictionary<string, IMessageChannel> _channels;
    private readonly ILogger<MessageChannelRegistry> _logger;

    public MessageChannelRegistry(
        IEnumerable<IMessageChannel> channels,
        ILogger<MessageChannelRegistry> logger)
    {
        _logger = logger;
        var dict = new Dictionary<string, IMessageChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in channels)
        {
            if (!dict.TryAdd(ch.ChannelId, ch))
                _logger.LogWarning("Duplicate channel id '{Id}' — second registration ignored.", ch.ChannelId);
        }
        _channels = dict;
    }

    public IMessageChannel? Get(string channelId) => _channels.GetValueOrDefault(channelId);

    public IReadOnlyCollection<IMessageChannel> All() =>
        (IReadOnlyCollection<IMessageChannel>)_channels.Values;

    public async Task SendAsync(string channelId, ChannelOutboundMessage message, CancellationToken ct)
    {
        var ch = Get(channelId);
        if (ch is null)
        {
            _logger.LogWarning("SendAsync: channel '{Id}' not found.", channelId);
            return;
        }
        await ch.SendAsync(message, ct);
    }
}
