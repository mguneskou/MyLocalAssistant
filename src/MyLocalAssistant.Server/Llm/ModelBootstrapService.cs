using Microsoft.Extensions.Hosting;
using MyLocalAssistant.Server.Llm;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// On startup, eagerly loads the configured default model (if installed) so the
/// first chat request doesn't pay the load cost.
/// </summary>
public sealed class ModelBootstrapService(IServiceProvider sp) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = sp.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ModelManager>();
        await mgr.EnsureLoadedOnStartupAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
