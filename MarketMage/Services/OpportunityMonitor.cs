using System;
using System.Collections.Generic;
using System.Linq;
using MarketMage.Models;

namespace MarketMage.Services;

public static class OpportunityMonitor
{
    public const int MaximumActive = 50;
    public static bool Matches(MonitoredOpportunity entry, GilOpportunity row, string dc) =>
        entry.SourceDataCenter == dc && entry.SavedPlan.SaleWorld == row.SaleWorld && entry.SavedPlan.Key == row.Key;

    public static bool Add(IList<MonitoredOpportunity> entries, GilOpportunity row, string dc, DateTimeOffset now)
    {
        if (entries.Any(e => !e.Sold && Matches(e, row, dc)) || entries.Count(e => !e.Sold) >= MaximumActive) return false;
        entries.Add(new() { SourceDataCenter = dc, SavedPlan = row, LatestPlan = row, SavedAt = now, LastCheckedAt = now });
        return true;
    }

    public static IEnumerable<uint> Priority(IEnumerable<MonitoredOpportunity> entries, ScanScope scope) =>
        entries.Where(e => !e.Sold && e.SourceDataCenter == scope.DataCenter && e.SavedPlan.SaleWorld == scope.SaleWorld)
            .Select(e => e.SavedPlan.ItemId).Distinct();

    public static bool Apply(IEnumerable<MonitoredOpportunity> entries, ScanScope scope, ScanUpdate update, DateTimeOffset now)
    {
        if (update.Phase != ScanPhase.Validating || update.ItemIds.Count == 0) return false;
        var ids = update.ItemIds.ToHashSet();
        var changed = false;
        foreach (var entry in entries.Where(e => !e.Sold && e.SourceDataCenter == scope.DataCenter &&
            e.SavedPlan.SaleWorld == scope.SaleWorld && ids.Contains(e.SavedPlan.ItemId)))
        {
            entry.LatestPlan = update.Opportunities.FirstOrDefault(r => Matches(entry, r, scope.DataCenter));
            entry.LastCheckedAt = now;
            changed = true;
        }
        return changed;
    }

    public static string Status(MonitoredOpportunity entry, DateTimeOffset now)
    {
        if (entry.Sold) return "Sold (manual)";
        if (!entry.LastCheckedAt.HasValue || now - entry.LastCheckedAt.Value > TimeSpan.FromMinutes(15)) return "Needs recheck";
        return entry.LatestPlan == null ? "Not qualifying / data unavailable" : "Qualifies in latest check";
    }
}
