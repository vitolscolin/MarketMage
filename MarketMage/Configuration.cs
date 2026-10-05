using System.Collections.Generic;
using Dalamud.Configuration;
using MarketMage.Models;

namespace MarketMage;

public sealed class Configuration : IPluginConfiguration
{
    public List<MonitoredOpportunity> MonitoredOpportunities { get; set; } = [];
    public int ExpectedMarketSharePercent { get; set; } = 25;
    public bool AutoScan { get; set; } = true;
    public int GilBudget { get; set; } = 100_000;
    public int MinimumProfit { get; set; } = 1_000;
    public int MinimumRoiPercent { get; set; } = 10;
    public int MinimumSales { get; set; } = 3;
    public int MaximumAgeHours { get; set; } = 24;
    public int Version { get; set; } = 1;
    public string World { get; set; } = "Cactuar";
    public bool HighQuality { get; set; }
    public bool CraftableOnly { get; set; }
    public bool CompareDataCenter { get; set; } = true;
    public List<uint> SelectedItems { get; set; } = [];
}
