namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Contract every messaging channel adapter implements.
/// Each adapter handles one external platform (Telegram, Teams, Email, etc.).
/// </summary>
public interface IMessageChannel
{
    /// <summary>Stable lowercase id, e.g. "telegram", "teams", "email".</summary>
    string ChannelId { get; }

    string DisplayName { get; }

    /// <summary>True while the channel is connected and receiving updates.</summary>
    bool IsConnected { get; }

    /// <summary>Start listening for inbound messages.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop gracefully.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Send a message to a recipient on this channel.</summary>
    Task SendAsync(ChannelOutboundMessage message, CancellationToken ct);

    /// <summary>Raised when an inbound message arrives.</summary>
    event Func<ChannelMessage, CancellationToken, Task> OnMessageReceived;
}
