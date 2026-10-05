using System;
using System.Collections.Generic;

namespace MarketMage.Models;

public sealed record AggregateQuality
{
    public long? WorldMinimum { get; init; }
    public long? DcMinimum { get; init; }
    public uint? DcMinimumWorldId { get; init; }
    public decimal? WorldAverageSale { get; init; }
    public double? WorldDailySales { get; init; }
    public DateTimeOffset? WorldLastSale { get; init; }
}

public sealed record AggregateSnapshot
{
    public uint ItemId { get; init; }
    public AggregateQuality Nq { get; init; } = new();
    public AggregateQuality Hq { get; init; } = new();
    public IReadOnlyDictionary<uint, DateTimeOffset> UploadTimes { get; init; } = new Dictionary<uint, DateTimeOffset>();
    public bool HasSaleData => Nq.WorldAverageSale is > 0 || Hq.WorldAverageSale is > 0;
}
