using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarketMage.Models;
using MarketMage.Services;

namespace MarketMage.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly OpportunitiesPanel opportunities;
    private bool manualMode;
    private const int MaxSelection = 50;
    private readonly UniversalisService market = new();
    private readonly RecipeService recipes;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly IReadOnlyList<ItemCatalogEntry> catalog;
    private readonly HashSet<uint> craftable;
    private readonly List<WorldEntry> worlds;
    private readonly HashSet<uint> selected;
    private readonly LatestRequest<RefreshResult> refresh = new();
    private string search = string.Empty;
    private string status = "Ready. Select items and refresh.";
    private string resultContext = string.Empty;
    private bool sortById;
    private uint? detailItem;
    private IReadOnlyList<ProfitEstimate> estimates = [];
    private ItemCatalogEntry[] visibleCatalog = [];
    private bool catalogDirty = true;
    private bool disposed;
    private sealed record WorldEntry(string Name, string DataCenter, string Region);
    private sealed record RefreshResult(IReadOnlyList<ProfitEstimate> Estimates, string Context, string? Error);

    public MainWindow(IDataManager data, IPluginLog log, IDalamudPluginInterface pluginInterface, IPlayerState playerState) : base("MarketMage")
    {
        this.log = log;
        this.pluginInterface = pluginInterface;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        recipes = new RecipeService(data);
        catalog = new ItemCatalogService(data).GetItemCatalog();
        craftable = recipes.GetCraftableItemIds();
        worlds = data.GetExcelSheet<World>().Where(w => w.IsPublic && w.DataCenter.RowId > 0)
            .Select(w => new WorldEntry(w.Name.ToString(), w.DataCenter.Value.Name.ToString(), Region(w.DataCenter.Value.Region.RowId)))
            .Where(w => !string.IsNullOrWhiteSpace(w.Name) && !string.IsNullOrWhiteSpace(w.DataCenter))
            .OrderBy(w => w.Region).ThenBy(w => w.DataCenter).ThenBy(w => w.Name).ToList();
        if (!worlds.Any(w => w.Name == config.World)) config.World = worlds.FirstOrDefault()?.Name ?? string.Empty;
        var validIds = catalog.Select(i => i.ItemId).ToHashSet();
        selected = (config.SelectedItems ?? []).Where(validIds.Contains).Take(MaxSelection).ToHashSet();
        opportunities = new OpportunitiesPanel(playerState, market, catalog, recipes.GetRecipeOptions(validIds),
            worlds.GroupBy(w => w.DataCenter).ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(w => w.Name).ToHashSet()), config, pluginInterface, log);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(1050, 650), MaximumSize = new Vector2(float.MaxValue) };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        opportunities.Dispose();
        refresh.Dispose();
        market.Dispose();
    }

    public void UpdateOpportunities() => opportunities.Update(IsOpen && !manualMode);

    public override void OnClose()
    {
        opportunities.Suspend();
        refresh.Cancel();
    }

    public override void Draw()
    {
        var previousMode = manualMode;
        if (ImGui.RadioButton("Find gil opportunities", !manualMode)) manualMode = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Manual comparison", manualMode)) manualMode = true;
        if (previousMode != manualMode)
        {
            if (manualMode) opportunities.Suspend();
            else refresh.Cancel();
        }
        ImGui.Separator();
        if (!manualMode) { opportunities.Draw(); return; }
        DrawManual();
    }

    private void DrawManual()
    {
        if (refresh.TryTake(out var completed))
        {
            estimates = completed.Estimates;
            resultContext = completed.Context;
            detailItem = estimates.FirstOrDefault()?.ItemId;
            status = completed.Error ?? $"Loaded {estimates.Count} items. {estimates.Count(e => e.IsCraftable && !e.HasCompleteCost)} local costs incomplete.";
        }
        ImGui.SetNextItemWidth(230);
        if (ImGui.BeginCombo("Sale world", config.World))
        {
            foreach (var group in worlds.GroupBy(w => $"{w.Region} / {w.DataCenter}"))
            {
                ImGui.TextDisabled(group.Key);
                foreach (var world in group)
                    if (ImGui.Selectable(world.Name, config.World == world.Name))
                    { config.World = world.Name; Invalidate(); Save(); }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var hq = config.HighQuality;
        if (ImGui.Checkbox("HQ output", ref hq)) { config.HighQuality = hq; Invalidate(); Save(); }
        ImGui.SameLine();
        var dc = config.CompareDataCenter;
        if (ImGui.Checkbox("Compare data center", ref dc)) { config.CompareDataCenter = dc; Invalidate(); Save(); }
        ImGui.SameLine();
        ImGui.BeginDisabled(refresh.IsRunning || selected.Count == 0 || worlds.Count == 0);
        if (ImGui.Button("Refresh")) StartRefresh();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(refresh.IsRunning ? "Cancel" : "Clear"))
        {
            if (refresh.IsRunning) Invalidate();
            else { selected.Clear(); Invalidate(); Save(); }
        }
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("Search name or ID", ref search, 128)) catalogDirty = true;
        ImGui.SameLine();
        var onlyCraftable = config.CraftableOnly;
        if (ImGui.Checkbox("Craftable only", ref onlyCraftable)) { config.CraftableOnly = onlyCraftable; catalogDirty = true; Save(); }
        ImGui.SameLine();
        if (ImGui.Checkbox("Sort by ID", ref sortById)) catalogDirty = true;
        ImGui.TextWrapped($"{status} Selected: {selected.Count}/{MaxSelection} (saved watchlist).");
        DrawCatalog();
        if (estimates.Count == 0) return;
        ImGui.TextWrapped(resultContext);
        ImGui.TextWrapped("Sale = median of up to 20 sampled transactions, not sales/day. Revenue assumes 5% sale tax. Ingredients: NQ listings, up to 100 per item/scope; partial-stack value, excluding purchase tax and travel. Actual stack outlay may be higher.");
        DrawResults();
        DrawDetails();
    }

    private void StartRefresh()
    {
        // Snapshot all game data on the UI thread before starting asynchronous network work.
        var world = worlds.FirstOrDefault(w => w.Name == config.World);
        if (world == null || selected.Count == 0) return;
        var ids = selected.Order().ToArray();
        var recipeMap = recipes.GetRecipesForItems(ids);
        var names = catalog.Where(i => selected.Contains(i.ItemId)).ToDictionary(i => i.ItemId, i => i.Name);
        var hq = config.HighQuality;
        var compare = config.CompareDataCenter;
        estimates = [];
        status = $"Refreshing {world.Name}...";
        refresh.Start(token => FetchAsync(world, ids, recipeMap, names, hq, compare, token));
    }

    private async Task<RefreshResult> FetchAsync(WorldEntry world, uint[] ids, IReadOnlyDictionary<uint, CraftingRecipe> recipeMap,
        Dictionary<uint, string> names, bool hq, bool compare, CancellationToken token)
    {
        try
        {
            var sales = await market.GetSnapshotsAsync(world.Name, ids, hq, false, token).ConfigureAwait(false);
            var ingredientIds = recipeMap.Values.SelectMany(r => r.Ingredients).Select(i => i.ItemId).Distinct().ToArray();
            var local = (await market.GetSnapshotsAsync(world.Name, ingredientIds, false, true, token).ConfigureAwait(false)).ToDictionary(s => s.ItemId);
            var dc = compare
                ? (await market.GetSnapshotsAsync(world.DataCenter, ingredientIds, false, true, token).ConfigureAwait(false)).ToDictionary(s => s.ItemId)
                : new Dictionary<uint, MarketPriceSnapshot>();
            // Include separately fetched local stock in the comparison without double-counting its listings.
            if (compare)
                foreach (var pair in local)
                    dc[pair.Key] = new MarketPriceSnapshot { ItemId = pair.Key, Listings =
                        (dc.GetValueOrDefault(pair.Key)?.Listings ?? []).Where(l => l.World != world.Name).Concat(pair.Value.Listings).ToList() };
            var results = sales.Select(s => ProfitCalculator.Build(s, names[s.ItemId], recipeMap.GetValueOrDefault(s.ItemId), local, dc))
                .OrderByDescending(e => e.Profit ?? long.MinValue).ToList();
            return new RefreshResult(results, $"Sale world: {world.Name} | Output: {(hq ? "HQ" : "NQ")} | DC comparison: {(compare ? world.DataCenter : "off")} | Retrieved: {DateTimeOffset.Now:g}", null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new RefreshResult([], "", "Refresh canceled."); }
        catch (Exception ex)
        {
            log.Error(ex, "MarketMage refresh failed.");
            return new RefreshResult([], "", "Refresh failed. Check the connection and try again; details are in the plugin log.");
        }
    }

    private void Invalidate()
    {
        refresh.Cancel();
        estimates = [];
        detailItem = null;
        resultContext = string.Empty;
        status = "Selection changed or request canceled. Refresh to load prices.";
    }
    private void Save()
    {
        config.SelectedItems = selected.Order().ToList();
        pluginInterface.SavePluginConfig(config);
    }
    private void Toggle(uint id)
    {
        if (!selected.Remove(id) && selected.Count < MaxSelection) selected.Add(id);
        Invalidate();
        Save();
    }
    private void DrawCatalog()
    {
        if (catalogDirty)
        {
            var query = search.Trim();
            var filtered = catalog.Where(i => (!config.CraftableOnly || craftable.Contains(i.ItemId)) &&
                (query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.ItemId.ToString() == query));
            visibleCatalog = (sortById ? filtered.OrderBy(i => i.ItemId) : filtered.OrderBy(i => i.Name)).ToArray();
            catalogDirty = false;
        }
        ImGui.TextDisabled($"Showing {Math.Min(250, visibleCatalog.Length):N0} of {visibleCatalog.Length:N0} matches. Narrow your search to see more.");
        if (!ImGui.BeginTable("catalog", 3, TableFlags, new Vector2(0, 170))) return;
        Headers("Use", "Item", "ID");
        foreach (var item in visibleCatalog.Take(250))
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var enabled = selected.Contains(item.ItemId);
            ImGui.BeginDisabled(!enabled && selected.Count >= MaxSelection);
            if (ImGui.Checkbox($"##{item.ItemId}", ref enabled)) Toggle(item.ItemId);
            ImGui.EndDisabled();
            Cell(item.Name); Cell(item.ItemId.ToString());
        }
        ImGui.EndTable();
    }
    private void DrawResults()
    {
        if (!ImGui.BeginTable("results", 10, TableFlags, new Vector2(0, 180))) return;
        Headers("Item", "Sale", "Local cost", "DC cost", "Revenue", "Local profit", "DC profit", "ROI", "Sample", "Last sale / upload");
        foreach (var e in estimates)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.Selectable($"{e.ItemName}##result{e.ItemId}", detailItem == e.ItemId)) detailItem = e.ItemId;
            Cell(e.EstimatedSalePrice > 0 ? e.EstimatedSalePrice.ToString("N0") : "No sales");
            Cell(e.IsCraftable ? Money(e.EstimatedMaterialCost) : "Not craftable");
            Cell(config.CompareDataCenter ? Money(e.DataCenterMaterialCost) : "Off");
            Cell(Money(e.AdjustedRevenue)); Cell(Money(e.Profit));
            Cell(config.CompareDataCenter ? Money(e.DataCenterProfit) : "Off");
            Cell(e.Roi?.ToString("P0") ?? "N/A"); Cell(e.RecentSalesCount.ToString());
            Cell($"{Age(e.LastSaleTime)} / {Age(e.UploadedAt)}");
        }
        ImGui.EndTable();
    }
    private void DrawDetails()
    {
        var e = estimates.FirstOrDefault(e => e.ItemId == detailItem);
        if (e == null) return;
        ImGui.TextWrapped($"{e.ItemName} — yield {e.RecipeYield}. DC chooses the cheapest sampled world with enough stock for EACH ingredient; multiple worlds may be required. Unknown or >24h ages need checking.");
        if (!e.IsCraftable) { ImGui.TextUnformatted("No crafting recipe found."); return; }
        if (!ImGui.BeginTable("ingredients", 8, TableFlags, new Vector2(0, 160))) return;
        Headers("Ingredient", "Qty", "Local total", "Local stock / age", "DC total", "DC world", "DC stock", "DC age");
        for (var i = 0; i < e.IngredientCosts.Count; i++)
        {
            var local = e.IngredientCosts[i]; var dc = e.DataCenterIngredientCosts[i];
            ImGui.TableNextRow(); Cell(local.ItemName); Cell(local.Quantity.ToString());
            Cell(local.HasPrice ? local.TotalPrice.ToString("N0") : "Insufficient");
            Cell($"{local.AvailableQuantity:N0} / {Age(local.ReviewedAt)}");
            Cell(!config.CompareDataCenter ? "Off" : dc.HasPrice ? dc.TotalPrice.ToString("N0") : "Insufficient");
            Cell(dc.SourceWorld); Cell(config.CompareDataCenter ? dc.AvailableQuantity.ToString("N0") : "—");
            Cell(config.CompareDataCenter ? Age(dc.ReviewedAt) : "—");
        }
        ImGui.EndTable();
    }
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
    private static void Headers(params string[] names) { foreach (var name in names) ImGui.TableSetupColumn(name); ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow(); }
    private static void Cell(string text) { ImGui.TableNextColumn(); ImGui.TextUnformatted(text); }
    private static string Money(long? value) => value?.ToString("N0") ?? "N/A";
    private static string Age(DateTimeOffset? time)
    {
        if (!time.HasValue) return "Unknown";
        var age = DateTimeOffset.UtcNow - time.Value;
        if (age < TimeSpan.Zero) return "Clock mismatch";
        return age.TotalHours >= 24 ? $"{age.TotalDays:F1}d (stale)" : age.TotalHours >= 1 ? $"{age.TotalHours:F0}h" : $"{age.TotalMinutes:F0}m";
    }
    private static string Region(uint region) => region switch { 1 => "Japan", 2 => "North America", 3 => "Europe", 4 => "Oceania", _ => $"Region {region}" };
}
