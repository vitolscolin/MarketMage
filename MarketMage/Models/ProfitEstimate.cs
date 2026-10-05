using System;
using System.Collections.Generic;

namespace MarketMage.Models;

public sealed class ProfitEstimate
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long EstimatedSalePrice { get; init; }
    public bool IsCraftable { get; init; }
    public bool HasCompleteCost { get; init; }
    public long? EstimatedMaterialCost { get; init; }
    public long? DataCenterMaterialCost { get; init; }
    public int RecentSalesCount { get; init; }
    public DateTimeOffset? LastSaleTime { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public int RecipeYield { get; init; } = 1;
    public IReadOnlyList<string> MissingIngredientNames { get; init; } = [];
    public IReadOnlyList<IngredientCostEstimate> IngredientCosts { get; init; } = [];
    public IReadOnlyList<IngredientCostEstimate> DataCenterIngredientCosts { get; init; } = [];
    public long? AdjustedRevenue => EstimatedSalePrice <= 0 ? null : (long)Math.Floor(EstimatedSalePrice * 0.95m);
    public long? Profit => HasCompleteCost && AdjustedRevenue.HasValue && EstimatedMaterialCost.HasValue
        ? AdjustedRevenue.Value - EstimatedMaterialCost.Value : null;
    public long? DataCenterProfit => AdjustedRevenue.HasValue && DataCenterMaterialCost.HasValue
        ? AdjustedRevenue.Value - DataCenterMaterialCost.Value : null;
    public double? Roi => Profit.HasValue && EstimatedMaterialCost is > 0
        ? (double)Profit.Value / EstimatedMaterialCost.Value : null;
}
