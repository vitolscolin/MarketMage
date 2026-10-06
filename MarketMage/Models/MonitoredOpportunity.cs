using System;
using System.Collections.Generic;

namespace MarketMage.Models;

public sealed class MonitoredOpportunity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceDataCenter { get; set; } = string.Empty;
    public GilOpportunity SavedPlan { get; set; } = new();
    public GilOpportunity? LatestPlan { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public List<int> PurchasedSteps { get; set; } = [];
    public int? CommittedQuantity { get; set; }
    public int CraftedQuantity { get; set; }
    public int ListedQuantity { get; set; }
    public int SoldQuantity { get; set; }
    // Listed units are part of produced/committed units, not an additional batch.
    public int UnsoldQuantity => Sold ? 0 : Math.Max(0,
        Math.Max(CommittedQuantity ?? SavedPlan.OutputQuantity, Math.Max(CraftedQuantity, ListedQuantity)) - SoldQuantity);
    public bool Crafted { get; set; }
    public bool Listed { get; set; }
    public bool Sold { get; set; }
    public string Notes { get; set; } = string.Empty;
}
