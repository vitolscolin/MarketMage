using System.Net;
using MarketMage.Models;
using MarketMage.Services;

if (args.Contains("--regional-smoke"))
{
    using var regional = new RegionalHistoryService();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var regionalResult = await regional.GetAsync(2, "Cactuar", false, timeout.Token);
    Console.WriteLine($"PASS Saddlebag regional API: item {regionalResult.ItemId}, {regionalResult.SampleTransactions} sampled transactions; metrics parsed without altering local prices.");
    return 0;
}
if (args.Contains("--live-smoke")) return await LiveSmoke.RunAsync();

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; received {actual}.");
}
const string history = """
{"itemID":1,"lastUploadTime":1700000000000,"recentHistory":[
{"hq":false,"pricePerUnit":100,"timestamp":1700000000},
{"hq":true,"pricePerUnit":900,"timestamp":1700000010},
{"hq":false,"pricePerUnit":201,"timestamp":1700000020},
{"hq":false,"pricePerUnit":0,"timestamp":1700000030}],
"listings":[{"hq":false,"pricePerUnit":80,"quantity":10,"lastReviewTime":1700000000},
{"hq":true,"pricePerUnit":800,"quantity":20,"lastReviewTime":1700000000}]}
""";
Test("NQ median excludes HQ and invalid prices", () => { var s = MarketParser.Parse(history, [1u], "Cactuar", false)[0]; Equal(150L, s.MedianRecentSalePrice); Equal(2, s.RecentSalesCount); Equal(1700000020L, s.LastSaleTime!.Value.ToUnixTimeSeconds()); });
Test("HQ history and listings stay separate", () => { var s = MarketParser.Parse(history, [1u], "Cactuar", true)[0]; Equal(900L, s.MedianRecentSalePrice); Equal(1, s.Listings.Count); Equal(800L, s.Listings[0].PricePerUnit); });
Test("Single-world listings inherit world and upload milliseconds", () => { var s = MarketParser.Parse(history, [1u], "Cactuar", false)[0]; Equal("Cactuar", s.Listings[0].World); Equal(1700000000L, s.UploadedAt!.Value.ToUnixTimeSeconds()); });
Test("Multi-item response preserves missing items", () => { var s = MarketParser.Parse("{\"items\":{\"1\":" + history + "}}", [1u, 2u], "Aether", false); Equal(2, s.Count); Equal(0, s[1].RecentSalesCount); });
Test("Mismatched single item is not attributed to requested ID", () => Equal(0L, MarketParser.Parse(history, [2u], "Cactuar", false)[0].MedianRecentSalePrice));
Test("Empty history is unknown price", () => Equal(0L, MarketParser.Parse("{\"itemID\":1}", [1u], "Cactuar", false)[0].MedianRecentSalePrice));
Test("Median addition does not overflow int", () => { var json = history.Replace("100,", "2147483647,").Replace("201,", "2147483647,"); Equal(2147483647L, MarketParser.Parse(json, [1u], "Cactuar", false)[0].MedianRecentSalePrice); });
Test("Malformed timestamps do not crash parser", () => { var json = history.Replace("1700000020", "9223372036854775807"); Equal(1700000000L, MarketParser.Parse(json, [1u], "Cactuar", false)[0].LastSaleTime!.Value.ToUnixTimeSeconds()); });
var ingredient = new RecipeIngredient { ItemId = 2, ItemName = "Material", Quantity = 3 };
MarketListing Listing(string world, long price, int qty) => new() { World = world, PricePerUnit = price, Quantity = qty, ReviewedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000) };
Dictionary<uint, MarketPriceSnapshot> Prices(params MarketListing[] listings) => new() { [2] = new() { ItemId = 2, Listings = listings } };
Test("Cost consumes sufficient quantities across price levels", () => { var cost = ProfitCalculator.Cost(ingredient, Prices(Listing("A", 10, 1), Listing("A", 20, 5))); Equal(true, cost.HasPrice); Equal(50L, cost.TotalPrice); Equal(6L, cost.AvailableQuantity); });
Test("Insufficient stock withholds cost", () => Equal(false, ProfitCalculator.Cost(ingredient, Prices(Listing("A", 10, 2))).HasPrice));
Test("DC chooses cheapest sufficient world, not cheapest unit listing", () => { var cost = ProfitCalculator.Cost(ingredient, Prices(Listing("A", 1, 1), Listing("A", 100, 2), Listing("B", 20, 4))); Equal("B", cost.SourceWorld); Equal(60L, cost.TotalPrice); });
Test("DC does not combine insufficient stock from different worlds", () => Equal(false, ProfitCalculator.Cost(ingredient, Prices(Listing("A", 10, 2), Listing("B", 10, 2))).HasPrice));
var recipe = new CraftingRecipe { ResultItemId = 1, AmountResult = 3, Ingredients = [ingredient] };
Test("Recipe yield, tax rounding, profit and ROI", () => { var e = ProfitCalculator.Build(new() { ItemId = 1, MedianRecentSalePrice = 101 }, "Output", recipe, Prices(Listing("A", 10, 1), Listing("A", 20, 5)), Prices(Listing("B", 10, 3))); Equal<long?>(17, e.EstimatedMaterialCost); Equal<long?>(95, e.AdjustedRevenue); Equal<long?>(78, e.Profit); Equal<long?>(85, e.DataCenterProfit); Equal<double?>(78d / 17, e.Roi); });
Test("Missing ingredient prices withhold profit", () => { var e = ProfitCalculator.Build(new() { ItemId = 1, MedianRecentSalePrice = 100 }, "Output", recipe, new Dictionary<uint, MarketPriceSnapshot>(), new Dictionary<uint, MarketPriceSnapshot>()); Equal<long?>(null, e.Profit); Equal("Material", e.MissingIngredientNames.Single()); });
Test("No sale history withholds revenue and profit", () => { var e = ProfitCalculator.Build(new() { ItemId = 1 }, "Output", recipe, Prices(Listing("A", 10, 3)), Prices()); Equal<long?>(null, e.AdjustedRevenue); Equal<long?>(null, e.Profit); });
Test("Noncraftable output has no material cost", () => { var e = ProfitCalculator.Build(new() { ItemId = 1 }, "Output", null, Prices(), Prices()); Equal(false, e.IsCraftable); Equal<long?>(null, e.EstimatedMaterialCost); });
Test("Large ingredient totals use 64-bit arithmetic", () => Equal(6442450941L, ProfitCalculator.Cost(ingredient, Prices(Listing("A", int.MaxValue, 3))).TotalPrice));
Test("Unknown listing review time stays unknown", () => { var cost = ProfitCalculator.Cost(ingredient, Prices(new() { World = "A", PricePerUnit = 10, Quantity = 1 }, Listing("A", 20, 2))); Equal<DateTimeOffset?>(null, cost.ReviewedAt); });
AsyncTest("Canceled request cannot publish late results", async () => { using var latest = new LatestRequest<int>(); var tcs = new TaskCompletionSource<int>(); latest.Start(_ => tcs.Task); latest.Cancel(); tcs.SetResult(1); await tcs.Task; Equal(false, latest.TryTake(out _)); Equal(false, latest.IsRunning); });
Test("Replacement publishes only the new request", () => { using var latest = new LatestRequest<int>(); var old = new TaskCompletionSource<int>(); latest.Start(_ => old.Task); latest.Start(_ => Task.FromResult(2)); old.SetResult(1); Equal(true, latest.TryTake(out var value)); Equal(2, value); Equal(false, latest.TryTake(out _)); });
Test("Dispose cancels the request token", () => { var latest = new LatestRequest<int>(); CancellationToken captured = default; latest.Start(t => { captured = t; return Task.FromResult(1); }); latest.Dispose(); Equal(true, captured.IsCancellationRequested); Equal(false, latest.TryTake(out _)); });
AsyncTest("Requests batch ingredient IDs and encode quality", async () => { var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":{}}") }); using var service = new UniversalisService(new HttpClient(handler)); var result = await service.GetSnapshotsAsync("Aether", Enumerable.Range(1, 121).Select(i => (uint)i).ToArray(), false, true, CancellationToken.None); Equal(3, handler.Urls.Count); Equal(121, result.Count); Equal(true, handler.Urls.All(u => u.Contains("listings=100") && u.Contains("hq=false"))); });
AsyncTest("HTTP failure does not silently yield empty prices", async () => { var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)); using var service = new UniversalisService(new HttpClient(handler)); try { await service.GetSnapshotsAsync("Cactuar", [1u], false, false, CancellationToken.None); throw new Exception("Expected HTTP failure"); } catch (HttpRequestException) { } });
AsyncTest("Transient server failure retries", async () => { var handler = new StubHandler((_, attempt) => new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("{\"itemID\":1}") }); using var service = new UniversalisService(new HttpClient(handler)); await service.GetSnapshotsAsync("Cactuar", [1u], false, false, CancellationToken.None); Equal(2, handler.Urls.Count); });
AsyncTest("Empty ingredient list makes no network requests", async () => { var handler = new StubHandler((_, _) => throw new Exception("Unexpected request")); using var service = new UniversalisService(new HttpClient(handler)); Equal(0, (await service.GetSnapshotsAsync("Cactuar", [], false, true, CancellationToken.None)).Count); });
Test("Data-center response retains each listing world", () =>
{
    const string json = """
    {"items":{"1":{"listings":[{"hq":false,"pricePerUnit":5,"quantity":3,"worldName":"Faerie"},{"hq":false,"pricePerUnit":6,"quantity":4,"worldName":"Cactuar"}]}}}
    """;
    var snapshot = MarketParser.Parse(json, [1u], "Aether", false)[0];
    Equal("Faerie", snapshot.Listings[0].World);
    Equal("Cactuar", snapshot.Listings[1].World);
});
AsyncTest("Cancellation prevents subsequent request batches", async () =>
{
    using var cancel = new CancellationTokenSource();
    var handler = new StubHandler((_, _) =>
    {
        cancel.Cancel();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":{}}") };
    });
    using var service = new UniversalisService(new HttpClient(handler));
    try
    {
        await service.GetSnapshotsAsync("Aether", Enumerable.Range(1, 100).Select(i => (uint)i).ToArray(), false, true, cancel.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { }
    Equal(1, handler.Urls.Count);
});
tests.AddRange(OpportunityChecks.All());
tests.AddRange(AggregateChecks.All());
tests.AddRange(MonitorChecks.All());
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;

sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<string> Urls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Urls.Add(request.RequestUri!.ToString()); return Task.FromResult(response(request, Urls.Count)); }
}
