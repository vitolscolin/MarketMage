using System;
using System.Collections.Generic;

namespace MarketMage.Models;

public sealed class MarketPriceSnapshot
{
    public uint ItemId { get; init; }
    public long MedianRecentSalePrice { get; init; }
    public int RecentSalesCount { get; init; }
    public DateTimeOffset? LastSaleTime { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public IReadOnlyList<RecentSale> Sales { get; init; } = [];
    public IReadOnlyList<MarketListing> Listings { get; init; } = [];
}

public sealed class MarketListing
{
    public long PricePerUnit { get; init; }
    public int Quantity { get; init; }
    public string World { get; init; } = string.Empty;
    public DateTimeOffset? ReviewedAt { get; init; }
}

public sealed record RecentSale(long Price, int Quantity, DateTimeOffset SoldAt);
