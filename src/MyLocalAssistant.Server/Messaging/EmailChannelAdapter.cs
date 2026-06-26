using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Email channel adapter.
/// Outbound: SMTP (any provider).
/// Inbound:  IMAP polling — polls the inbox every <see cref="PollInterval"/> for unseen messages.
/// Configure via appsettings: Messaging:Email:*
/// </summary>
public sealed class EmailChannelAdapter : IMessageChannel, IDisposable
{
    public string  ChannelId   => "email";
    public string  DisplayName => "Email";
    public bool    IsConnected => _running;

    public event Func<ChannelMessage, CancellationToken, Task>? OnMessageReceived;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly EmailChannelOptions _opts;
    private readonly ILogger<EmailChannelAdapter> _logger;
    private CancellationTokenSource? _cts;
    private bool _running;

    public EmailChannelAdapter(EmailChannelOptions opts, ILogger<EmailChannelAdapter> logger)
    {
        _opts   = opts;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!_opts.IsValid)
        {
            _logger.LogWarning("Email channel: configuration incomplete — channel disabled.");
            return Task.CompletedTask;
        }
        _cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;
        _ = PollLoopAsync(_cts.Token);
        _logger.LogInformation("Email channel started (polling every {Interval}).", PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _running = false;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(_opts.SmtpFrom));
        mime.To.Add(MailboxAddress.Parse(message.Recipient));
        mime.Subject = "MyLocalAssistant";
        mime.Body    = new TextPart("plain") { Text = message.Text };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_opts.SmtpHost, _opts.SmtpPort, _opts.SmtpUseSsl, ct);
        await smtp.AuthenticateAsync(_opts.SmtpUser, _opts.SmtpPassword, ct);
        await smtp.SendAsync(mime, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    // ── IMAP polling ─────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { await FetchUnseenAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Email poll error."); }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FetchUnseenAsync(CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(_opts.ImapHost, _opts.ImapPort, _opts.ImapUseSsl, ct);
        await imap.AuthenticateAsync(_opts.ImapUser, _opts.ImapPassword, ct);

        var inbox = imap.Inbox;
        await inbox.OpenAsync(MailKit.FolderAccess.ReadWrite, ct);

        var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
        foreach (var uid in uids)
        {
            var msg = await inbox.GetMessageAsync(uid, ct);
            var from = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "unknown";
            var name = msg.From.Mailboxes.FirstOrDefault()?.Name ?? from;
            var text = msg.TextBody ?? msg.HtmlBody ?? string.Empty;

            if (OnMessageReceived is { } handler)
                await handler(new ChannelMessage(ChannelId, from, name, text, DateTimeOffset.UtcNow), ct);

            var request = new MailKit.StoreFlagsRequest(MailKit.StoreAction.Add, MailKit.MessageFlags.Seen) { Silent = true };
            await inbox.StoreAsync(uid, request, ct);
        }

        await imap.DisconnectAsync(true, ct);
    }

    public void Dispose() => _cts?.Dispose();
}

public sealed class EmailChannelOptions
{
    public string SmtpHost     { get; set; } = string.Empty;
    public int    SmtpPort     { get; set; } = 587;
    public bool   SmtpUseSsl   { get; set; } = true;
    public string SmtpUser     { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFrom     { get; set; } = string.Empty;

    public string ImapHost     { get; set; } = string.Empty;
    public int    ImapPort     { get; set; } = 993;
    public bool   ImapUseSsl   { get; set; } = true;
    public string ImapUser     { get; set; } = string.Empty;
    public string ImapPassword { get; set; } = string.Empty;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        !string.IsNullOrWhiteSpace(SmtpUser) &&
        !string.IsNullOrWhiteSpace(ImapHost) &&
        !string.IsNullOrWhiteSpace(ImapUser);
}
