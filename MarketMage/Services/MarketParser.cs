using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MarketMage.Models;

namespace MarketMage.Services;

public static class MarketParser
{
    public static IReadOnlyList<MarketPriceSnapshot> Parse(string json, IReadOnlyCollection<uint> itemIds, string scope, bool hq)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new List<MarketPriceSnapshot>();
        var multi = root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object;
        foreach (var id in itemIds)
        {
            if (multi)
                result.Add(items.TryGetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var item)
                    ? ParseItem(id, item, scope, hq) : new MarketPriceSnapshot { ItemId = id });
            else if (itemIds.Count == 1 && Number(root, "itemID") == id)
                result.Add(ParseItem(id, root, scope, hq));
            else
                result.Add(new MarketPriceSnapshot { ItemId = id });
        }
        return result;
    }

    private static MarketPriceSnapshot ParseItem(uint id, JsonElement item, string scope, bool hq)
    {
        var prices = new List<long>();
        var sales = new List<RecentSale>();
        DateTimeOffset? latest = null;
        if (item.TryGetProperty("recentHistory", out var history) && history.ValueKind == JsonValueKind.Array)
            foreach (var entry in history.EnumerateArray())
            {
                var price = Number(entry, "pricePerUnit");
                if (!MatchesQuality(entry, hq) || price <= 0 || price > int.MaxValue) continue;
                prices.Add(price);
                var time = Timestamp(Number(entry, "timestamp"));
                var quantity = Number(entry, "quantity");
                if (time.HasValue && quantity > 0 && quantity <= int.MaxValue)
                    sales.Add(new RecentSale(price, (int)quantity, time.Value));
                if (time.HasValue && (!latest.HasValue || time > latest)) latest = time;
            }
        var listings = new List<MarketListing>();
        if (item.TryGetProperty("listings", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var entry in rows.EnumerateArray())
            {
                var price = Number(entry, "pricePerUnit");
                var quantity = Number(entry, "quantity");
                if (!MatchesQuality(entry, hq) || price <= 0 || price > int.MaxValue || quantity <= 0 || quantity > int.MaxValue) continue;
                var world = entry.TryGetProperty("worldName", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()! : scope;
                listings.Add(new MarketListing { PricePerUnit = price, Quantity = (int)quantity, World = world,
                    ReviewedAt = Timestamp(Number(entry, "lastReviewTime")) });
            }
        prices.Sort();
        var median = prices.Count == 0 ? 0 : prices.Count % 2 == 1 ? prices[prices.Count / 2]
            : (long)Math.Floor(((decimal)prices[prices.Count / 2 - 1] + prices[prices.Count / 2]) / 2);
        return new MarketPriceSnapshot { ItemId = id, MedianRecentSalePrice = median, RecentSalesCount = prices.Count,
            LastSaleTime = latest, UploadedAt = Timestamp(Number(item, "lastUploadTime"), true), Listings = listings, Sales = sales };
    }

    private static bool MatchesQuality(JsonElement entry, bool hq) =>
        entry.TryGetProperty("hq", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean() == hq;
    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static DateTimeOffset? Timestamp(long value, bool milliseconds = false)
    {
        if (value <= 0) return null;
        try { return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
