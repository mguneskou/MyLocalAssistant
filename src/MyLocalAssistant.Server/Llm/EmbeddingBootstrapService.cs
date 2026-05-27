using Microsoft.Extensions.Hosting;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Eagerly loads the configured embedding model on startup so first RAG query has zero load lag.
/// </summary>
public sealed class EmbeddingBootstrapService(EmbeddingService embedding) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => embedding.EnsureLoadedOnStartupAsync();
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
