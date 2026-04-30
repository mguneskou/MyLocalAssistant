namespace MyLocalAssistant.Core.Models;

public sealed class InstalledModel
{
    public required CatalogEntry Catalog { get; init; }
    public required string PrimaryFilePath { get; init; }
    public required long SizeOnDisk { get; init; }
}
