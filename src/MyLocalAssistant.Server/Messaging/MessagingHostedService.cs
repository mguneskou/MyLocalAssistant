using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Hosted service that starts and stops all registered IMessageChannel adapters.
/// Also wires inbound messages into the skill/agent pipeline via InboundMessageRouter.
/// </summary>
public sealed class MessagingHostedService(
    MessageChannelRegistry registry,
    InboundMessageRouter router,
    ILogger<MessagingHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var ch in registry.All())
        {
            try
            {
                ch.OnMessageReceived += HandleInboundAsync;
                await ch.StartAsync(ct);
                log.LogInformation("Messaging: channel '{Id}' started.", ch.ChannelId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Messaging: failed to start channel '{Id}'.", ch.ChannelId);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var ch in registry.All())
        {
            try { await ch.StopAsync(ct); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Messaging: error stopping channel '{Id}'.", ch.ChannelId);
            }
        }
    }

    private async Task HandleInboundAsync(ChannelMessage message, CancellationToken ct)
    {
        log.LogInformation("[{Channel}] Inbound from {Sender}: {Text}",
            message.ChannelId, message.SenderName, message.Text);
        await router.HandleAsync(message, ct);
    }
}
