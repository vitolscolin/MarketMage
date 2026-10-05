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
    public bool Crafted { get; set; }
    public bool Listed { get; set; }
    public bool Sold { get; set; }
    public string Notes { get; set; } = string.Empty;
}
