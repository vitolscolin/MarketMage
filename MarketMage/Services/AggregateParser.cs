using System;
using System.Collections.Generic;
using System.Text.Json;
using MarketMage.Models;

namespace MarketMage.Services;

public static class AggregateParser
{
    public static IReadOnlyDictionary<uint, AggregateSnapshot?> Parse(string json, IReadOnlyCollection<uint> requested)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Aggregate response is missing its results array.");
        var rows = new Dictionary<uint, AggregateSnapshot?>();
        foreach (var id in requested) rows[id] = null;
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("itemId", out var idValue) || !idValue.TryGetUInt32(out var id) || !rows.ContainsKey(id)) continue;
            var uploads = new Dictionary<uint, DateTimeOffset>();
            if (item.TryGetProperty("worldUploadTimes", out var times) && times.ValueKind == JsonValueKind.Array)
                foreach (var time in times.EnumerateArray())
                    if (time.TryGetProperty("worldId", out var world) && world.TryGetUInt32(out var worldId) &&
                        time.TryGetProperty("timestamp", out var stamp) && Milliseconds(stamp) is { } uploaded)
                        uploads[worldId] = uploaded;
            rows[id] = new AggregateSnapshot
            {
                ItemId = id, Nq = Quality(item, "nq"), Hq = Quality(item, "hq"), UploadTimes = uploads,
            };
        }
        // Explicit API failures override any partial data for that item.
        if (root.TryGetProperty("failedItems", out var failures) && failures.ValueKind == JsonValueKind.Array)
            foreach (var failure in failures.EnumerateArray())
                if (failure.TryGetUInt32(out var id) && rows.ContainsKey(id)) rows[id] = null;
        return rows;
    }

    private static AggregateQuality Quality(JsonElement item, string quality)
    {
        if (!item.TryGetProperty(quality, out var value) || value.ValueKind != JsonValueKind.Object) return new();
        var dcWorld = Number(value, "minListing", "dc", "worldId");
        var velocity = Number(value, "dailySaleVelocity", "world", "quantity");
        return new AggregateQuality
        {
            WorldMinimum = Price(Number(value, "minListing", "world", "price")),
            DcMinimum = Price(Number(value, "minListing", "dc", "price")),
            DcMinimumWorldId = dcWorld is > 0 and <= uint.MaxValue ? (uint)dcWorld.Value : null,
            WorldAverageSale = Number(value, "averageSalePrice", "world", "price") is > 0 and <= int.MaxValue
                ? Number(value, "averageSalePrice", "world", "price") : null,
            WorldDailySales = velocity is >= 0 ? (double)velocity.Value : null,
            WorldLastSale = Nested(value, "recentPurchase", "world", "timestamp") is { } timestamp ? Milliseconds(timestamp) : null,
        };
    }
    private static long? Price(decimal? value) => value is > 0 and <= int.MaxValue ? (long)value.Value : null;
    private static decimal? Number(JsonElement item, params string[] path) =>
        Nested(item, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var number) ? number : null;
    private static JsonElement? Nested(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value;
    }
    private static DateTimeOffset? Milliseconds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var stamp) || stamp <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(stamp); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
