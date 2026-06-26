namespace MyLocalAssistant.Server.Messaging;

/// <summary>Direction of a channel message.</summary>
public enum MessageDirection { Inbound, Outbound }

/// <summary>An inbound message received from a messaging channel.</summary>
public sealed record ChannelMessage(
    string ChannelId,
    string SenderId,
    string SenderName,
    string Text,
    DateTimeOffset ReceivedAt,
    string? RawPayload = null);

/// <summary>An outbound message to send via a messaging channel.</summary>
public sealed record ChannelOutboundMessage(
    string Recipient,
    string Text,
    string? ParseMode = null);
