using System.Net;
using MarketMage.Models;
using MarketMage.Services;

internal static class AggregateChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpportunitySettings Settings = new() { MinimumProfit = 1, MinimumRoi = 0 };
    private const string Payload = """
        {"results":[{"itemId":2,"nq":{"minListing":{"world":{"price":201},"dc":{"price":100,"worldId":2}},
        "averageSalePrice":{"world":{"price":199.5}},"dailySaleVelocity":{"world":{"quantity":12.25}},
        "recentPurchase":{"world":{"price":200,"timestamp":1791201199000}}},"hq":{},
        "worldUploadTimes":[{"worldId":1,"timestamp":1791210336413},{"worldId":2,"timestamp":1791210336413}]}],"failedItems":[3]}
        """;
    private static AggregateSnapshot Snapshot(uint id, DateTimeOffset now) => new()
    {
        ItemId = id,
        Nq = new() { WorldMinimum = 201, WorldAverageSale = 200, DcMinimum = 100, DcMinimumWorldId = 2,
            WorldDailySales = 10, WorldLastSale = now.AddHours(-1) },
        UploadTimes = new Dictionary<uint, DateTimeOffset> { [1] = now, [2] = now },
    };
    private static ScanScope Scope => new("Home", "LocalDC", new HashSet<string> { "Home", "Other" }, 1, "LocalDC");
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        var tests = new List<(string, Func<Task>)>();
        void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
        void Async(string name, Func<Task> run) => tests.Add((name, run));
        Test("Aggregate parser reads decimal prices, velocity, and millisecond timestamps", () =>
        {
            var row = AggregateParser.Parse(Payload, [2u, 3u])[2]!;
            Equal(199.5m, row.Nq.WorldAverageSale); Equal(12.25, row.Nq.WorldDailySales);
            Equal(1791201199000L, row.Nq.WorldLastSale!.Value.ToUnixTimeMilliseconds());
            Equal(1791210336413L, row.UploadTimes[1].ToUnixTimeMilliseconds());
            Equal(2u, row.Nq.DcMinimumWorldId);
        });
        Test("Aggregate absent HQ and failed items stay unknown, never zero-priced", () =>
        {
            var result = AggregateParser.Parse(Payload, [2u, 3u, 4u]);
            Equal<long?>(null, result[2]!.Hq.WorldMinimum);
            Equal<AggregateSnapshot?>(null, result[3]); Equal<AggregateSnapshot?>(null, result[4]);
        });
        Test("Malformed aggregate envelope raises an error instead of claiming coverage", () =>
        {
            try { AggregateParser.Parse("{}", [2u]); throw new Exception("Expected malformed response error"); }
            catch (System.Text.Json.JsonException) { }
        });
        Test("Aggregate null values and invalid timestamps remain unknown", () =>
        {
            var row = AggregateParser.Parse(Payload.Replace("199.5", "null").Replace("1791201199000", "9223372036854775807"), [2u])[2]!;
            Equal<decimal?>(null, row.Nq.WorldAverageSale); Equal<DateTimeOffset?>(null, row.Nq.WorldLastSale);
        });
        Test("Aggregate cache isolates scopes and expires snapshots", () =>
        {
            var cache = new AggregateCache(); cache.Put("Home", 2, Snapshot(2, Now), Now);
            True(cache.TryGet("Home", 2, Now.AddMinutes(9), out _));
            Equal(false, cache.TryGet("OtherDC", 2, Now, out _));
            Equal(false, cache.TryGet("Home", 2, Now.AddMinutes(11), out _));
        });
        Test("Unavailable aggregate items have a shorter negative-cache lifetime", () =>
        {
            var cache = new AggregateCache(); cache.Put("Home", 2, null, Now);
            True(cache.TryGet("Home", 2, Now.AddMinutes(1), out var value)); Equal<AggregateSnapshot?>(null, value);
            Equal(false, cache.TryGet("Home", 2, Now.AddMinutes(3), out _));
        });
        Test("Aggregate screening finds unit-margin candidates but publishes no shopping plan", () =>
        {
            var values = new Dictionary<uint, AggregateSnapshot?> { [2] = Snapshot(2, Now) };
            var ranked = AggregateScreener.Rank([new() { ItemId = 2 }], new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), values, values, Scope, Settings, Now);
            Equal(1, ranked.Count); Equal(2u, ranked[0].ItemId); True(ranked[0].PotentialProfit > 0);
        });
        Test("Screening excludes stale source worlds and missing sale velocity", () =>
        {
            var valid = Snapshot(2, Now);
            var source = valid with { UploadTimes = new Dictionary<uint, DateTimeOffset> { [2] = Now.AddDays(-2) } };
            var home = new Dictionary<uint, AggregateSnapshot?> { [2] = valid };
            Equal(0, AggregateScreener.Rank([new() { ItemId = 2 }], new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), home,
                new Dictionary<uint, AggregateSnapshot?> { [2] = source }, Scope, Settings, Now).Count);
            home[2] = valid with { Nq = valid.Nq with { WorldDailySales = null } };
            Equal(0, AggregateScreener.Rank([new() { ItemId = 2 }], new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), home, home, Scope, Settings, Now).Count);
        });
        Test("Craft screening requires known fresh prices for every ingredient", () =>
        {
            var sale = Snapshot(1, Now) with { Nq = Snapshot(1, Now).Nq with { DcMinimumWorldId = 1 } };
            var prices = new Dictionary<uint, AggregateSnapshot?> { [1] = sale, [2] = Snapshot(2, Now) };
            var recipes = new Dictionary<uint, IReadOnlyList<CraftingRecipe>> { [1] = [new() { ResultItemId = 1, AmountResult = 2,
                Ingredients = [new() { ItemId = 2, Quantity = 1 }] }] };
            var catalog = new ItemCatalogEntry[] { new() { ItemId = 1 } };
            Equal(1, AggregateScreener.Rank(catalog, recipes, prices, prices, Scope, Settings, Now).Count);
            prices.Remove(2);
            Equal(0, AggregateScreener.Rank(catalog, recipes, prices, prices, Scope, Settings, Now).Count);
        });
        Async("Aggregate HTTP batches 100 IDs with both qualities in one request", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[],\"failedItems\":[]}") });
            using var client = new UniversalisService(new HttpClient(handler), TimeSpan.Zero);
            var result = await client.GetAggregatesAsync("Home", Enumerable.Range(1, 201).Select(i => (uint)i).ToArray(), CancellationToken.None);
            Equal(3, handler.Urls.Count); Equal(201, result.Count);
            True(handler.Urls.All(u => u.Contains("/aggregated/Home/") && !u.Contains("hq=")));
        });
        Async("Unavailable singleton aggregates do not abort an otherwise valid sweep", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.NotFound));
            using var client = new UniversalisService(new HttpClient(handler), TimeSpan.Zero);
            var result = await client.GetAggregatesAsync("Home", [99u], CancellationToken.None);
            Equal<AggregateSnapshot?>(null, result[99]);
        });
        Async("A missing multi-item aggregate scope remains an error", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.NotFound));
            using var client = new UniversalisService(new HttpClient(handler), TimeSpan.Zero);
            try { await client.GetAggregatesAsync("InvalidScope", [1u, 2u], CancellationToken.None); throw new Exception("Expected HTTP error"); }
            catch (HttpRequestException) { }
        });
        Async("Full 16845-item catalog is screened in 169 aggregate batches", async () =>
        {
            var market = new FakeAggregates(); var updates = new List<ScanUpdate>();
            var catalog = Enumerable.Range(1, 16845).Select(i => new ItemCatalogEntry { ItemId = (uint)i }).ToArray();
            var summary = await new OpportunityScanner(market).ScanAsync(Scope, catalog,
                new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, CancellationToken.None);
            Equal<string?>(null, summary.Error); Equal(169, market.AggregateCalls.Count);
            True(market.AggregateCalls.All(c => c.Count <= 100 && c.Scope == "Home"));
            Equal(16845, updates[^1].Coverage.Screened); Equal(16845, updates[^1].Coverage.WithSalesData);
            Equal(16845, updates[^1].Coverage.FreshSalesData); Equal(16845, updates[^1].Coverage.FreshSourcePrices); Equal(200, summary.Evaluated);
            // Empty detailed responses must never turn aggregate estimates into recommendations.
            True(updates.All(u => u.Opportunities.Count == 0));
        });
        Async("Missing aggregate data is counted separately from screened coverage", async () =>
        {
            var market = new FakeAggregates { Missing = [2u] }; var updates = new List<ScanUpdate>();
            await new OpportunityScanner(market).ScanAsync(Scope, [new() { ItemId = 1 }, new() { ItemId = 2 }],
                new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, CancellationToken.None);
            Equal(2, updates[^1].Coverage.Screened); Equal(1, updates[^1].Coverage.WithSalesData);
        });
        Async("Cross-DC travel screens home sales separately from current-DC sourcing", async () =>
        {
            var market = new FakeAggregates(); var updates = new List<ScanUpdate>();
            await new OpportunityScanner(market).ScanAsync(Scope with { DataCenter = "VisitedDC" }, [new() { ItemId = 1 }],
                new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, CancellationToken.None);
            Equal(2, market.AggregateCalls.Count); Equal("Home", market.AggregateCalls[0].Scope); Equal("VisitedDC", market.AggregateCalls[1].Scope);
        });
        Async("Paused screening resumes from cached batches without re-fetching them", async () =>
        {
            var market = new FakeAggregates(); var scanner = new OpportunityScanner(market);
            var catalog = Enumerable.Range(1, 201).Select(i => new ItemCatalogEntry { ItemId = (uint)i }).ToArray();
            using var cancel = new CancellationTokenSource();
            try
            {
                await scanner.ScanAsync(Scope, catalog, new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings,
                    u => { if (u.Phase == ScanPhase.Screening && u.Completed == 100) cancel.Cancel(); }, cancel.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            Equal(1, market.AggregateCalls.Count);
            await scanner.ScanAsync(Scope, catalog, new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, _ => { }, CancellationToken.None);
            Equal(3, market.AggregateCalls.Count);
        });
        Async("Aggregate HTTP failure never reports full coverage", async () =>
        {
            var market = new FakeAggregates { FailOnCall = 2 }; var updates = new List<ScanUpdate>();
            var catalog = Enumerable.Range(1, 201).Select(i => new ItemCatalogEntry { ItemId = (uint)i }).ToArray();
            var summary = await new OpportunityScanner(market).ScanAsync(Scope, catalog,
                new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, CancellationToken.None);
            True(summary.Error != null); Equal(100, updates[^1].Coverage.Screened); Equal(0, market.DetailCalls);
        });
        return tests;
    }

    private sealed class FakeAggregates : IMarketDataClient
    {
        public List<(string Scope, int Count)> AggregateCalls { get; } = [];
        public HashSet<uint> Missing { get; init; } = [];
        public int FailOnCall { get; init; }
        public int DetailCalls { get; private set; }
        public Task<IReadOnlyDictionary<uint, AggregateSnapshot?>> GetAggregatesAsync(string scope, IReadOnlyCollection<uint> itemIds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); AggregateCalls.Add((scope, itemIds.Count));
            if (AggregateCalls.Count == FailOnCall) throw new HttpRequestException("Unavailable");
            return Task.FromResult<IReadOnlyDictionary<uint, AggregateSnapshot?>>(itemIds.ToDictionary(id => id,
                id => Missing.Contains(id) ? null : Snapshot(id, DateTimeOffset.UtcNow)));
        }
        public Task<IReadOnlyList<MarketPriceSnapshot>> GetSnapshotsAsync(string scope, IReadOnlyCollection<uint> itemIds,
            bool hq, bool listings, CancellationToken cancellationToken, bool includeHistory = false)
        {
            DetailCalls++; cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MarketPriceSnapshot>>(itemIds.Select(id => new MarketPriceSnapshot { ItemId = id }).ToArray());
        }
    }
}
