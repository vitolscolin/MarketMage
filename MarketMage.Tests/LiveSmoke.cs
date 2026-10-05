using MarketMage.Services;

internal static class LiveSmoke
{
    public static async Task<int> RunAsync()
    {
        using var client = new UniversalisService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var recent = await client.GetRecentItemsAsync("Aether", timeout.Token);
        if (recent.Count == 0) throw new Exception("Expected recently updated Aether markets.");
        var screeningIds = recent.Take(100).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var aggregates = await client.GetAggregatesAsync("Cactuar", screeningIds, timeout.Token);
        timer.Stop();
        if (aggregates.Count != screeningIds.Length || !aggregates.Values.Any(s => s?.Nq.WorldAverageSale is > 0 || s?.Hq.WorldAverageSale is > 0))
            throw new Exception("Expected aggregate results with usable price data.");
        Console.WriteLine($"PASS aggregate API: {aggregates.Count} items screened in {timer.Elapsed.TotalSeconds:F2}s; {aggregates.Values.Count(s => s != null)} resolved. This is one request, not a full-catalog benchmark.");
        var home = await client.GetSnapshotsAsync("Cactuar", [2u, 3u], false, true, timeout.Token, true);
        var dc = await client.GetSnapshotsAsync("Aether", [2u, 3u], false, true, timeout.Token);
        if (home.Count != 2 || dc.Count != 2 || home.Any(s => s.Sales.Count == 0 || s.Listings.Count == 0) ||
            dc.Any(s => s.Listings.Count == 0 || s.Listings.Any(l => l.World == "Aether")))
            throw new Exception("Live response did not contain the expected sale, quantity, and source-world fields.");
        Console.WriteLine($"PASS public API smoke: {recent.Count} recent item IDs; home history/listings and DC source worlds parsed for two items.");
        return 0;
    }
}
