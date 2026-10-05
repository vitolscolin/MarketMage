using System;
using System.Collections.Generic;
using System.Linq;
using MarketMage.Models;

namespace MarketMage.Services;

// UI-owned: replace all findings for checked items, so obsolete profits never survive a rescan.
public sealed class OpportunityBook
{
    private readonly Dictionary<string, GilOpportunity> rows = [];
    public void Clear() => rows.Clear();
    public void Apply(ScanUpdate update)
    {
        var checkedIds = update.ItemIds.ToHashSet();
        foreach (var key in rows.Where(pair => checkedIds.Contains(pair.Value.ItemId)).Select(pair => pair.Key).ToArray()) rows.Remove(key);
        foreach (var row in update.Opportunities) rows[row.Key] = row;
    }
    public IReadOnlyList<GilOpportunity> Current(DateTimeOffset now, OpportunitySettings settings)
    {
        foreach (var key in rows.Where(pair => now - pair.Value.CheckedAt > TimeSpan.FromMinutes(15) ||
            !OpportunityEngine.Fresh(pair.Value.OldestMarketData, now, settings)).Select(pair => pair.Key).ToArray()) rows.Remove(key);
        return rows.Values.Where(row => OpportunityEngine.Eligible(row, settings))
            .OrderByDescending(row => row.Profit).ThenByDescending(row => row.SampleSales).ToList();
    }
}
