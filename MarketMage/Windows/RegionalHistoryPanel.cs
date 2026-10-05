using System;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using MarketMage.Models;
using MarketMage.Services;

namespace MarketMage.Windows;

public sealed class RegionalHistoryPanel : IDisposable
{
    private sealed record Result(RegionalHistory? History, string? Error);
    private readonly RegionalHistoryService service = new();
    private readonly LatestRequest<Result> request = new();
    private string? selected;
    private Result? result;

    public void Draw(GilOpportunity plan)
    {
        var key = $"{plan.ItemId}:{plan.HighQuality}:{plan.SaleWorld}";
        if (selected != key) { Reset(); selected = key; }
        if (request.TryTake(out var completed)) result = completed;
        if (!ImGui.CollapsingHeader("Additional regional history — Saddlebag Exchange")) return;
        ImGui.TextWrapped("Optional seven-day regional analysis, using Universalis data through Saddlebag Exchange. This is not an independent source and does not change home-world profit or batch sell-time estimates.");
        ImGui.BeginDisabled(request.IsRunning);
        if (ImGui.Button("Load regional history"))
        {
            result = null;
            request.Start(async token =>
            {
                try { return new Result(await service.GetAsync(plan.ItemId, plan.SaleWorld, plan.HighQuality, token).ConfigureAwait(false), null); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { return new Result(null, "Regional service unavailable or returned unsupported data. Your local scan is unaffected."); }
            });
        }
        ImGui.EndDisabled();
        if (request.IsRunning) ImGui.TextWrapped("Loading regional context (requests may wait for the 30-second pacing interval)...");
        if (result?.Error != null) ImGui.TextWrapped(result.Error);
        if (result?.History is { } history)
            ImGui.TextWrapped($"{(plan.HighQuality ? "HQ" : "NQ")} REGION-wide: {history.UnitsPerDay:N1} units/day; median {history.MedianPrice:N0} gil; {history.SampleTransactions:N0} sampled transactions / {history.SampleUnits:N0} units. Retrieved {history.RetrievedAt.LocalDateTime:g}. History can be capped; retrieved time is not the age of each sale.");
    }
    public void Reset() { request.Cancel(); selected = null; result = null; }
    public void Dispose() { request.Dispose(); service.Dispose(); }
}
