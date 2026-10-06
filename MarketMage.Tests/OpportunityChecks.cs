using System.Net;
using MarketMage.Models;
using MarketMage.Services;

internal static class OpportunityChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpportunitySettings Settings = new() { Budget = 100_000, MinimumProfit = 1, MinimumRoi = 0 };
    private static readonly ItemCatalogEntry Item = new() { ItemId = 1, Name = "Output", CanBeHq = true };
    private static MarketListing Listing(string world = "Other", long price = 100, int quantity = 10, DateTimeOffset? reviewed = null) =>
        new() { World = world, PricePerUnit = price, Quantity = quantity, ReviewedAt = reviewed ?? Now.AddMinutes(-10) };
    private static MarketPriceSnapshot Sale(long ask = 201, DateTimeOffset? uploaded = null, IReadOnlyList<RecentSale>? sales = null,
        IReadOnlyList<MarketListing>? listings = null) => new()
    {
        ItemId = 1, UploadedAt = uploaded ?? Now.AddMinutes(-5),
        Sales = sales ?? [new(200, 10, Now.AddHours(-1)), new(200, 10, Now.AddHours(-2)), new(200, 10, Now.AddHours(-3))],
        Listings = listings ?? [Listing("Home", ask)],
    };
    private static MarketPriceSnapshot Stock(params MarketListing[] listings) => new() { ItemId = 1, Listings = listings };
    private static IReadOnlyList<GilOpportunity> Evaluate(MarketPriceSnapshot? sale = null, MarketPriceSnapshot? stock = null,
        OpportunitySettings? settings = null, IReadOnlyList<CraftingRecipe>? recipes = null,
        IReadOnlyDictionary<uint, MarketPriceSnapshot>? ingredients = null, bool hq = false, double? dailySales = 40) =>
        OpportunityEngine.Evaluate(Item, hq, "Home", sale ?? Sale(), stock ?? Stock(Listing()), recipes ?? [],
            ingredients ?? new Dictionary<uint, MarketPriceSnapshot>(), settings ?? Settings, Now, dailySales);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        var checks = new List<(string, Func<Task>)>();
        void Test(string name, Action check) => checks.Add((name, () => { check(); return Task.CompletedTask; }));
        void Async(string name, Func<Task> check) => checks.Add((name, check));
        Test("Resale subtracts whole-stack purchase tax and sale tax", () =>
        {
            var row = Evaluate().Single();
            Equal(OpportunityKind.Resell, row.Kind); Equal(1050L, row.Outlay);
            Equal(1900L, row.Revenue); Equal(850L, row.Profit); Equal(10, row.OutputQuantity);
        });
        Test("Sale estimate is capped below current home-world competition", () =>
        { var row = Evaluate(Sale(180)).Single(); Equal(179L, row.SalePrice); Equal(650L, row.Profit); });
        Test("No speculation above recent median", () => Equal(200L, Evaluate(Sale(10000)).Single().SalePrice));
        Test("Budget includes full stacks and tax", () => Equal(0, Evaluate(settings: Settings with { Budget = 1049 }).Count));
        Test("Low profit and ROI are excluded", () =>
        {
            Equal(0, Evaluate(settings: Settings with { MinimumProfit = 851 }).Count);
            Equal(0, Evaluate(settings: Settings with { MinimumRoi = 1 }).Count);
        });
        Test("No same-world resale recommendation", () => Equal(0, Evaluate(stock: Stock(Listing("Home"))).Count));
        Test("No resale stack larger than observed 7-day sampled units", () => Equal(0, Evaluate(stock: Stock(Listing(quantity: 31))).Count));
        Test("Stale and future uploaded data cannot rank", () =>
        {
            Equal(0, Evaluate(Sale(uploaded: Now.AddDays(-2))).Count);
            Equal(0, Evaluate(Sale(uploaded: Now.AddMinutes(1))).Count);
        });
        Test("Unknown upload time cannot rank", () => Equal(0, Evaluate(new() { ItemId = 1, Sales = Sale().Sales, Listings = Sale().Listings }).Count));
        Test("Old sales cannot inflate demand", () =>
            Equal(0, Evaluate(Sale(sales: [new(200, 10, Now.AddDays(-8)), new(200, 10, Now.AddDays(-9)), new(200, 10, Now.AddDays(-10))])).Count));
        Test("One sale cannot establish sufficient demand", () => Equal(0, Evaluate(Sale(sales: [new(200, 100, Now)])).Count));
        Test("Unknown or stale purchase age cannot rank", () =>
        {
            Equal(0, Evaluate(stock: Stock(new MarketListing { World = "Other", PricePerUnit = 10, Quantity = 1 })).Count);
            Equal(0, Evaluate(stock: Stock(Listing(reviewed: Now.AddDays(-2)))).Count);
        });
        Test("Stale cheapest home ask is not skipped to justify inflated profit", () =>
            Equal(0, Evaluate(Sale(listings: [Listing("Home", 150, reviewed: Now.AddDays(-2)), Listing("Home", 300)])).Count));
        Test("Missing competing listings cannot rank", () => Equal(0, Evaluate(Sale(listings: [])).Count));
        Test("Whole-stack optimizer prefers lower checkout cost over lower unit price", () =>
        {
            var steps = OpportunityEngine.BuyWholeStacks(new() { ItemId = 2, ItemName = "Material", Quantity = 3 }, 1,
                [Listing(price: 1, quantity: 100), Listing(price: 10, quantity: 3)], Settings, Now)!;
            Equal(1, steps.Count); Equal(3, steps[0].Quantity); Equal(32L, steps[0].CostWithTax);
        });
        Test("Whole-stack optimizer combines multiple stacks without reusing one", () =>
        {
            var steps = OpportunityEngine.BuyWholeStacks(new() { ItemId = 2, Quantity = 3 }, 1,
                [Listing(price: 4, quantity: 2), Listing(price: 8, quantity: 1), Listing(price: 7, quantity: 3)], Settings, Now)!;
            Equal(18L, steps.Sum(p => p.CostWithTax)); Equal(2, steps.Count);
            Equal<IReadOnlyList<PurchaseStep>?>(null, OpportunityEngine.BuyWholeStacks(new() { ItemId = 2, Quantity = 3 }, 1,
                [Listing(price: 4, quantity: 2)], Settings, Now));
        });
        var recipe = new CraftingRecipe
        {
            RecipeId = 10, ResultItemId = 1, AmountResult = 2, CanHq = true, CraftJob = "Alchemist", CraftLevel = 50,
            Ingredients = [new() { ItemId = 2, ItemName = "Material", Quantity = 3 }],
        };
        var ingredients = new Dictionary<uint, MarketPriceSnapshot> { [2] = Stock(Listing(price: 10, quantity: 30)) };
        Test("Crafting compares batches using actual yield and whole-stack outlay", () =>
        {
            var row = Evaluate(recipes: [recipe], ingredients: ingredients).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(10, row.CraftCount); Equal(20, row.OutputQuantity); Equal(315L, row.Outlay);
            Equal(3485L, row.Profit); Equal(10u, row.RecipeId);
        });
        Test("Slow demand limits crafts using actual recipe yield", () =>
        {
            var row = Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 4).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(1, row.CraftCount); Equal(2, row.OutputQuantity);
            Equal(2d, SaleTiming.Days(row.OutputQuantity, row.EstimatedDailySales, 25)!.Value);
        });
        Test("One craft exceeding sale horizon is withheld", () =>
            True(!Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 1).Any(r => r.Kind == OpportunityKind.Craft)));
        Test("Zero or invalid demand cannot recommend crafting", () =>
        {
            foreach (var rate in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
                True(!Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: rate).Any(r => r.Kind == OpportunityKind.Craft));
        });
        Test("Intermediate craft counts are considered", () =>
        {
            var row = Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 8).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(3, row.CraftCount); Equal(6, row.OutputQuantity);
        });
        Test("Per-day ranking avoids larger equal-throughput batches", () =>
        {
            var stacks = new Dictionary<uint, MarketPriceSnapshot> { [2] = Stock(Enumerable.Range(1, 10).Select(_ => Listing(price: 10, quantity: 3)).ToArray()) };
            var row = Evaluate(recipes: [recipe], ingredients: stacks, dailySales: 8).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(1, row.CraftCount);
        });
        Test("Missing aggregate demand falls back to sampled units over seven days", () =>
        {
            var row = Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: null).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(30d / 7, row.EstimatedDailySales!.Value); Equal(1, row.CraftCount); True(row.UsesSampledDemand);
        });
        Test("Stricter share removes previously qualifying craft from book", () =>
        {
            var book = new OpportunityBook();
            book.Apply(new([1u], Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 8), 1, 1));
            True(!book.Current(Now, Settings with { ExpectedMarketSharePercent = 1 }).Any(r => r.Kind == OpportunityKind.Craft));
        });
        Test("Existing unsold units reduce the new craft allowance", () =>
        {
            var reserved = Settings with { CommittedUnits = new Dictionary<(uint, bool), int> { [(1, false)] = 4 } };
            var row = Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 8, settings: reserved).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(2, row.OutputQuantity); Equal(4, row.CommittedUnits);
            Equal(3d, row.BatchComparisons.Single(b => b.Crafts == 1).Days!.Value);
            Equal("Exceeds remaining demand", row.BatchComparisons.Single(b => b.Crafts == 2).Status);
        });
        Test("Fully reserved demand suppresses additional crafting", () =>
        {
            var reserved = Settings with { CommittedUnits = new Dictionary<(uint, bool), int> { [(1, false)] = 6 } };
            True(!Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 8, settings: reserved).Any(r => r.Kind == OpportunityKind.Craft));
        });
        Test("Batch comparison retains all ten quantities and exact chosen costs", () =>
        {
            var row = Evaluate(recipes: [recipe], ingredients: ingredients, dailySales: 8).Single(r => r.Kind == OpportunityKind.Craft);
            Equal(10, row.BatchComparisons.Count);
            var selected = row.BatchComparisons.Single(b => b.Crafts == row.CraftCount);
            Equal<long?>(row.Outlay, selected.Cost); Equal<long?>(row.Profit, selected.Profit);
            Equal(row.OutputQuantity, selected.Quantity); Equal("Qualifies", selected.Status);
        });
        Test("Missing batch ingredients remain unknown rather than zero cost", () =>
        {
            var row = Evaluate(recipes: [recipe]).Single();
            Equal(10, row.BatchComparisons.Count); True(row.BatchComparisons.All(b => b.Cost == null && b.Profit == null));
        });
        Test("Alternate recipes compete on net profit", () =>
        {
            var cheaper = new CraftingRecipe { RecipeId = 20, ResultItemId = 1, AmountResult = 2,
                Ingredients = [new() { ItemId = 3, ItemName = "Cheaper", Quantity = 1 }] };
            var prices = new Dictionary<uint, MarketPriceSnapshot>(ingredients) { [3] = Stock(Listing(price: 1, quantity: 10)) };
            Equal(20u, Evaluate(recipes: [recipe, cheaper], ingredients: prices).Single(r => r.Kind == OpportunityKind.Craft).RecipeId);
        });
        Test("Incomplete crafting inputs are withheld, not treated as free", () =>
            Equal(false, Evaluate(recipes: [recipe]).Any(r => r.Kind == OpportunityKind.Craft)));
        Test("HQ craft is conditional on recipe supporting HQ", () =>
        {
            var nqOnly = new CraftingRecipe { ResultItemId = 1, AmountResult = 2, Ingredients = recipe.Ingredients };
            Equal(false, Evaluate(recipes: [nqOnly], ingredients: ingredients, hq: true).Any(r => r.Kind == OpportunityKind.Craft));
        });
        Test("Different ingredient requirements cannot double-spend one stack", () =>
        {
            var duplicate = new CraftingRecipe { ResultItemId = 1, AmountResult = 1,
                Ingredients = [new() { ItemId = 2, Quantity = 2 }, new() { ItemId = 2, Quantity = 2 }] };
            Equal(false, Evaluate(recipes: [duplicate], ingredients: new Dictionary<uint, MarketPriceSnapshot> { [2] = Stock(Listing(quantity: 3)) })
                .Any(r => r.Kind == OpportunityKind.Craft));
        });
        Test("Validation rotates lower-ranked candidates without dropping the strongest", () =>
        {
            var ranked = Enumerable.Range(1, 300).Select(i => new ScreenCandidate((uint)i, 301 - i)).ToArray();
            var valid = ranked.Select(c => c.ItemId).ToHashSet();
            var first = OpportunityScanner.PlanValidation(ranked, [290u, 999u], valid, 0);
            Equal(290u, first[0]); Equal(201, first.Length); True(first.Contains(1u)); True(first.Contains(151u));
            var second = OpportunityScanner.PlanValidation(ranked, [], valid, 50);
            True(second.Contains(201u)); Equal(false, second.Contains(151u));
        });
        Test("A rescan removes a no-longer-profitable finding", () =>
        {
            var book = new OpportunityBook(); book.Apply(new([1u], Evaluate(), 1, 1));
            Equal(1, book.Current(Now, Settings).Count);
            book.Apply(new([1u], [], 1, 1)); Equal(0, book.Current(Now, Settings).Count);
        });
        Test("Opportunity findings expire after 15 minutes", () =>
        {
            var book = new OpportunityBook(); book.Apply(new([1u], Evaluate(), 1, 1));
            Equal(0, book.Current(Now.AddMinutes(16), Settings).Count);
        });
        Test("History parser retains quantities and quality-specific demand", () =>
        {
            const string json = """{"itemID":1,"recentHistory":[{"hq":false,"pricePerUnit":50,"quantity":3,"timestamp":1700000000},{"hq":true,"pricePerUnit":500,"quantity":9,"timestamp":1700000000}]}""";
            var snapshot = MarketParser.Parse(json, [1u], "Home", false)[0]; Equal(1, snapshot.Sales.Count); Equal(3, snapshot.Sales[0].Quantity);
        });
        Async("Recent market discovery parses and deduplicates item IDs", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[{\"itemID\":1},{\"itemID\":1},{\"itemID\":2}]}") });
            using var client = new UniversalisService(new HttpClient(handler), TimeSpan.Zero);
            var ids = await client.GetRecentItemsAsync("Test DC", CancellationToken.None);
            Equal(2, ids.Count); True(handler.Urls[0].Contains("entries=200"));
        });
        Async("Discovery requests sale listings and history together", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{\"itemID\":1}") });
            using var client = new UniversalisService(new HttpClient(handler), TimeSpan.Zero);
            await client.GetSnapshotsAsync("Home", [1u], false, true, CancellationToken.None, true);
            True(handler.Urls[0].Contains("listings=100") && handler.Urls[0].Contains("entries=20"));
        });
        Async("Scanner publishes incremental findings and excludes worlds outside local DC", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var fake = new FakeMarket(now);
            var scanner = new OpportunityScanner(fake);
            var updates = new List<ScanUpdate>();
            var result = await scanner.ScanAsync(new("Home", "LocalDC", new HashSet<string> { "Other", "Home" }),
                [Item], new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, CancellationToken.None);
            Equal<string?>(null, result.Error); Equal(1, result.Evaluated);
            True(updates[^1].Opportunities.Count > 0);
            True(updates[^1].Opportunities.All(r => r.Purchases.All(p => p.World == "Other")));
            True(fake.Scopes.All(s => s is "Home" or "LocalDC"));
        });
        Async("Scanner caches shared ingredients within a round", async () =>
        {
            var fake = new FakeMarket(DateTimeOffset.UtcNow);
            var catalog = Enumerable.Range(1, 26).Select(i => new ItemCatalogEntry { ItemId = (uint)i }).ToArray();
            var recipeMap = catalog.ToDictionary(i => i.ItemId, i => (IReadOnlyList<CraftingRecipe>)[new CraftingRecipe
                { ResultItemId = i.ItemId, AmountResult = 1, Ingredients = [new() { ItemId = 99, Quantity = 1 }] }]);
            await new OpportunityScanner(fake).ScanAsync(new("Home", "LocalDC", new HashSet<string> { "Other" }), catalog,
                recipeMap, [], 0, Settings, _ => { }, CancellationToken.None);
            Equal(1, fake.IngredientRequests);
        });
        Async("Targeted refresh only reads selected items and required ingredients and bypasses aggregate cache", async () =>
        {
            var fake = new FakeMarket(DateTimeOffset.UtcNow);
            var scanner = new OpportunityScanner(fake);
            var catalog = Enumerable.Range(1, 1000).Select(i => new ItemCatalogEntry { ItemId = (uint)i }).ToArray();
            var map = new Dictionary<uint, IReadOnlyList<CraftingRecipe>> { [1] = [new CraftingRecipe
                { ResultItemId = 1, AmountResult = 1, Ingredients = [new() { ItemId = 9999, Quantity = 1 }] }] };
            var updates = new List<ScanUpdate>();
            for (var i = 0; i < 2; i++)
            {
                var result = await scanner.ScanAsync(new("Home", "LocalDC", new HashSet<string> { "Other" }), catalog,
                    map, [1u], 35, Settings, updates.Add, CancellationToken.None, targeted: true);
                Equal(1, result.Evaluated); Equal(35, result.NextOffset); Equal<string?>(null, result.Error);
            }
            Equal(2, fake.AggregateRequests.Count);
            True(fake.AggregateRequests.All(ids => ids.SequenceEqual(new[] { 1u })));
            True(fake.DetailIds.All(id => id is 1 or 9999)); True(fake.DetailIds.Contains(9999u));
            True(updates.Where(u => u.ItemIds.Count > 0).All(u => u.ItemIds.SequenceEqual(new[] { 1u })));
            True(updates[^1].Opportunities.Count > 0);
        });
        Async("Targeted refresh cancellation cannot publish validated rows", async () =>
        {
            using var cancel = new CancellationTokenSource(); var updates = new List<ScanUpdate>();
            var fake = new FakeMarket(DateTimeOffset.UtcNow) { OnRequest = () => cancel.Cancel() };
            try
            {
                await new OpportunityScanner(fake).ScanAsync(new("Home", "DC", new HashSet<string>()), [Item],
                    new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [1u], 0, Settings, updates.Add, cancel.Token, targeted: true);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            Equal(0, updates.Count(u => u.ItemIds.Count > 0));
        });
        Async("Scanner stops after cancellation without publishing a batch", async () =>
        {
            using var cancel = new CancellationTokenSource(); var updates = new List<ScanUpdate>();
            var fake = new FakeMarket(DateTimeOffset.UtcNow) { OnRequest = () => cancel.Cancel() };
            try
            {
                await new OpportunityScanner(fake).ScanAsync(new("Home", "LocalDC", new HashSet<string> { "Other" }), [Item],
                    new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 0, Settings, updates.Add, cancel.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            Equal(0, updates.Count(u => u.ItemIds.Count > 0));
        });
        Async("Scanner reports HTTP failure and preserves rotation for retry", async () =>
        {
            var fake = new FakeMarket(DateTimeOffset.UtcNow) { OnRequest = () => throw new HttpRequestException("Unavailable") };
            var result = await new OpportunityScanner(fake).ScanAsync(new("Home", "LocalDC", new HashSet<string> { "Other" }), [Item],
                new Dictionary<uint, IReadOnlyList<CraftingRecipe>>(), [], 25, Settings, _ => { }, CancellationToken.None);
            True(result.Error != null); Equal(25, result.NextOffset);
        });
        return checks;
    }

    private sealed class FakeMarket(DateTimeOffset now) : IMarketDataClient
    {
        public List<uint[]> AggregateRequests { get; } = [];
        public List<uint> DetailIds { get; } = [];
        public List<string> Scopes { get; } = [];
        public Action? OnRequest { get; init; }
        public int IngredientRequests { get; private set; }
        public Task<IReadOnlyDictionary<uint, AggregateSnapshot?>> GetAggregatesAsync(string scope, IReadOnlyCollection<uint> ids, CancellationToken token)
        {
            OnRequest?.Invoke(); token.ThrowIfCancellationRequested();
            AggregateRequests.Add(ids.ToArray());
            var quality = new AggregateQuality { WorldMinimum = 201, DcMinimum = 100, DcMinimumWorldId = 1,
                WorldAverageSale = 200, WorldDailySales = 10, WorldLastSale = now.AddHours(-1) };
            return Task.FromResult<IReadOnlyDictionary<uint, AggregateSnapshot?>>(ids.ToDictionary(id => id, id => (AggregateSnapshot?)new AggregateSnapshot
            { ItemId = id, Nq = quality, Hq = quality, UploadTimes = new Dictionary<uint, DateTimeOffset> { [0] = now, [1] = now } }));
        }
        public Task<IReadOnlyList<MarketPriceSnapshot>> GetSnapshotsAsync(string scope, IReadOnlyCollection<uint> ids,
            bool hq, bool listings, CancellationToken cancellationToken, bool includeHistory = false)
        {
            OnRequest?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Scopes.Add(scope); DetailIds.AddRange(ids);
            if (ids.Contains(99u)) IngredientRequests++;
            return Task.FromResult<IReadOnlyList<MarketPriceSnapshot>>(ids.Select(id => new MarketPriceSnapshot
            {
                ItemId = id, UploadedAt = now,
                Sales = [new(200, 10, now.AddHours(-1)), new(200, 10, now.AddHours(-2)), new(200, 10, now.AddHours(-3))],
                Listings = scope == "Home" ? [Listing("Home", 201, reviewed: now)] :
                    [Listing("Other", 100, reviewed: now), Listing("OutsideDC", 1, reviewed: now)],
            }).ToArray());
        }
    }
}
