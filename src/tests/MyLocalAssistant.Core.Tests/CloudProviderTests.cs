using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;

namespace MyLocalAssistant.Core.Tests;

public class CloudProviderTests
{
    [Fact]
    public void SecretProtector_RoundTrips_NonEmptyString()
    {
        var original = "sk-test-1234567890ABCDEF";
        var blob = SecretProtector.Protect(original);

        // Encrypted blob must not contain the plaintext (smoke check that we actually transformed it).
        Assert.DoesNotContain(original, blob, StringComparison.Ordinal);
        Assert.NotEqual(original, blob);

        var roundTripped = SecretProtector.Unprotect(blob);
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void SecretProtector_Unprotect_NullOrEmpty_Returns_Null()
    {
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Null(SecretProtector.Unprotect(""));
    }

    [Fact]
    public void ChatProviderRouter_Selects_By_Source()
    {
        var local = new FakeProvider(ModelSource.Local);
        var openAi = new FakeProvider(ModelSource.OpenAi);
        var anthropic = new FakeProvider(ModelSource.Anthropic);
        var router = new ChatProviderRouter(new IChatProvider[] { local, openAi, anthropic });

        Assert.Same(local, router.Get(new CatalogEntry { Id = "x", Source = ModelSource.Local }));
        Assert.Same(openAi, router.Get(new CatalogEntry { Id = "y", Source = ModelSource.OpenAi }));
        Assert.Same(anthropic, router.Get(new CatalogEntry { Id = "z", Source = ModelSource.Anthropic }));
    }

    [Fact]
    public void ChatProviderRouter_Throws_When_Provider_Missing()
    {
        var router = new ChatProviderRouter(new IChatProvider[] { new FakeProvider(ModelSource.Local) });
        Assert.Throws<InvalidOperationException>(() =>
            router.Get(new CatalogEntry { Id = "y", Source = ModelSource.OpenAi }));
    }

    [Fact]
    public void CatalogEntry_IsCloud_Reflects_Source()
    {
        Assert.False(new CatalogEntry { Source = ModelSource.Local }.IsCloud);
        Assert.True(new CatalogEntry { Source = ModelSource.OpenAi }.IsCloud);
        Assert.True(new CatalogEntry { Source = ModelSource.Anthropic }.IsCloud);
    }

    private sealed class FakeProvider : IChatProvider
    {
        public FakeProvider(ModelSource source) { Source = source; }
        public ModelSource Source { get; }
        public bool IsReady(CatalogEntry entry) => true;
        public string? UnavailableReason(CatalogEntry entry) => null;
        public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct) => Task.CompletedTask;
        public Task UnloadAsync() => Task.CompletedTask;
        public async IAsyncEnumerable<string> GenerateAsync(
            CatalogEntry entry, string prompt, int maxTokens, IReadOnlyList<string> stops,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
