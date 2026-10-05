using System;

namespace MarketMage.Models;

public sealed class IngredientCostEstimate
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice => Quantity > 0 ? TotalPrice / (decimal)Quantity : 0;
    public long TotalPrice { get; init; }
    public bool HasPrice { get; init; }
    public string SourceWorld { get; init; } = string.Empty;
    public long AvailableQuantity { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
}
