using System;
using MarketMage.Models;

namespace MarketMage.Services;

public static class PriceScenarios
{
    public static long? BreakEven(GilOpportunity row) => row.OutputQuantity <= 0 || row.Outlay <= 0 ? null :
        (long)Math.Ceiling(Math.Ceiling(row.Outlay / (decimal)row.OutputQuantity) / 0.95m);

    public static long SalePrice(GilOpportunity row, int dropPercent) =>
        (long)Math.Floor(row.SalePrice * (1m - Math.Clamp(dropPercent, 0, 100) / 100m));

    public static long Profit(GilOpportunity row, int dropPercent) =>
        (long)Math.Floor(SalePrice(row, dropPercent) * 0.95m) * row.OutputQuantity - row.Outlay;
}
