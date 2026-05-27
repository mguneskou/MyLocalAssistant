using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.Core.Tests;

public class ModelCatalogServiceTests
{
    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "model-catalog.json");

    private static ModelCatalogService Load()
    {
        using var s = File.OpenRead(CatalogPath);
        return ModelCatalogService.LoadFromStream(s);
    }

    [Fact]
    public void Catalog_FileExists()
    {
        Assert.True(File.Exists(CatalogPath), $"Expected catalog at {CatalogPath}");
    }

    [Fact]
    public void Catalog_Parses_AndHasEntries()
    {
        var svc = Load();
        Assert.NotEmpty(svc.Entries);
    }

    [Fact]
    public void Catalog_NoDuplicateIds()
    {
        var svc = Load();
        var dup = svc.Entries.GroupBy(e => e.Id).FirstOrDefault(g => g.Count() > 1);
        Assert.Null(dup);
    }

    [Fact]
    public void Catalog_AllEntries_HaveRequiredFields()
    {
        var svc = Load();
        foreach (var e in svc.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id), $"missing id: {e.DisplayName}");
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayName), $"missing displayName: {e.Id}");
            Assert.False(string.IsNullOrWhiteSpace(e.License), $"missing license: {e.Id}");
            if (e.IsCloud)
            {
                // Cloud entries are pure remote endpoints — they have no downloadable files.
                Assert.False(string.IsNullOrWhiteSpace(e.RemoteModel), $"cloud entry missing remoteModel: {e.Id}");
                Assert.Empty(e.Files);
                continue;
            }
            Assert.NotEmpty(e.Files);
            foreach (var f in e.Files)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.FileName), $"missing fileName in {e.Id}");
                Assert.False(string.IsNullOrWhiteSpace(f.Url), $"missing url in {e.Id}");
                Assert.StartsWith("https://", f.Url);
            }
        }
    }

    [Fact]
    public void Catalog_AllTiers_Represented()
    {
        var svc = Load();
        var tiers = svc.Entries.Select(e => e.Tier).Distinct().ToHashSet();
        Assert.Contains(ModelTier.Lightweight, tiers);
        Assert.Contains(ModelTier.Mid, tiers);
        Assert.Contains(ModelTier.Heavy, tiers);
        Assert.Contains(ModelTier.Workstation, tiers);
    }
}
