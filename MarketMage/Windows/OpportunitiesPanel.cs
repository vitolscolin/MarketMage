using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketMage.Models;
using MarketMage.Services;

namespace MarketMage.Windows;

public sealed class OpportunitiesPanel : IDisposable
{
    private readonly IPlayerState player;
    private readonly OpportunityScanner scanner;
    private readonly IReadOnlyList<ItemCatalogEntry> catalog;
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<CraftingRecipe>> recipes;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> dcWorlds;
    private readonly Configuration config;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly LatestRequest<ScanSummary> request = new();
    private readonly ConcurrentQueue<(int Generation, ScanUpdate Update)> updates = new();
    private readonly OpportunityBook book = new();
    private readonly RegionalHistoryPanel regionalHistory = new();
    private ScanCoverage coverage = new(0, 0, 0, 0, 0);
    private ScanPhase phase;
    private ScanScope? scope;
    private DateTimeOffset nextScan;
    private int generation;
    private int rotation;
    private int scanned;
    private int total;
    private string status = "Log in to detect your sale world and local data center.";
    private string? selectedKey;
    private bool showCrafts = true;
    private bool showResales = true;
    private bool showHq = true;
    private string search = string.Empty;
    private bool targetedScan;
    private bool showMonitor;
    private bool showCompleted;
    private bool disposed;

    public OpportunitiesPanel(IPlayerState player, UniversalisService market, IReadOnlyList<ItemCatalogEntry> catalog,
        IReadOnlyDictionary<uint, IReadOnlyList<CraftingRecipe>> recipes, IReadOnlyDictionary<string, IReadOnlySet<string>> dcWorlds, Configuration config,
        IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.player = player;
        scanner = new OpportunityScanner(market);
        this.catalog = catalog.OrderByDescending(i => i.ItemId).ToArray();
        this.recipes = recipes;
        this.dcWorlds = dcWorlds;
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.log = log;
        config.MonitoredOpportunities ??= [];
        NormalizeSettings();
    }

    private OpportunitySettings Settings => new()
    {
        CommittedUnits = OpportunityMonitor.Commitments(config.MonitoredOpportunities, scope?.SaleWorld ?? string.Empty),
        ExpectedMarketSharePercent = config.ExpectedMarketSharePercent, MaximumCraftSaleDays = config.MaximumCraftSaleDays,
        Budget = config.GilBudget, MinimumProfit = config.MinimumProfit,
        MinimumRoi = config.MinimumRoiPercent / 100d, MinimumSales = config.MinimumSales,
        MaximumDataAge = TimeSpan.FromHours(config.MaximumAgeHours),
    };

