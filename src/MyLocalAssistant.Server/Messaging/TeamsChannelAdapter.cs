using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Microsoft Teams channel adapter.
/// Outbound: Incoming Webhook (no app registration needed).
/// Inbound:  Exposes a POST endpoint at /api/messaging/teams that Teams calls via Bot Framework.
///           Wire the endpoint in Program.cs when Bot Framework auth is configured.
/// Configure via appsettings: Messaging:Teams:WebhookUrl
/// </summary>
public sealed class TeamsChannelAdapter : IMessageChannel, IDisposable
{
    public string  ChannelId   => "teams";
    public string  DisplayName => "Microsoft Teams";
    public bool    IsConnected => !string.IsNullOrWhiteSpace(_webhookUrl);

    public event Func<ChannelMessage, CancellationToken, Task>? OnMessageReceived;

    private readonly string _webhookUrl;
    private readonly HttpClient _http;
    private readonly ILogger<TeamsChannelAdapter> _logger;

    public TeamsChannelAdapter(string webhookUrl, HttpClient http, ILogger<TeamsChannelAdapter> logger)
    {
        _webhookUrl = webhookUrl;
        _http       = http;
        _logger     = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl))
        {
            _logger.LogWarning("Teams: WebhookUrl not configured — outbound disabled.");
            return Task.CompletedTask;
        }
        _logger.LogInformation("Teams channel ready (outbound webhook).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Sends a plain-text or Markdown message to the configured Teams channel via Incoming Webhook.
    /// The <paramref name="message"/> Recipient field is ignored for webhook delivery
    /// (the webhook targets a fixed channel); it is used when Bot Framework routing is added.
    /// </summary>
    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl))
        {
            _logger.LogWarning("Teams: cannot send — WebhookUrl not configured.");
            return;
        }

        // Adaptive Card / simple message card payload
        var payload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type    = "AdaptiveCard",
                        version = "1.4",
                        body    = new[] { new { type = "TextBlock", text = message.Text, wrap = true } },
                    }
                }
            }
        };

        var response = await _http.PostAsJsonAsync(_webhookUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Teams webhook returned {Status}.", response.StatusCode);
    }

    /// <summary>Called by the /api/messaging/teams endpoint when a Teams bot message arrives.</summary>
    public async Task HandleInboundWebhookAsync(string senderId, string senderName, string text, CancellationToken ct)
    {
        if (OnMessageReceived is { } handler)
            await handler(new ChannelMessage(ChannelId, senderId, senderName, text, DateTimeOffset.UtcNow), ct);
    }

    public void Dispose() { }
}
