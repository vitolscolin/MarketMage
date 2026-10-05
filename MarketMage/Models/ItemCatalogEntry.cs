namespace MarketMage.Models;

public sealed class ItemCatalogEntry
{
    public bool CanBeHq { get; init; }
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
}
