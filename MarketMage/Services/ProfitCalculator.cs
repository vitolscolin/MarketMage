using System;
using System.Collections.Generic;
using System.Linq;
using MarketMage.Models;

namespace MarketMage.Services;

public static class ProfitCalculator
{
    public static ProfitEstimate Build(MarketPriceSnapshot sale, string name, CraftingRecipe? recipe,
        IReadOnlyDictionary<uint, MarketPriceSnapshot> local, IReadOnlyDictionary<uint, MarketPriceSnapshot> dataCenter)
    {
        var ingredients = recipe?.Ingredients.Select(i => Cost(i, local)).ToList() ?? [];
        var dcIngredients = recipe?.Ingredients.Select(i => Cost(i, dataCenter)).ToList() ?? [];
        var complete = recipe != null && ingredients.All(i => i.HasPrice);
        var dcComplete = recipe != null && dcIngredients.All(i => i.HasPrice);
        var yield = Math.Max(1, recipe?.AmountResult ?? 1);
        return new ProfitEstimate { ItemId = sale.ItemId, ItemName = name, EstimatedSalePrice = sale.MedianRecentSalePrice,
            RecentSalesCount = sale.RecentSalesCount, LastSaleTime = sale.LastSaleTime, UploadedAt = sale.UploadedAt,
            IsCraftable = recipe != null, HasCompleteCost = complete, RecipeYield = yield,
            IngredientCosts = ingredients, DataCenterIngredientCosts = dcIngredients,
            MissingIngredientNames = ingredients.Where(i => !i.HasPrice).Select(i => i.ItemName).ToList(),
            EstimatedMaterialCost = complete ? (long)Math.Ceiling(ingredients.Sum(i => i.TotalPrice) / (decimal)yield) : null,
            DataCenterMaterialCost = dcComplete ? (long)Math.Ceiling(dcIngredients.Sum(i => i.TotalPrice) / (decimal)yield) : null };
    }

    public static IngredientCostEstimate Cost(RecipeIngredient ingredient, IReadOnlyDictionary<uint, MarketPriceSnapshot> prices)
    {
        if (!prices.TryGetValue(ingredient.ItemId, out var snapshot)) return Missing(ingredient);
        // Choose one world per ingredient with enough sampled stock; never imply a single-world shopping basket.
        var candidates = snapshot.Listings.GroupBy(l => l.World).Select(group =>
        {
            var remaining = ingredient.Quantity;
            long total = 0;
            DateTimeOffset? reviewed = null;
            var unknownTime = false;
            foreach (var listing in group.OrderBy(l => l.PricePerUnit))
            {
                var take = Math.Min(remaining, listing.Quantity);
                if (take <= 0) break;
                total += take * listing.PricePerUnit;
                remaining -= take;
                unknownTime |= !listing.ReviewedAt.HasValue;
                if (listing.ReviewedAt.HasValue && (!reviewed.HasValue || listing.ReviewedAt < reviewed)) reviewed = listing.ReviewedAt;
            }
            return new IngredientCostEstimate { ItemId = ingredient.ItemId, ItemName = ingredient.ItemName,
                Quantity = ingredient.Quantity, HasPrice = remaining == 0, TotalPrice = total, SourceWorld = group.Key,
                AvailableQuantity = group.Sum(l => (long)l.Quantity), ReviewedAt = unknownTime ? null : reviewed };
        }).Where(c => c.HasPrice).OrderBy(c => c.TotalPrice).ThenBy(c => c.SourceWorld).ToList();
        return candidates.FirstOrDefault() ?? Missing(ingredient, snapshot.Listings.Sum(l => (long)l.Quantity));
    }
    private static IngredientCostEstimate Missing(RecipeIngredient ingredient, long available = 0) => new()
    { ItemId = ingredient.ItemId, ItemName = ingredient.ItemName, Quantity = ingredient.Quantity, AvailableQuantity = available };
}
