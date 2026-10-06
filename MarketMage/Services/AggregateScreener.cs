using System;
using System.Collections.Generic;
using System.Linq;
using MarketMage.Models;

namespace MarketMage.Services;

public sealed record ScreenCandidate(uint ItemId, decimal PotentialProfit);

public static class AggregateScreener
{
    public static bool HasFreshSales(AggregateSnapshot? value, uint homeWorld, OpportunitySettings settings, DateTimeOffset now) =>
        value != null && value.HasSaleData && value.UploadTimes.TryGetValue(homeWorld, out var uploaded) &&
        OpportunityEngine.Fresh(uploaded, now, settings);

    public static bool HasFreshSource(AggregateSnapshot? value, OpportunitySettings settings, DateTimeOffset now) =>
        FreshSource(value, value?.Nq, settings, now) || FreshSource(value, value?.Hq, settings, now);

    // Screening prices are optimistic unit costs, never shopping plans. Only the detail engine can publish an opportunity.
    public static IReadOnlyList<ScreenCandidate> Rank(IReadOnlyList<ItemCatalogEntry> catalog,
        IReadOnlyDictionary<uint, IReadOnlyList<CraftingRecipe>> recipes,
        IReadOnlyDictionary<uint, AggregateSnapshot?> home, IReadOnlyDictionary<uint, AggregateSnapshot?> source,
        ScanScope scope, OpportunitySettings settings, DateTimeOffset now)
    {
        var ranked = new List<ScreenCandidate>();
        foreach (var item in catalog)
        {
            var sales = home.GetValueOrDefault(item.ItemId);
            if (!HasFreshSales(sales, scope.SaleWorldId, settings, now)) continue;
            decimal score = 0;
            foreach (var hq in item.CanBeHq ? new[] { false, true } : new[] { false })
            {
                var sell = hq ? sales!.Hq : sales!.Nq;
                if (sell.WorldMinimum is not > 0 || sell.WorldAverageSale is not > 0 || sell.WorldDailySales is not > 0 ||
                    !sell.WorldLastSale.HasValue || sell.WorldLastSale > now || now - sell.WorldLastSale > settings.SalesWindow) continue;
                var salePrice = Math.Min((decimal)sell.WorldMinimum.Value - 1, sell.WorldAverageSale.Value);
                if (salePrice <= 0) continue;
                var revenue = Math.Floor(salePrice * 0.95m);
                var demand = Math.Clamp(sell.WorldDailySales.Value * settings.SalesWindow.TotalDays, 1, 9999);
                var stock = source.GetValueOrDefault(item.ItemId);
                var buy = hq ? stock?.Hq : stock?.Nq;
                if (FreshSource(stock, buy, settings, now) && buy!.DcMinimumWorldId != scope.SaleWorldId)
                {
                    var cost = buy.DcMinimum!.Value * 1.05m;
                    var quantity = Math.Min((decimal)demand, Math.Floor(settings.Budget / cost));
                    score = Math.Max(score, Potential(revenue * quantity, cost * quantity, settings));
                }
                foreach (var recipe in recipes.GetValueOrDefault(item.ItemId) ?? [])
                {
                    if (recipe.AmountResult <= 0 || recipe.Ingredients.Count == 0 || hq && !recipe.CanHq) continue;
                    decimal craftCost = 0;
                    var complete = true;
                    foreach (var ingredient in recipe.Ingredients)
                    {
                        var price = source.GetValueOrDefault(ingredient.ItemId);
                        if (!FreshSource(price, price?.Nq, settings, now)) { complete = false; break; }
                        craftCost += price!.Nq.DcMinimum!.Value * 1.05m * ingredient.Quantity;
                    }
                    if (!complete) continue;
                    foreach (var count in Enumerable.Range(1, 10))
                    {
                        var quantity = recipe.AmountResult * count;
                        if (quantity > demand || !OpportunityEngine.FitsCraftDemand(quantity, sell.WorldDailySales, settings)) continue;
                        score = Math.Max(score, Potential(revenue * quantity, craftCost * count, settings));
                    }
                }
            }
            if (score > 0) ranked.Add(new(item.ItemId, score));
        }
        return ranked.OrderByDescending(c => c.PotentialProfit).ThenBy(c => c.ItemId).ToArray();
    }

    private static bool FreshSource(AggregateSnapshot? snapshot, AggregateQuality? price, OpportunitySettings settings, DateTimeOffset now) =>
        snapshot != null && price?.DcMinimum is > 0 && price.DcMinimumWorldId.HasValue &&
        snapshot.UploadTimes.TryGetValue(price.DcMinimumWorldId.Value, out var uploaded) && OpportunityEngine.Fresh(uploaded, now, settings);

    private static decimal Potential(decimal revenue, decimal cost, OpportunitySettings settings) =>
        cost > 0 && cost <= settings.Budget && revenue - cost >= settings.MinimumProfit &&
        (revenue - cost) / cost >= (decimal)settings.MinimumRoi ? revenue - cost : 0;
}
