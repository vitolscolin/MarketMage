using System;
using System.Collections.Generic;
using System.Linq;
using MarketMage.Models;

namespace MarketMage.Services;

public static class OpportunityEngine
{
    public static IReadOnlyList<GilOpportunity> Evaluate(ItemCatalogEntry item, bool hq, string saleWorld,
        MarketPriceSnapshot sale, MarketPriceSnapshot? dcOutput, IReadOnlyList<CraftingRecipe> recipes,
        IReadOnlyDictionary<uint, MarketPriceSnapshot> ingredients, OpportunitySettings settings, DateTimeOffset now, double? dailySales = null)
    {
        if (hq && !item.CanBeHq || !Fresh(sale.UploadedAt, now, settings)) return [];
        var history = sale.Sales.Where(s => s.Price > 0 && s.Quantity > 0 && s.SoldAt <= now && s.SoldAt >= now - settings.SalesWindow).ToList();
        if (history.Count < settings.MinimumSales) return [];
        // A stale/unknown best ask must not be silently skipped to justify a higher selling price.
        var cheapest = sale.Listings.Where(l => l.PricePerUnit > 0 && l.Quantity > 0).OrderBy(l => l.PricePerUnit).FirstOrDefault();
        if (cheapest == null || !Fresh(cheapest.ReviewedAt, now, settings)) return [];
        var prices = history.Select(s => s.Price).Order().ToArray();
        var median = prices.Length % 2 == 1 ? prices[prices.Length / 2]
            : (long)Math.Floor(((decimal)prices[prices.Length / 2 - 1] + prices[prices.Length / 2]) / 2);
        var salePrice = Math.Min(median, Math.Max(1, cheapest.PricePerUnit - 1));
        var unitRevenue = (long)Math.Floor(salePrice * 0.95m);
        var units = history.Sum(s => (long)s.Quantity);
        var lastSale = history.Max(s => s.SoldAt);
        var baseRow = new GilOpportunity
        {
            CommittedUnits = settings.CommittedUnits.GetValueOrDefault((item.ItemId, hq)),
            ItemId = item.ItemId, ItemName = item.Name, HighQuality = hq, SaleWorld = saleWorld,
            UsesSampledDemand = !dailySales.HasValue, EstimatedDailySales = dailySales ?? units / settings.SalesWindow.TotalDays,
            SalePrice = salePrice, SampleSales = history.Count, SampleUnits = units, LastSale = lastSale,
            CheckedAt = now, OldestMarketData = new[] { sale.UploadedAt!.Value, cheapest.ReviewedAt!.Value }.Min(),
        };
        var results = new List<GilOpportunity>();
        var comparisons = new List<BatchComparison>();
        foreach (var listing in dcOutput?.Listings ?? [])
        {
            if (listing.World == saleWorld || listing.Quantity <= 0 || listing.Quantity > units || !Fresh(listing.ReviewedAt, now, settings)) continue;
            var purchase = Step(item.ItemId, item.Name, listing);
            var row = baseRow with
            {
                Kind = OpportunityKind.Resell, OutputQuantity = listing.Quantity,
                Outlay = purchase.CostWithTax, Revenue = unitRevenue * listing.Quantity,
                Purchases = [purchase], OldestMarketData = Min(baseRow.OldestMarketData, purchase.ReviewedAt),
            };
            if (Eligible(row, settings)) results.Add(row);
        }
        foreach (var recipe in recipes.Where(r => !hq || r.CanHq))
        {
            if (recipe.AmountResult <= 0 || recipe.Ingredients.Count == 0) continue;
            foreach (var count in Enumerable.Range(1, 10))
            {
                var output = recipe.AmountResult * count;

                var purchases = new List<PurchaseStep>();
                var complete = true;
                var requirements = recipe.Ingredients.GroupBy(i => i.ItemId).Select(g => new RecipeIngredient
                { ItemId = g.Key, ItemName = g.First().ItemName, Quantity = g.Sum(i => i.Quantity) });
                foreach (var ingredient in requirements)
                {
                    if (!ingredients.TryGetValue(ingredient.ItemId, out var snapshot)) { complete = false; break; }
                    var basket = BuyWholeStacks(ingredient, count, snapshot.Listings, settings, now);
                    if (basket == null) { complete = false; break; }
                    purchases.AddRange(basket);
                }
                if (!complete)
                {
                    comparisons.Add(new(recipe.RecipeId, count, output, null, null, null,
                        SaleTiming.Days((long)output + baseRow.CommittedUnits, baseRow.EstimatedDailySales, settings.ExpectedMarketSharePercent),
                        "Missing stock/data or over budget"));
                    continue;
                }
                var row = baseRow with
                {
                    Kind = OpportunityKind.Craft, OutputQuantity = output, CraftCount = count,
                    RecipeId = recipe.RecipeId, CraftJob = recipe.CraftJob, CraftLevel = recipe.CraftLevel,
                    Outlay = purchases.Sum(p => p.CostWithTax), Revenue = unitRevenue * output, Purchases = purchases,
                    OldestMarketData = Min(baseRow.OldestMarketData, purchases.Min(p => p.ReviewedAt)),
                };
                var reason = output > units ? "Exceeds sampled sales" :
                    !FitsCraftDemand(output, row.EstimatedDailySales, settings, row.CommittedUnits) ? "Exceeds remaining demand" :
                    row.Outlay > settings.Budget ? "Over budget" :
                    row.Profit <= 0 || row.Profit < settings.MinimumProfit || row.Roi < settings.MinimumRoi ? "Below profit/ROI filter" : "Qualifies";
                comparisons.Add(new(recipe.RecipeId, count, output, row.Outlay, row.Profit, row.Roi,
                    SaleTiming.Days((long)output + row.CommittedUnits, row.EstimatedDailySales, settings.ExpectedMarketSharePercent), reason));
                if (reason == "Qualifies" && Eligible(row, settings)) results.Add(row);
            }
        }
        return results.GroupBy(r => r.Kind).Select(g => g.OrderByDescending(r => r.Kind == OpportunityKind.Craft ? DailyProfit(r, settings) : r.Profit)
            .ThenBy(r => r.OutputQuantity).ThenByDescending(r => r.Roi).First())
            .Select(r => r with { BatchComparisons = comparisons }).ToList();
    }

