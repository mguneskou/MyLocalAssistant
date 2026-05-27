using System.Text;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Hosting;

/// <summary>
/// Background service that periodically compresses old messages in long conversations
/// into a concise summary stored as a <see cref="MessageRole.System"/> message.
///
/// Strategy:
///   • Runs every <see cref="s_interval"/>.
///   • Selects conversations with more than <see cref="CompressThreshold"/> non-system
///     messages and no summary within the last <see cref="s_summaryWindow"/> messages.
///   • Takes the oldest unsummarised batch (first <see cref="BatchSize"/> messages),
///     calls the active LLM with a summarisation prompt, then:
///       - Deletes the source messages.
///       - Inserts one System message with the summary text.
///   • If no model is loaded the pass is skipped silently.
///
/// Configuration (<see cref="ServerSettings"/>):
///   <c>SummarizationEnabled</c>       — opt-in, default false.
///   <c>SummarizationThreshold</c>     — message count before summarisation kicks in.
///   <c>SummarizationBatchSize</c>     — how many messages to collapse per pass.
/// </summary>
public sealed class MemorySummarizationService(
    IServiceScopeFactory scopes,
    ModelManager models,
    ModelCatalogService catalog,
    ChatProviderRouter router,
    ILogger<MemorySummarizationService> log) : BackgroundService
{
    private static readonly TimeSpan s_initialDelay  = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_interval      = TimeSpan.FromMinutes(30);
    /// <summary>How many messages from the bottom of the conversation are considered
    /// "recent" and never collapsed, regardless of the total count.</summary>
    private const int RecentWindowProtected = 6;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(s_initialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { log.LogWarning(ex, "Memory summarisation pass failed."); }

            try { await Task.Delay(s_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp       = scope.ServiceProvider;
        var db       = sp.GetRequiredService<AppDbContext>();
        var settings = sp.GetRequiredService<ServerSettings>();

        if (!settings.SummarizationEnabled) return;

        var threshold = Math.Max(10, settings.SummarizationThreshold);
        var batchSize = Math.Max(4, settings.SummarizationBatchSize);

        // Guard: need a loaded model.
        if (models.Status != ModelStatus.Loaded || models.ActiveModelId is null)
        {
            log.LogDebug("MemorySummarisation: no model loaded, skipping.");
            return;
        }
        var activeModelId = models.ActiveModelId;
        var entry         = catalog.FindById(activeModelId);
        if (entry is null) return;
        IChatProvider provider;
        try   { provider = router.Get(entry); }
        catch { return; }
        if (!provider.IsReady(entry)) return;

        // Find eligible conversations: more than `threshold` non-system, non-purged messages.
        var eligible = await db.Conversations
            .Where(c => db.Messages.Count(m =>
                m.ConversationId == c.Id &&
                m.Role != MessageRole.System &&
                m.Body != null) > threshold)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (eligible.Count == 0) return;
        log.LogInformation("MemorySummarisation: {Count} conversation(s) eligible.", eligible.Count);

        foreach (var convId in eligible)
        {
            try { await SummariseConversationAsync(db, provider, entry, convId, batchSize, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Summarisation failed for conversation {Id}.", convId); }
        }
    }

    private async Task SummariseConversationAsync(
        AppDbContext db,
        IChatProvider provider,
        CatalogEntry entry,
        Guid convId,
        int batchSize,
        CancellationToken ct)
    {
        // Load oldest non-system messages, excluding the most recent protected window.
        var allMessages = await db.Messages
            .Where(m => m.ConversationId == convId &&
                        m.Role != MessageRole.System &&
                        m.Body != null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.Role, m.Body, m.CreatedAt })
            .ToListAsync(ct);

        // Protect the most recent `RecentWindowProtected` messages.
        var protectedIds = allMessages
            .TakeLast(RecentWindowProtected)
            .Select(m => m.Id)
            .ToHashSet();

        var batch = allMessages
            .Where(m => !protectedIds.Contains(m.Id))
            .Take(batchSize)
            .ToList();

        if (batch.Count < 2) return; // Nothing meaningful to collapse.

        // Build summarisation prompt.
        var dialogue = new StringBuilder();
        foreach (var m in batch)
        {
            var role = m.Role == MessageRole.User ? "User" : "Assistant";
            dialogue.AppendLine($"{role}: {m.Body}");
        }

        var prompt =
            "You are a summarisation assistant. Read the following conversation excerpt and " +
            "produce a concise, neutral summary (3-6 sentences) that captures the key topics " +
            "and any important facts or decisions. Do not include greetings or filler.\n\n" +
            dialogue +
            "\nSummary:";

        // Call model.
        var resultBuilder = new StringBuilder();
        await foreach (var token in provider.GenerateAsync(entry, prompt, 300, [], ct))
            resultBuilder.Append(token);

        var summary = resultBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            log.LogWarning("MemorySummarisation: model returned empty summary for conversation {Id}.", convId);
            return;
        }

        // Replace batch with single System summary message.
        var batchIds = batch.Select(m => m.Id).ToList();
        var inserted = new Message
        {
            ConversationId = convId,
            Role           = MessageRole.System,
            Body           = $"[Summary of earlier conversation]\n{summary}",
            CreatedAt      = batch.First().CreatedAt, // place at start of compressed window
        };

        await db.Messages.Where(m => batchIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
        db.Messages.Add(inserted);
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "MemorySummarisation: compressed {N} messages into 1 summary in conversation {Id}.",
            batch.Count, convId);
    }
}
