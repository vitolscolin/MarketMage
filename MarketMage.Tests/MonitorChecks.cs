using System.Net;
using System.Text.Json;
using MarketMage.Models;
using MarketMage.Services;

internal static class MonitorChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        var tests = new List<(string, Func<Task>)>();
        void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
        void Async(string name, Func<Task> run) => tests.Add((name, run));
        void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
        var now = DateTimeOffset.UtcNow;
        var plan = new GilOpportunity { ItemId = 1, ItemName = "Test", SaleWorld = "Home", Kind = OpportunityKind.Craft,
            OutputQuantity = 10, EstimatedDailySales = 2, Outlay = 100, Revenue = 500,
            Purchases = [new(2, "Ingredient", "Other", 3, 10, 32, now)] };
        var scope = new ScanScope("Home", "DC", new HashSet<string> { "Home", "Other" });
        Test("Batch estimate accounts for quantity and chosen market share", () =>
        { Equal<double?>(5, SaleTiming.Days(10, 2, 100)); Equal<double?>(20, SaleTiming.Days(10, 2, 25)); });
        Test("Unknown zero and invalid demand do not produce a fake ETA", () =>
        {
            foreach (var rate in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity }) Equal<double?>(null, SaleTiming.Days(10, rate, 25));
            Equal<double?>(null, SaleTiming.Days(10, 2, 0)); Equal<double?>(null, SaleTiming.Days(0, 2, 25));
        });
        Test("Very slow batches show a long selling horizon", () =>
        { Equal<double?>(400, SaleTiming.Days(10, 0.1, 25)); Equal(">1 year", SaleTiming.Format(400)); Equal("Unknown", SaleTiming.Format(null)); });
        Test("Monitor deduplicates by world DC quality and method", () =>
        {
            var entries = new List<MonitoredOpportunity>();
            Equal(true, OpportunityMonitor.Add(entries, plan, "DC", now)); Equal(false, OpportunityMonitor.Add(entries, plan, "DC", now));
            Equal(true, OpportunityMonitor.Add(entries, plan with { HighQuality = true }, "DC", now));
            Equal(true, OpportunityMonitor.Add(entries, plan with { SaleWorld = "Other" }, "DC", now));
            Equal(true, OpportunityMonitor.Add(entries, plan, "DifferentDC", now));
        });
        Test("Monitor refresh preserves the original shopping plan and progress", () =>
        {
            var entries = new List<MonitoredOpportunity>(); OpportunityMonitor.Add(entries, plan, "DC", now);
            entries[0].PurchasedSteps.Add(0); entries[0].Listed = true; entries[0].Notes = "Remember this";
            var latest = plan with { OutputQuantity = 5, Revenue = 200, Purchases = [] };
            Equal(true, OpportunityMonitor.Apply(entries, scope, new([1u], [latest], 1, 1), now));
            Equal(10, entries[0].SavedPlan.OutputQuantity); Equal(5, entries[0].LatestPlan!.OutputQuantity);
            Equal(1, entries[0].PurchasedSteps.Count); Equal(true, entries[0].Listed); Equal("Remember this", entries[0].Notes);
        });
        Test("Disappearing opportunities remain saved but lose qualifying status", () =>
        {
            var entries = new List<MonitoredOpportunity>(); OpportunityMonitor.Add(entries, plan, "DC", now);
            OpportunityMonitor.Apply(entries, scope, new([1u], [], 1, 1), now);
            Equal(1, entries.Count); Equal<GilOpportunity?>(null, entries[0].LatestPlan);
            Equal("Not qualifying / data unavailable", OpportunityMonitor.Status(entries[0], now));
            Equal("Needs recheck", OpportunityMonitor.Status(entries[0], now.AddMinutes(16)));
        });
        Test("Another DC and screening progress cannot overwrite monitor status", () =>
        {
            var entries = new List<MonitoredOpportunity>(); OpportunityMonitor.Add(entries, plan, "DC", now);
            Equal(false, OpportunityMonitor.Apply(entries, scope with { DataCenter = "Elsewhere" }, new([1u], [], 1, 1), now));
            Equal(false, OpportunityMonitor.Apply(entries, scope, new([1u], [], 1, 1) { Phase = ScanPhase.Screening }, now));
            Equal(plan, entries[0].LatestPlan);
        });
        Test("Only active monitor entries in the current scope get scan priority", () =>
        {
            var entries = new List<MonitoredOpportunity>(); OpportunityMonitor.Add(entries, plan, "DC", now);
            OpportunityMonitor.Add(entries, plan with { ItemId = 3 }, "OtherDC", now);
            Equal(1u, OpportunityMonitor.Priority(entries, scope).Single()); entries[0].Sold = true;
            Equal(0, OpportunityMonitor.Priority(entries, scope).Count());
        });
        Test("Active monitor limit matches guaranteed priority capacity", () =>
        {
            var entries = new List<MonitoredOpportunity>();
            for (uint i = 1; i <= 50; i++) Equal(true, OpportunityMonitor.Add(entries, plan with { ItemId = i }, "DC", now));
            Equal(false, OpportunityMonitor.Add(entries, plan with { ItemId = 51 }, "DC", now));
            entries[0].Sold = true; Equal(true, OpportunityMonitor.Add(entries, plan with { ItemId = 51 }, "DC", now));
        });
        Test("Monitor snapshot checklist and notes survive JSON roundtrip", () =>
        {
            var entries = new List<MonitoredOpportunity>(); OpportunityMonitor.Add(entries, plan, "DC", now);
            entries[0].PurchasedSteps.Add(0); entries[0].Crafted = true; entries[0].Notes = "Saved notes";
            var loaded = JsonSerializer.Deserialize<List<MonitoredOpportunity>>(JsonSerializer.Serialize(entries))!;
            Equal("Saved notes", loaded[0].Notes); Equal(true, loaded[0].Crafted); Equal(0, loaded[0].PurchasedSteps.Single());
            Equal(32L, loaded[0].SavedPlan.Purchases.Single().CostWithTax); Equal(10, loaded[0].SavedPlan.OutputQuantity);
        });
        const string history = """{"itemID":1,"average_quantity_sold_per_day":100.5,"median_ppu":50,"total_quantity_sold":703,"total_purchase_amount":70}""";
        Test("Regional history parser preserves region metrics without local blending", () =>
        {
            var result = RegionalHistoryService.Parse(history, 1, now);
            Equal(100.5, result.UnitsPerDay); Equal(703L, result.SampleUnits); Equal(2d, plan.EstimatedDailySales);
        });
        Test("Regional error payload and mismatched IDs are rejected", () =>
        {
            foreach (var payload in new[] { "{\"exception\":\"Unavailable\"}", history.Replace("\"itemID\":1", "\"itemID\":2"), history.Replace("100.5", "-1") })
            {
                try { RegionalHistoryService.Parse(payload, 1, now); throw new Exception("Expected invalid response"); }
                catch (JsonException) { }
            }
        });
        Async("Regional lookup caches by item world and quality", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(history) });
            using var service = new RegionalHistoryService(new HttpClient(handler), TimeSpan.Zero);
            await service.GetAsync(1, "Home", false, CancellationToken.None);
            await service.GetAsync(1, "Home", false, CancellationToken.None);
            Equal(1, handler.Urls.Count);
            await service.GetAsync(1, "Home", true, CancellationToken.None); Equal(2, handler.Urls.Count);
            await service.GetAsync(1, "Other", false, CancellationToken.None); Equal(3, handler.Urls.Count);
        });
        Async("Regional provider failure does not become zero prices", async () =>
        {
            var handler = new StubHandler((_, _) => new(HttpStatusCode.ServiceUnavailable));
            using var service = new RegionalHistoryService(new HttpClient(handler), TimeSpan.Zero);
            try { await service.GetAsync(1, "Home", false, CancellationToken.None); throw new Exception("Expected HTTP error"); }
            catch (HttpRequestException) { }
        });
        return tests;
    }
}