    // Exact minimum full-stack outlay within the sampled listings, on one world per ingredient.
    public static IReadOnlyList<PurchaseStep>? BuyWholeStacks(RecipeIngredient ingredient, int crafts,
        IReadOnlyList<MarketListing> listings, OpportunitySettings settings, DateTimeOffset now)
    {
        var required = (long)ingredient.Quantity * crafts;
        if (required <= 0 || required > 9999) return null;
        List<PurchaseStep>? best = null;
        long bestCost = long.MaxValue;
        foreach (var world in listings.Where(l => l.PricePerUnit > 0 && l.Quantity > 0 && Fresh(l.ReviewedAt, now, settings)).GroupBy(l => l.World))
        {
            var states = new Dictionary<int, (long Cost, List<PurchaseStep> Steps)> { [0] = (0, []) };
            foreach (var listing in world)
            {
                var step = Step(ingredient.ItemId, ingredient.ItemName, listing);
                foreach (var state in states.ToArray())
                {
                    if (state.Key == required) continue;
                    var quantity = (int)Math.Min(required, (long)state.Key + listing.Quantity);
                    var cost = state.Value.Cost + step.CostWithTax;
                    if (cost > settings.Budget || cost >= bestCost) continue;
                    if (states.TryGetValue(quantity, out var previous) && previous.Cost <= cost) continue;
                    states[quantity] = (cost, [.. state.Value.Steps, step]);
                }
            }
            if (states.TryGetValue((int)required, out var completed) && completed.Cost < bestCost)
            { bestCost = completed.Cost; best = completed.Steps; }
        }
        return best;
    }

    public static bool FitsCraftDemand(int quantity, double? dailySales, OpportunitySettings settings, int committed = 0) =>
        SaleTiming.Days((long)quantity + committed, dailySales, settings.ExpectedMarketSharePercent) is double days &&
        days <= settings.MaximumCraftSaleDays;

    // A one-day floor avoids implying repeatable intraday turnovers from a short history sample.
    public static double DailyProfit(GilOpportunity row, OpportunitySettings settings) =>
        SaleTiming.Days((long)row.OutputQuantity + row.CommittedUnits, row.EstimatedDailySales, settings.ExpectedMarketSharePercent) is double days
            ? row.Profit / Math.Max(1, days) : 0;

    public static bool Eligible(GilOpportunity row, OpportunitySettings settings) =>
        (row.Kind != OpportunityKind.Craft || FitsCraftDemand(row.OutputQuantity, row.EstimatedDailySales, settings, settings.CommittedUnits.GetValueOrDefault((row.ItemId, row.HighQuality)))) &&
        row.Outlay > 0 && row.Outlay <= settings.Budget && row.Profit > 0 &&
        row.Profit >= settings.MinimumProfit && row.Roi >= settings.MinimumRoi;

    public static bool Fresh(DateTimeOffset? time, DateTimeOffset now, OpportunitySettings settings) =>
        time.HasValue && time.Value <= now && now - time.Value <= settings.MaximumDataAge;

    private static PurchaseStep Step(uint id, string name, MarketListing listing) => new(id, name, listing.World,
        listing.Quantity, listing.PricePerUnit, (long)Math.Ceiling(listing.PricePerUnit * (decimal)listing.Quantity * 1.05m), listing.ReviewedAt!.Value);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
