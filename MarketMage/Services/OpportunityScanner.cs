using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketMage.Models;

namespace MarketMage.Services;

public sealed record ScanScope(string SaleWorld, string DataCenter, IReadOnlySet<string> SourceWorlds,
    uint SaleWorldId = 0, string? SaleDataCenter = null);
public enum ScanPhase { Screening, Validating }
public sealed record ScanCoverage(int Screened, int CatalogTotal, int WithSalesData, int FreshSalesData, int Promising, int WithSourcePrices = 0, int FreshSourcePrices = 0);
public sealed record ScanUpdate(IReadOnlyList<uint> ItemIds, IReadOnlyList<GilOpportunity> Opportunities, int Completed, int Total)
{
    public ScanPhase Phase { get; init; } = ScanPhase.Validating;
    public ScanCoverage Coverage { get; init; } = new(0, 0, 0, 0, 0);
}
public sealed record ScanSummary(int Evaluated, int Total, int NextOffset, string? Error);

public sealed class OpportunityScanner(IMarketDataClient market)
{
    public const int ScreeningBatchSize = 100;
    public const int BatchSize = 25;
    public const int TopCandidates = 150;
    public const int RotatingCandidates = 50;
    private readonly AggregateCache cache = new();

    // Keep the strongest signals and rotate some lower-ranked ones so recurring false positives cannot monopolize validation.
    public static uint[] PlanValidation(IReadOnlyList<ScreenCandidate> ranked, IEnumerable<uint> priority,
        IReadOnlySet<uint> validIds, int offset)
    {
        var remaining = ranked.Skip(TopCandidates).ToArray();
        var rotation = Enumerable.Range(0, Math.Min(RotatingCandidates, remaining.Length))
            .Select(i => remaining[(Math.Max(0, offset) + i) % remaining.Length].ItemId);
        return priority.Where(validIds.Contains).Distinct().Take(50)
            .Concat(ranked.Take(TopCandidates).Select(c => c.ItemId)).Concat(rotation).Distinct().ToArray();
    }

