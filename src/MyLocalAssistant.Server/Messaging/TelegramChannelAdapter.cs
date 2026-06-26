using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Telegram channel adapter using the Telegram.Bot SDK (long-polling).
/// Configure via appsettings: Messaging:Telegram:BotToken.
/// </summary>
public sealed class TelegramChannelAdapter : IMessageChannel, IDisposable
{
    public string  ChannelId   => "telegram";
    public string  DisplayName => "Telegram";
    public bool    IsConnected => _client is not null && _connected;

    public event Func<ChannelMessage, CancellationToken, Task>? OnMessageReceived;

    private readonly string _botToken;
    private readonly ILogger<TelegramChannelAdapter> _logger;
    private TelegramBotClient? _client;
    private CancellationTokenSource? _cts;
    private bool _connected;

    public TelegramChannelAdapter(string botToken, ILogger<TelegramChannelAdapter> logger)
    {
        _botToken = botToken;
        _logger   = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
        {
            _logger.LogWarning("Telegram: BotToken not configured — channel disabled.");
            return Task.CompletedTask;
        }

        _cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new TelegramBotClient(_botToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };

        _client.StartReceiving(
            updateHandler:      HandleUpdateAsync,
            errorHandler:       HandleErrorAsync,
            receiverOptions:    receiverOptions,
            cancellationToken:  _cts.Token);

        _connected = true;
        _logger.LogInformation("Telegram channel started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _connected = false;
        _logger.LogInformation("Telegram channel stopped.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Telegram channel not started.");

        var parseMode = message.ParseMode?.ToLowerInvariant() switch
        {
            "html"     => ParseMode.Html,
            "markdown" => ParseMode.MarkdownV2,
            _          => ParseMode.None,
        };

        await _client.SendMessage(
            chatId:    message.Recipient,
            text:      message.Text,
            parseMode: parseMode,
            cancellationToken: ct);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } msg) return;

        var inbound = new ChannelMessage(
            ChannelId:   ChannelId,
            SenderId:    msg.Chat.Id.ToString(),
            SenderName:  msg.Chat.Username ?? msg.Chat.FirstName ?? "unknown",
            Text:        text,
            ReceivedAt:  DateTimeOffset.UtcNow);

        if (OnMessageReceived is { } handler)
            await handler(inbound, ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        if (ex is ApiRequestException api)
            _logger.LogWarning("Telegram API error {Code}: {Msg}", api.ErrorCode, api.Message);
        else
            _logger.LogWarning(ex, "Telegram receiver error.");
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();
}
