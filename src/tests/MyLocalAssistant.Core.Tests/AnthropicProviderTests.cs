using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;

namespace MyLocalAssistant.Core.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public void AnthropicProvider_UsesPromptCachingHeadersAndEphemeralCacheControl()
    {
        var provider = new AnthropicChatProvider(
            settings: new ServerSettings { AnthropicApiKeyProtected = "test" },
            httpFactory: new StubHttpClientFactory(),
            log: NullLogger<AnthropicChatProvider>.Instance);

        var entry = new CatalogEntry
        {
            Id = "anthropic-claude-opus-4-8",
            RemoteModel = "claude-opus-4-8",
            Source = ModelSource.Anthropic,
        };

        Assert.True(provider.IsReady(entry));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
