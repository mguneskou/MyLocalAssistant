using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Routes inbound channel messages to the appropriate skill or agent.
/// Uses IntentClassifier for bilingual keyword matching (EN + TR).
/// </summary>
public sealed class InboundMessageRouter
{
    private readonly SkillRegistry _skills;
    private readonly SkillExecutor _executor;
    private readonly MessageChannelRegistry _channels;
    private readonly ILogger<InboundMessageRouter> _logger;

    public InboundMessageRouter(
        SkillRegistry skills,
        SkillExecutor executor,
        MessageChannelRegistry channels,
        ILogger<InboundMessageRouter> logger)
    {
        _skills   = skills;
        _executor = executor;
        _channels = channels;
        _logger   = logger;
    }

    public async Task HandleAsync(ChannelMessage message, CancellationToken ct)
    {
        var skillId = IntentClassifier.Classify(message.Text);

        if (skillId is null)
        {
            _logger.LogDebug("[{Channel}] No skill matched for message from {Sender}.", message.ChannelId, message.SenderName);
            await _channels.SendAsync(message.ChannelId,
                new ChannelOutboundMessage(message.SenderId,
                    "I received your message but couldn't match it to a workflow. " +
                    "Try: shortage review, purchase request, or supplier follow-up."),
                ct);
            return;
        }

        _logger.LogInformation("[{Channel}] Routing to skill '{Skill}' for {Sender}.", message.ChannelId, skillId, message.SenderName);

        // Acknowledge immediately.
        await _channels.SendAsync(message.ChannelId,
            new ChannelOutboundMessage(message.SenderId, $"Processing your request with the '{_skills.Find(skillId)?.Name ?? skillId}' workflow..."),
            ct);

        var context = new SkillContext(
            UserId:           Guid.Empty,
            Username:         message.SenderName,
            IsAdmin:          false,
            AgentId:          "default",
            ConversationId:   Guid.NewGuid(),
            WorkDirectory:    ServerPaths.StateDirectory,
            UserMessage:      message.Text,
            Tools:            new Dictionary<string, Server.Tools.ITool>(),
            CancellationToken: ct);

        var result = await _executor.RunAsync(skillId, context, ct);

        await _channels.SendAsync(message.ChannelId,
            new ChannelOutboundMessage(message.SenderId, result.Summary),
            ct);
    }
}