    // Called by Dalamud's framework update. Worker tasks only enqueue immutable results.
    public void Update(bool visible)
    {
        if (disposed) return;
        if (!player.IsLoaded || player.HomeWorld.RowId == 0 || player.CurrentWorld.RowId == 0)
        {
            Suspend();
            scope = null;
            book.Clear();
            coverage = new(0, catalog.Count, 0, 0, 0);
            rotation = 0;
            status = "Log in to detect your sale world and local data center.";
            return;
        }
        var home = player.HomeWorld.Value.Name.ToString();
        var dc = player.CurrentWorld.Value.DataCenter.Value.Name.ToString();
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(dc)) { Suspend(); scope = null; return; }
        if (scope?.SaleWorld != home || scope.DataCenter != dc)
        {
            Suspend();
            book.Clear(); coverage = new(0, catalog.Count, 0, 0, 0); rotation = 0;
            // Only public worlds in the player's current DC can be buying sources.
            if (!dcWorlds.TryGetValue(dc, out var worlds))
            {
                scope = null;
                status = "The current data center has no supported public worlds.";
                return;
            }
            scope = new ScanScope(home, dc, worlds, player.HomeWorld.RowId, player.HomeWorld.Value.DataCenter.Value.Name.ToString());
            nextScan = DateTimeOffset.MinValue;
            status = "Ready to discover crafting and resale opportunities.";
        }
        if (!visible) { Suspend(); return; }
        var finished = request.TryTake(out var summary);
        while (updates.TryDequeue(out var queued))
        {
            if (queued.Generation != generation) continue;
            book.Apply(queued.Update);
            if (OpportunityMonitor.Apply(config.MonitoredOpportunities, scope, queued.Update, DateTimeOffset.UtcNow)) Save();
            coverage = queued.Update.Coverage;
            phase = queued.Update.Phase;
            scanned = queued.Update.Completed;
            total = queued.Update.Total;
            status = phase == ScanPhase.Screening
                ? $"Screening catalog prices: {scanned:N0}/{total:N0}. Detailed stock checks follow for promising items."
                : $"Checking actual listings: {scanned:N0}/{total:N0} shortlisted items. Confirmed opportunities appear as batches finish.";
        }
        if (finished)
        {
            rotation = summary!.NextOffset;
            nextScan = DateTimeOffset.UtcNow.AddMinutes(summary.Error == null ? 10 : 1);
            status = summary.Error ?? $"{(targetedScan ? "Targeted refresh" : "Round")} complete: screened {coverage.Screened:N0} items and checked {summary.Evaluated:N0} shortlisted items against listings.";
        }
        if (config.AutoScan && !request.IsRunning && DateTimeOffset.UtcNow >= nextScan) Start();
    }

    public void Suspend()
    {
        regionalHistory.Reset();
        if (request.IsRunning)
        {
            request.Cancel();
            generation++;
            nextScan = DateTimeOffset.UtcNow;
            status = "Scan paused. Existing findings expire after 15 minutes.";
        }
        while (updates.TryDequeue(out _)) { }
    }

    private void Start(uint[]? targetIds = null)
    {
        if (scope == null) return;
        if (targetIds != null) Suspend();
        if (request.IsRunning) return;
        var activeScope = scope;
        var activeSettings = Settings;
        var activeGeneration = ++generation;
        var offset = rotation;
        var priority = targetIds ?? OpportunityMonitor.Priority(config.MonitoredOpportunities, activeScope)
            .Concat(book.Current(DateTimeOffset.UtcNow, activeSettings).Select(r => r.ItemId))
            .Concat(config.SelectedItems ?? []).Distinct().ToArray();
        targetedScan = targetIds != null;
        scanned = total = 0;
        coverage = new(0, targetIds?.Length ?? catalog.Count, 0, 0, 0);
        phase = ScanPhase.Screening;
        status = targetIds == null ? "Screening the full catalog with cached market summaries..." : "Refreshing selected items and their ingredients...";
        request.Start(token => Task.Run(async () =>
        {
            try
            {
                return await scanner.ScanAsync(activeScope, catalog, recipes, priority, offset, activeSettings,
                    update => updates.Enqueue((activeGeneration, update)), token, targetIds != null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.Error(ex, "Automatic opportunity scan failed.");
                return new ScanSummary(0, 0, offset, "Scan failed; details are in the plugin log. Retry in 1 minute.");
            }
        }, token));
    }

    public void Draw()
    {
        ImGui.TextUnformatted("Find ways to make gil");
        ImGui.SameLine();
        if (ImGui.Button(showMonitor ? "Back to opportunities" : $"Monitor / checklist ({config.MonitoredOpportunities.Count(e => !e.Sold)})")) { showMonitor = !showMonitor; regionalHistory.Reset(); }
        DrawTimingAssumption();
        if (showMonitor) { DrawMonitor(); return; }
        if (scope == null) { ImGui.TextWrapped(status); return; }
        ImGui.TextWrapped($"Sell on {scope.SaleWorld} (home world) | Buy on {scope.DataCenter} (current data center)");
        var auto = config.AutoScan;
        if (ImGui.Checkbox("Automatic scanning while this view is open", ref auto))
        {
            config.AutoScan = auto;
            if (!auto) Suspend();
            Save();
        }
        ImGui.SameLine();
        if (request.IsRunning)
        {
            if (ImGui.Button("Pause scan")) { config.AutoScan = false; Suspend(); Save(); }
        }
        else if (ImGui.Button("Scan now")) Start();
        if (ImGui.CollapsingHeader("Budget and opportunity filters")) DrawSettings();
        ImGui.TextWrapped(status);
        if (request.IsRunning && total > 0) ImGui.ProgressBar(scanned / (float)total, new Vector2(-1, 0), $"{scanned}/{total}");
        else if (config.AutoScan && nextScan > DateTimeOffset.UtcNow)
            ImGui.TextDisabled($"Next round in {Math.Ceiling((nextScan - DateTimeOffset.UtcNow).TotalMinutes):N0} minutes.");
        ImGui.TextWrapped($"{(targetedScan ? "Targeted items screened" : "Catalog screened")}: {coverage.Screened:N0}/{coverage.CatalogTotal:N0} | Home sales data: {coverage.WithSalesData:N0} | Fresh home data: {coverage.FreshSalesData:N0}");
        ImGui.TextWrapped($"Source DC price data: {coverage.WithSourcePrices:N0} items | Fresh source data: {coverage.FreshSourcePrices:N0}");
        if (phase == ScanPhase.Validating)
            ImGui.TextWrapped($"Potential candidates: {coverage.Promising:N0} | Detailed checks this round: {scanned:N0}/{total:N0}. Only listing-checked plans appear below.");
        if (ImGui.CollapsingHeader("How these opportunities are calculated"))
        {
            ImGui.TextWrapped("Automatic discovery screens the full catalog using aggregate prices and estimated sales velocity. It then verifies a shortlist against actual listings. The counts distinguish coverage from data availability; screened items may have missing or stale data.");
            ImGui.TextWrapped("Profit includes full-stack purchases and assumed 5% buying/selling taxes. Sale prices are capped below the current lowest home-world listing. Travel and crafting time are excluded.");
        }
        ImGui.Checkbox("Crafting", ref showCrafts); ImGui.SameLine();
        ImGui.Checkbox("Resale", ref showResales); ImGui.SameLine();
        ImGui.Checkbox("Include HQ", ref showHq); ImGui.SameLine();
        ImGui.SetNextItemWidth(220); ImGui.InputText("Filter items", ref search, 100);
        var rows = book.Current(DateTimeOffset.UtcNow, Settings)
            .Where(r => (showCrafts || r.Kind != OpportunityKind.Craft) && (showResales || r.Kind != OpportunityKind.Resell) &&
                (showHq || !r.HighQuality) && r.ItemName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        ImGui.TextUnformatted($"{rows.Count:N0} qualifying opportunities (showing top 100). Budget applies separately to each plan.");
        if (rows.Count == 0)
        {
            ImGui.TextWrapped(request.IsRunning ? "Looking for opportunities that meet your filters..." :
                "No qualifying opportunities in the checked markets. Try the next scan or adjust your budget, minimum profit, or ROI. Missing/stale markets and insufficient sales evidence are excluded.");
            return;
        }
        if (ImGui.BeginTable("gil-opportunities", 9, TableFlags, new Vector2(0, Math.Max(160, ImGui.GetContentRegionAvail().Y * 0.45f))))
        {
            Headers("Item / quality", "Method", "Sell qty", "Buy cost", "Net gil", "ROI", "Buy worlds", "Units/day / batch time", "Checked");
            foreach (var row in rows.Take(100))
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable($"{row.ItemName} ({(row.HighQuality ? "HQ" : "NQ")})##{row.Key}", selectedKey == row.Key)) selectedKey = row.Key;
                Cell(row.Kind == OpportunityKind.Craft ? $"Craft x{row.CraftCount}" : "Buy / resell");
                Cell(row.OutputQuantity.ToString("N0")); Cell(row.Outlay.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1), $"+{row.Profit:N0}");
                Cell(row.Roi.ToString("P0")); Cell(string.Join(", ", row.Purchases.Select(p => p.World).Distinct()));
                Cell($"{row.EstimatedDailySales?.ToString("N1") ?? "Unknown"}/day | {SaleTiming.Format(SaleTiming.Days((long)row.OutputQuantity + row.CommittedUnits, row.EstimatedDailySales, config.ExpectedMarketSharePercent))}");
                Cell($"{Math.Max(0, (DateTimeOffset.UtcNow - row.CheckedAt).TotalMinutes):F0}m ago");
            }
            ImGui.EndTable();
        }
        var detail = rows.FirstOrDefault(r => r.Key == selectedKey) ?? rows[0];
        DrawPlan(detail);
    }

    private void DrawSettings()
    {
        var budget = config.GilBudget; var profit = config.MinimumProfit; var roi = config.MinimumRoiPercent;
        var sales = config.MinimumSales; var age = config.MaximumAgeHours; var days = config.MaximumCraftSaleDays;
        ImGui.SetNextItemWidth(150); var changed = ImGui.InputInt("Gil budget per plan", ref budget, 1000, 10000);
        ImGui.SetNextItemWidth(150); changed |= ImGui.InputInt("Minimum net gil", ref profit, 100, 1000);
        ImGui.SetNextItemWidth(150); changed |= ImGui.InputInt("Minimum ROI %", ref roi);
        ImGui.SetNextItemWidth(150); changed |= ImGui.InputInt("Minimum sampled sales in 7 days", ref sales);
        ImGui.SetNextItemWidth(150); changed |= ImGui.InputInt("Maximum data age (hours)", ref age);
        ImGui.SetNextItemWidth(150); changed |= ImGui.SliderInt("Maximum crafting batch sale days", ref days, 1, 7);
        if (!changed) return;
        config.GilBudget = budget; config.MinimumProfit = profit; config.MinimumRoiPercent = roi;
        config.MinimumSales = sales; config.MaximumAgeHours = age; config.MaximumCraftSaleDays = days;
        NormalizeSettings(); Suspend(); book.Clear(); nextScan = DateTimeOffset.UtcNow.AddSeconds(2); Save();
    }

    private void DrawPlan(GilOpportunity row)
    {
        regionalHistory.Draw(row);
        ImGui.Separator();
        ImGui.TextWrapped("Tracking reserves this batch against future crafting demand. Set committed quantity to zero in the monitor to watch without reserving.");
        if (ImGui.Button("Refresh this item now")) Start([row.ItemId]);
        var tracked = config.MonitoredOpportunities.Any(e => !e.Sold && OpportunityMonitor.Matches(e, row, scope!.DataCenter));
        ImGui.BeginDisabled(tracked || config.MonitoredOpportunities.Count(e => !e.Sold) >= OpportunityMonitor.MaximumActive);
        if (ImGui.Button(tracked ? "On monitor list" : "Track this opportunity"))
        {
            OpportunityMonitor.Add(config.MonitoredOpportunities, row, scope!.DataCenter, DateTimeOffset.UtcNow);
            CommitmentsChanged();
        }
        ImGui.EndDisabled();
        if (!tracked && config.MonitoredOpportunities.Count(e => !e.Sold) >= OpportunityMonitor.MaximumActive)
            ImGui.TextDisabled("Monitor full: complete or remove an active plan to track another.");
        ImGui.TextWrapped(row.UsesSampledDemand ? "Demand source: sampled home-world sales divided by seven days; incomplete history can understate demand." : "Demand source: home-world aggregate sales velocity for this quality.");
        ImGui.TextWrapped($"Estimated sale time including committed units: {SaleTiming.Format(SaleTiming.Days((long)row.OutputQuantity + row.CommittedUnits, row.EstimatedDailySales, config.ExpectedMarketSharePercent))} at {config.ExpectedMarketSharePercent}% of observed market demand. This is a scenario, not a sell-through guarantee.");
        ImGui.TextWrapped($"{row.ItemName}: sell {row.OutputQuantity:N0} {(row.HighQuality ? "HQ" : "NQ")} at an estimated {row.SalePrice:N0} gil each on {row.SaleWorld}.");
        DrawComparisons(row);
        DrawPriceScenarios(row);
        ImGui.TextWrapped($"Already committed and unsold: {row.CommittedUnits:N0} units. Demand allowance includes those units before any new batch.");
        if (row.Kind == OpportunityKind.Craft)
            ImGui.TextWrapped($"Batch sizing: {row.CraftCount} crafts x {row.OutputQuantity / row.CraftCount} yield = {row.OutputQuantity} units; within {config.MaximumCraftSaleDays} sale days at your assumed share. Estimated net gil per sale day: {OpportunityEngine.DailyProfit(row, Settings):N0} (one-day minimum). Compared 1–10 crafts; do not repeat this batch before checking demand again. Price drops can reduce the displayed ROI.");
        if (row.Kind == OpportunityKind.Craft)
            ImGui.TextWrapped($"Craft {row.CraftCount} times using recipe {row.RecipeId} ({row.CraftJob}, level {row.CraftLevel}). Buy the stacks below; leftover materials are valued at zero in this plan. Check recipe access and gear; HQ plans require HQ outputs.");
        else ImGui.TextWrapped("Buy the listed stack below, then list it on your home world. The estimated profit assumes all units sell at the displayed price.");
        ImGui.TextWrapped($"Revenue after sale tax: {row.Revenue:N0} − full purchase cost: {row.Outlay:N0} = estimated net gil: {row.Profit:N0}. Last sampled sale: {row.LastSale.LocalDateTime:g}; oldest source: {row.OldestMarketData.LocalDateTime:g}.");
        ImGui.TextWrapped($"Listing verification used {row.SampleSales} sampled sales / {row.SampleUnits:N0} units in 7 days. Units/day uses the demand source labeled above, not guaranteed demand. Recheck listings before buying; plans can compete for the same stock.");
        if (!ImGui.BeginTable("shopping-plan", 5, TableFlags, new Vector2(0, 150))) return;
        Headers("Buy item", "World", "Whole stack qty", "Unit price", "Cost incl. tax");
        foreach (var step in row.Purchases)
        {
            ImGui.TableNextRow(); Cell(step.ItemName); Cell(step.World); Cell(step.Quantity.ToString("N0"));
            Cell(step.UnitPrice.ToString("N0")); Cell(step.CostWithTax.ToString("N0"));
        }
        ImGui.EndTable();
    }

    private void DrawTimingAssumption()
    {
        var share = config.ExpectedMarketSharePercent;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Your assumed share of daily sales (%)", ref share, 1, 100))
        { config.ExpectedMarketSharePercent = share; Suspend(); book.Clear(); nextScan = DateTimeOffset.UtcNow.AddSeconds(2); Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Batch quantity / (world units per day x your assumed share). Competing sellers and price changes can make actual sales slower. 25% is an editable scenario, not a measured share.");
    }

    private void DrawMonitor()
    {
        ImGui.TextWrapped("Saved plans and checklist progress stay here even when an opportunity disappears. Checkboxes are your manual records; scans do not detect purchases or sales. Up to 50 active plans are prioritized during scans on their saved world/DC.");
        ImGui.TextWrapped(status);
        ImGui.Checkbox("Show sold plans", ref showCompleted);
        if (scope != null)
        {
            ImGui.SameLine(); ImGui.BeginDisabled(!OpportunityMonitor.Priority(config.MonitoredOpportunities, scope).Any());
            if (ImGui.Button("Refresh tracked items now")) Start(OpportunityMonitor.Priority(config.MonitoredOpportunities, scope).ToArray());
            ImGui.EndDisabled();
        }
        if (config.MonitoredOpportunities.Count == 0) ImGui.TextWrapped("Select an opportunity and click Track this opportunity to save it here.");
        foreach (var entry in config.MonitoredOpportunities.Where(e => showCompleted || !e.Sold).ToArray())
        {
            var plan = entry.SavedPlan;
            ImGui.PushID(entry.Id);
            if (ImGui.CollapsingHeader($"{plan.ItemName} ({(plan.HighQuality ? "HQ" : "NQ")}) — {plan.Kind} — {plan.SaleWorld} / {entry.SourceDataCenter}##plan"))
            {
                ImGui.TextWrapped($"{OpportunityMonitor.Status(entry, DateTimeOffset.UtcNow)} | Last check: {entry.LastCheckedAt?.LocalDateTime.ToString("g") ?? "Never"}");
                if (scope == null || entry.SourceDataCenter != scope.DataCenter || plan.SaleWorld != scope.SaleWorld)
                    ImGui.TextWrapped("Monitoring pauses for this entry until your sale world and source DC match its saved scope.");
                ImGui.TextWrapped($"Saved plan: {plan.OutputQuantity:N0} output units, cost {plan.Outlay:N0}, estimated profit {plan.Profit:N0} gil. Saved {entry.SavedAt.LocalDateTime:g}.");
                if (scope != null && entry.SourceDataCenter == scope.DataCenter && plan.SaleWorld == scope.SaleWorld &&
                    ImGui.Button("Refresh this tracked item now")) Start([plan.ItemId]);
                var committed = entry.CommittedQuantity ?? plan.OutputQuantity;
                var produced = entry.CraftedQuantity; var listedUnits = entry.ListedQuantity; var soldUnits = entry.SoldQuantity;
                ImGui.TextWrapped("Quantities are cumulative and overlap: listed units are included in produced/committed units. Unsold = greatest of these quantities minus units sold. Tracking reserves the saved batch by default; set committed to zero for interest only.");
                var quantitiesChanged = ImGui.InputInt("Committed output units", ref committed);
                quantitiesChanged |= ImGui.InputInt("Crafted / acquired output units", ref produced);
                quantitiesChanged |= ImGui.InputInt("Listed output units", ref listedUnits);
                quantitiesChanged |= ImGui.InputInt("Sold output units", ref soldUnits);
                if (quantitiesChanged)
                {
                    entry.CommittedQuantity = Math.Clamp(committed, 0, 999999);
                    entry.CraftedQuantity = Math.Clamp(produced, 0, 999999);
                    entry.ListedQuantity = Math.Clamp(listedUnits, 0, 999999);
                    entry.SoldQuantity = Math.Clamp(soldUnits, 0, Math.Max(entry.CommittedQuantity.Value, Math.Max(entry.CraftedQuantity, entry.ListedQuantity)));
                    CommitmentsChanged();
                }
                ImGui.TextWrapped($"Reserved unsold units: {entry.UnsoldQuantity:N0}. Completion releases this reservation.");
                DrawPriceScenarios(plan);
                var latest = entry.LatestPlan;
                var rate = latest?.EstimatedDailySales ?? plan.EstimatedDailySales;
                ImGui.TextWrapped($"Remaining unsold sale-time scenario: {SaleTiming.Format(SaleTiming.Days(entry.UnsoldQuantity, rate, config.ExpectedMarketSharePercent))} at {config.ExpectedMarketSharePercent}% share; {rate?.ToString("N1") ?? "unknown"} units/day (latest qualifying estimate, otherwise saved data).");
                if (latest != null) ImGui.TextWrapped($"Latest qualifying additional batch: {latest.OutputQuantity:N0} units, cost {latest.Outlay:N0}, profit {latest.Profit:N0}. Your saved plan and checkboxes have not changed.");
                for (var i = 0; i < plan.Purchases.Count; i++)
                {
                    var step = plan.Purchases[i];
                    var bought = entry.PurchasedSteps.Contains(i);
                    if (ImGui.Checkbox($"Bought {step.Quantity:N0} x {step.ItemName} on {step.World} ({step.CostWithTax:N0} gil)##buy{i}", ref bought))
                    { if (bought) entry.PurchasedSteps.Add(i); else entry.PurchasedSteps.Remove(i); Save(); }
                }
                if (plan.Kind == OpportunityKind.Craft)
                {
                    var crafted = entry.Crafted;
                    if (ImGui.Checkbox($"Crafted {plan.CraftCount} batches", ref crafted)) { entry.Crafted = crafted; Save(); }
                }
                var listed = entry.Listed; var sold = entry.Sold;
                if (ImGui.Checkbox("Listed for sale", ref listed)) { entry.Listed = listed; Save(); }
                ImGui.SameLine();
                var cannotReopen = sold && (config.MonitoredOpportunities.Count(e => !e.Sold) >= OpportunityMonitor.MaximumActive ||
                    config.MonitoredOpportunities.Any(e => !e.Sold && e.Id != entry.Id && OpportunityMonitor.Matches(e, plan, entry.SourceDataCenter)));
                ImGui.BeginDisabled(cannotReopen);
                if (ImGui.Checkbox("Sold / completed", ref sold)) { entry.Sold = sold; CommitmentsChanged(); }
                ImGui.EndDisabled();
                if (cannotReopen) ImGui.TextDisabled("To reopen, free an active slot and remove any active duplicate of this plan.");
                var notes = entry.Notes;
                if (ImGui.InputText("Notes", ref notes, 500)) { entry.Notes = notes; Save(); }
                if (ImGui.Button("Remove saved plan")) { config.MonitoredOpportunities.Remove(entry); CommitmentsChanged(); }
            }
            ImGui.PopID();
        }
    }

    private void CommitmentsChanged()
    {
        Suspend(); book.Clear();
        foreach (var entry in config.MonitoredOpportunities) { entry.LastCheckedAt = null; entry.LatestPlan = null; }
        nextScan = DateTimeOffset.UtcNow.AddSeconds(2); Save();
    }

    private static void DrawPriceScenarios(GilOpportunity row)
    {
        if (!ImGui.CollapsingHeader("Break-even and price-drop scenarios")) return;
        ImGui.TextWrapped($"Break-even sale price per unit: {PriceScenarios.BreakEven(row)?.ToString("N0") ?? "Unknown"} gil, including assumed 5% sale tax and the full purchase cost. Assumes every unit sells; sale time is not predicted for price changes.");
        if (!ImGui.BeginTable("price-scenarios", 3, TableFlags, new Vector2(0, 125))) return;
        Headers("Price drop", "Sale price / unit", "Net gil");
        foreach (var drop in new[] { 0, 5, 10, 20 })
        {
            ImGui.TableNextRow(); Cell($"{drop}%"); Cell(PriceScenarios.SalePrice(row, drop).ToString("N0")); Cell(PriceScenarios.Profit(row, drop).ToString("N0"));
        }
        ImGui.EndTable();
    }

    private static void DrawComparisons(GilOpportunity row)
    {
        if (row.BatchComparisons.Count == 0 || !ImGui.CollapsingHeader("Compare crafting batches")) return;
        ImGui.TextWrapped("Batches from the checked recipes and listings. Unknown costs mean missing stock/data or an ingredient basket beyond your budget. Sale time includes existing unsold commitments. Alternatives are comparisons, not additional recommendations to combine.");
        if (!ImGui.BeginTable("batch-comparisons", 8, TableFlags, new Vector2(0, 200))) return;
        Headers("Recipe", "Crafts", "Output", "Cost", "Net gil", "ROI", "Sale time", "Result");
        foreach (var batch in row.BatchComparisons)
        {
            ImGui.TableNextRow(); Cell(batch.RecipeId.ToString()); Cell(batch.Crafts.ToString()); Cell(batch.Quantity.ToString());
            Cell(batch.Cost?.ToString("N0") ?? "Unknown"); Cell(batch.Profit?.ToString("N0") ?? "Unknown"); Cell(batch.Roi?.ToString("P0") ?? "Unknown"); Cell(SaleTiming.Format(batch.Days));
            Cell(row.Kind == OpportunityKind.Craft && batch.RecipeId == row.RecipeId && batch.Crafts == row.CraftCount ? "Recommended" : batch.Status);
        }
        ImGui.EndTable();
    }

    private void NormalizeSettings()
    {
        config.MaximumCraftSaleDays = Math.Clamp(config.MaximumCraftSaleDays, 1, 7);
        config.ExpectedMarketSharePercent = Math.Clamp(config.ExpectedMarketSharePercent, 1, 100);
        config.GilBudget = Math.Clamp(config.GilBudget, 1, 999_999_999);
        config.MinimumProfit = Math.Clamp(config.MinimumProfit, 1, 999_999_999);
        config.MinimumRoiPercent = Math.Clamp(config.MinimumRoiPercent, 0, 10000);
        config.MinimumSales = Math.Clamp(config.MinimumSales, 1, 20);
        config.MaximumAgeHours = Math.Clamp(config.MaximumAgeHours, 1, 168);
    }
    private void Save() => pluginInterface.SavePluginConfig(config);
    public void Dispose() { disposed = true; Suspend(); request.Dispose(); regionalHistory.Dispose(); }
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
    private static void Headers(params string[] names) { foreach (var name in names) ImGui.TableSetupColumn(name); ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow(); }
    private static void Cell(string value) { ImGui.TableNextColumn(); ImGui.TextUnformatted(value); }
}
