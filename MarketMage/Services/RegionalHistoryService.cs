using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMage.Services;

public sealed record RegionalHistory(uint ItemId, double UnitsPerDay, decimal MedianPrice, long SampleUnits,
    long SampleTransactions, DateTimeOffset RetrievedAt);

// Optional regional analytics, not an independent price feed. Never merged into local opportunity calculations.
public sealed class RegionalHistoryService : IDisposable
{
    private readonly HttpClient client;
    private readonly Dictionary<(uint Item, string World, bool Hq), RegionalHistory> cache = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    private readonly TimeSpan interval;
    public RegionalHistoryService(HttpClient? client = null, TimeSpan? interval = null)
    {
        this.client = client ?? new HttpClient();
        this.client.Timeout = TimeSpan.FromSeconds(45);
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("MarketMage/0.7");
        this.interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public async Task<RegionalHistory> GetAsync(uint item, string world, bool hq, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var key = (item, world, hq);
            if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.RetrievedAt < TimeSpan.FromMinutes(15)) return cached;
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            nextRequest = DateTimeOffset.UtcNow + interval;
            using var response = await client.PostAsJsonAsync("https://api.saddlebagexchange.com/api/ffxiv/v2/history",
                new { item_id = item, home_server = world, item_type = hq ? "hq_only" : "nq_only", initial_days = 7, end_days = 0 }, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var result = Parse(json, item, DateTimeOffset.UtcNow);
            cache[key] = result;
            return result;
        }
        finally { gate.Release(); }
    }

    public static RegionalHistory Parse(string json, uint item, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("itemID", out var id) ||
            !id.TryGetUInt32(out var returnedId) || returnedId != item) throw new JsonException("Unexpected history response.");
        var velocity = Number(root, "average_quantity_sold_per_day");
        var median = Number(root, "median_ppu");
        var units = Number(root, "total_quantity_sold");
        var transactions = Number(root, "total_purchase_amount");
        if (velocity < 0 || median < 0 || units < 0 || units > long.MaxValue || transactions < 0 || transactions > long.MaxValue)
            throw new JsonException("Invalid history metrics.");
        return new(item, (double)velocity, median, (long)units, (long)transactions, now);
    }
    private static decimal Number(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number : throw new JsonException("Missing history metric.");
    public void Dispose() => client.Dispose();
}
