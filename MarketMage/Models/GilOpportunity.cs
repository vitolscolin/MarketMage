using System;
using System.Collections.Generic;

namespace MarketMage.Models;

public enum OpportunityKind { Craft, Resell }

public sealed record OpportunitySettings
{
    public IReadOnlyDictionary<(uint Item, bool Hq), int> CommittedUnits { get; init; } = new Dictionary<(uint, bool), int>();
    public int ExpectedMarketSharePercent { get; init; } = 25;
    public int MaximumCraftSaleDays { get; init; } = 3;
    public long Budget { get; init; } = 100_000;
    public long MinimumProfit { get; init; } = 1_000;
    public double MinimumRoi { get; init; } = 0.10;
    public int MinimumSales { get; init; } = 3;
    public TimeSpan MaximumDataAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan SalesWindow { get; init; } = TimeSpan.FromDays(7);
}

public sealed record PurchaseStep(uint ItemId, string ItemName, string World, int Quantity,
    long UnitPrice, long CostWithTax, DateTimeOffset ReviewedAt);

public sealed record BatchComparison(uint RecipeId, int Crafts, int Quantity, long? Cost, long? Profit,
    double? Roi, double? Days, string Status);

public sealed record GilOpportunity
{
    public IReadOnlyList<BatchComparison> BatchComparisons { get; init; } = [];
    public int CommittedUnits { get; init; }
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool HighQuality { get; init; }
    public OpportunityKind Kind { get; init; }
    public string Key => $"{ItemId}:{HighQuality}:{Kind}";
    public string SaleWorld { get; init; } = string.Empty;
    public int OutputQuantity { get; init; }
    public int CraftCount { get; init; }
    public uint RecipeId { get; init; }
    public string CraftJob { get; init; } = string.Empty;
    public int CraftLevel { get; init; }
    public long SalePrice { get; init; }
    public long Revenue { get; init; }
    public long Outlay { get; init; }
    public long Profit => Revenue - Outlay;
    public double Roi => Outlay > 0 ? (double)Profit / Outlay : 0;
    public bool UsesSampledDemand { get; init; }
    public double? EstimatedDailySales { get; init; }
    public int SampleSales { get; init; }
    public long SampleUnits { get; init; }
    public DateTimeOffset LastSale { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public DateTimeOffset OldestMarketData { get; init; }
    public IReadOnlyList<PurchaseStep> Purchases { get; init; } = [];
}
