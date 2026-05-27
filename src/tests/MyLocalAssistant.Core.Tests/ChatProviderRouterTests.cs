using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Llm;

namespace MyLocalAssistant.Core.Tests;

public class ChatProviderRouterTests
{
    [Fact]
    public void GetForSource_resolves_every_registered_model_source()
    {
        var router = NewRouter();

        Assert.IsType<LocalProvider>(router.GetForSource(ModelSource.Local));
        Assert.IsType<OpenAiProvider>(router.GetForSource(ModelSource.OpenAi));
        Assert.IsType<AnthropicProvider>(router.GetForSource(ModelSource.Anthropic));
        Assert.IsType<GroqProvider>(router.GetForSource(ModelSource.Groq));
        Assert.IsType<GeminiProvider>(router.GetForSource(ModelSource.Gemini));
        Assert.IsType<MistralProvider>(router.GetForSource(ModelSource.Mistral));
        Assert.IsType<CerebrasProvider>(router.GetForSource(ModelSource.Cerebras));
    }

    [Fact]
    public void Get_uses_catalog_entry_source_for_every_registered_model_source()
    {
        var router = NewRouter();

        Assert.IsType<LocalProvider>(router.Get(new CatalogEntry { Id = "local", Source = ModelSource.Local }));
        Assert.IsType<OpenAiProvider>(router.Get(new CatalogEntry { Id = "openai", Source = ModelSource.OpenAi }));
        Assert.IsType<AnthropicProvider>(router.Get(new CatalogEntry { Id = "anthropic", Source = ModelSource.Anthropic }));
        Assert.IsType<GroqProvider>(router.Get(new CatalogEntry { Id = "groq", Source = ModelSource.Groq }));
        Assert.IsType<GeminiProvider>(router.Get(new CatalogEntry { Id = "gemini", Source = ModelSource.Gemini }));
        Assert.IsType<MistralProvider>(router.Get(new CatalogEntry { Id = "mistral", Source = ModelSource.Mistral }));
        Assert.IsType<CerebrasProvider>(router.Get(new CatalogEntry { Id = "cerebras", Source = ModelSource.Cerebras }));
    }

    [Fact]
    public void Unknown_source_throws()
    {
        var router = new ChatProviderRouter(new IChatProvider[] { new LocalProvider() });
        var unknown = (ModelSource)9999;

        Assert.Throws<InvalidOperationException>(() => router.GetForSource(unknown));
        Assert.Throws<InvalidOperationException>(() =>
            router.Get(new CatalogEntry { Id = "x", Source = unknown }));
    }

    private static ChatProviderRouter NewRouter() =>
        new(new IChatProvider[]
        {
            new LocalProvider(),
            new OpenAiProvider(),
            new AnthropicProvider(),
            new GroqProvider(),
            new GeminiProvider(),
            new MistralProvider(),
            new CerebrasProvider(),
        });

    private abstract class StubProvider : IChatProvider
    {
        public abstract ModelSource Source { get; }

        public bool IsReady(CatalogEntry entry) => true;

        public string? UnavailableReason(CatalogEntry entry) => null;

        public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct) => Task.CompletedTask;

        public Task UnloadAsync() => Task.CompletedTask;

        public async IAsyncEnumerable<string> GenerateAsync(
            CatalogEntry entry,
            string prompt,
            int maxTokens,
            IReadOnlyList<string> stops,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class LocalProvider : StubProvider { public override ModelSource Source => ModelSource.Local; }
    private sealed class OpenAiProvider : StubProvider { public override ModelSource Source => ModelSource.OpenAi; }
    private sealed class AnthropicProvider : StubProvider { public override ModelSource Source => ModelSource.Anthropic; }
    private sealed class GroqProvider : StubProvider { public override ModelSource Source => ModelSource.Groq; }
    private sealed class GeminiProvider : StubProvider { public override ModelSource Source => ModelSource.Gemini; }
    private sealed class MistralProvider : StubProvider { public override ModelSource Source => ModelSource.Mistral; }
    private sealed class CerebrasProvider : StubProvider { public override ModelSource Source => ModelSource.Cerebras; }
}