    public async Task<ScanSummary> ScanAsync(ScanScope scope, IReadOnlyList<ItemCatalogEntry> catalog,
        IReadOnlyDictionary<uint, IReadOnlyList<CraftingRecipe>> recipes, IEnumerable<uint> priority,
        int offset, OpportunitySettings settings, Action<ScanUpdate> report, CancellationToken token, bool targeted = false)
    {
        var completed = 0;
        var total = 0;
        try
        {
            cache.RemoveExpired(DateTimeOffset.UtcNow);
            var home = new Dictionary<uint, AggregateSnapshot?>();
            var source = new Dictionary<uint, AggregateSnapshot?>();
            var sameDc = scope.SaleDataCenter == null || scope.SaleDataCenter == scope.DataCenter;
            var allIds = catalog.Select(i => i.ItemId).Distinct().ToArray();
            if (targeted) allIds = allIds.Intersect(priority).ToArray();
            var priorityIds = priority.Distinct().ToArray();
            var ordered = priorityIds.Where(allIds.Contains).Concat(allIds).Distinct().ToArray();
            var coverage = new ScanCoverage(0, allIds.Length, 0, 0, 0);
            report(new([], [], 0, allIds.Length) { Phase = ScanPhase.Screening, Coverage = coverage });
            foreach (var batch in ordered.Chunk(ScreeningBatchSize))
            {
                token.ThrowIfCancellationRequested();
                foreach (var pair in await GetAggregatesAsync(scope.SaleWorld, batch, token, targeted).ConfigureAwait(false)) home[pair.Key] = pair.Value;
                if (!sameDc && !targeted)
                    foreach (var pair in await GetAggregatesAsync(scope.DataCenter, batch, token).ConfigureAwait(false)) source[pair.Key] = pair.Value;
                var now = DateTimeOffset.UtcNow;
                coverage = new ScanCoverage(home.Count, allIds.Length, home.Values.Count(v => v?.HasSaleData == true),
                    home.Values.Count(v => AggregateScreener.HasFreshSales(v, scope.SaleWorldId, settings, now)), 0,
                    (sameDc ? home : source).Values.Count(v => v?.Nq.DcMinimum is > 0 || v?.Hq.DcMinimum is > 0),
                    (sameDc ? home : source).Values.Count(v => AggregateScreener.HasFreshSource(v, settings, now)));
                token.ThrowIfCancellationRequested();
                report(new([], [], home.Count, allIds.Length) { Phase = ScanPhase.Screening, Coverage = coverage });
            }
            if (sameDc) source = home;
            token.ThrowIfCancellationRequested();
            var ranked = targeted ? Array.Empty<ScreenCandidate>() : AggregateScreener.Rank(catalog, recipes, home, source, scope, settings, DateTimeOffset.UtcNow);
            var ids = targeted ? allIds : PlanValidation(ranked, priorityIds, allIds.ToHashSet(), offset);
            var names = catalog.ToDictionary(i => i.ItemId);
            coverage = coverage with { Promising = ranked.Count };
            total = ids.Length;
            report(new([], [], 0, total) { Coverage = coverage });
            var ingredientCache = new Dictionary<uint, MarketPriceSnapshot>();
            foreach (var batch in ids.Chunk(BatchSize))
            {
                token.ThrowIfCancellationRequested();
                var nq = (await market.GetSnapshotsAsync(scope.SaleWorld, batch, false, true, token, true).ConfigureAwait(false)).ToDictionary(s => s.ItemId);
                var hqIds = batch.Where(id => names[id].CanBeHq).ToArray();
                var hq = (await market.GetSnapshotsAsync(scope.SaleWorld, hqIds, true, true, token, true).ConfigureAwait(false)).ToDictionary(s => s.ItemId);
                var dcNq = SourceOnly(await market.GetSnapshotsAsync(scope.DataCenter, batch, false, true, token).ConfigureAwait(false), scope);
                var dcHq = SourceOnly(await market.GetSnapshotsAsync(scope.DataCenter, hqIds, true, true, token).ConfigureAwait(false), scope);
                foreach (var pair in dcNq) ingredientCache[pair.Key] = pair.Value;
                var ingredientIds = batch.Where(recipes.ContainsKey).SelectMany(id => recipes[id]).SelectMany(r => r.Ingredients)
                    .Select(i => i.ItemId).Distinct().Where(id => !ingredientCache.ContainsKey(id)).ToArray();
                foreach (var pair in SourceOnly(await market.GetSnapshotsAsync(scope.DataCenter, ingredientIds, false, true, token).ConfigureAwait(false), scope))
                    ingredientCache[pair.Key] = pair.Value;
                var rows = new List<GilOpportunity>();
                var now = DateTimeOffset.UtcNow;
                foreach (var id in batch)
                {
                    token.ThrowIfCancellationRequested();
                    var options = recipes.GetValueOrDefault(id) ?? [];
                    var aggregate = home.GetValueOrDefault(id);
                    var freshDemand = AggregateScreener.HasFreshSales(aggregate, scope.SaleWorldId, settings, now);
                    if (nq.TryGetValue(id, out var normal))
                        rows.AddRange(OpportunityEngine.Evaluate(names[id], false, scope.SaleWorld, normal, dcNq.GetValueOrDefault(id), options, ingredientCache, settings, now, freshDemand ? aggregate?.Nq.WorldDailySales : null));
                    if (hq.TryGetValue(id, out var high))
                        rows.AddRange(OpportunityEngine.Evaluate(names[id], true, scope.SaleWorld, high, dcHq.GetValueOrDefault(id), options, ingredientCache, settings, now, freshDemand ? aggregate?.Hq.WorldDailySales : null));
                }
                completed += batch.Length;
                token.ThrowIfCancellationRequested();
                report(new(batch, rows, completed, total) { Coverage = coverage });
            }
            var remainder = Math.Max(0, ranked.Count - TopCandidates);
            var nextOffset = targeted ? offset : remainder == 0 ? 0 : (offset + Math.Min(RotatingCandidates, remainder)) % remainder;
            return new ScanSummary(completed, total, nextOffset, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return new ScanSummary(completed, total, offset, "Market data unavailable. Coverage is partial; cached screening resumes on retry. Existing opportunities still expire. Automatic retry in 1 minute.");
        }
    }

    private async Task<IReadOnlyDictionary<uint, AggregateSnapshot?>> GetAggregatesAsync(string scope, uint[] ids, CancellationToken token, bool force = false)
    {
        var values = new Dictionary<uint, AggregateSnapshot?>();
        var missing = new List<uint>();
        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids)
            if (!force && cache.TryGet(scope, id, now, out var snapshot)) values[id] = snapshot;
            else missing.Add(id);
        if (missing.Count > 0)
        {
            var fetched = await market.GetAggregatesAsync(scope, missing, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var id in missing)
            {
                var snapshot = fetched.GetValueOrDefault(id);
                cache.Put(scope, id, snapshot, DateTimeOffset.UtcNow);
                values[id] = snapshot;
            }
        }
        return values;
    }

    private static Dictionary<uint, MarketPriceSnapshot> SourceOnly(IReadOnlyList<MarketPriceSnapshot> snapshots, ScanScope scope) =>
        snapshots.ToDictionary(s => s.ItemId, s => new MarketPriceSnapshot
        { ItemId = s.ItemId, Listings = s.Listings.Where(l => scope.SourceWorlds.Contains(l.World)).ToList() });
}
