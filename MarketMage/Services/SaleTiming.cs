using System;

namespace MarketMage.Services;

public static class SaleTiming
{
    public static double? Days(long quantity, double? unitsPerDay, int marketSharePercent)
    {
        if (quantity <= 0 || !unitsPerDay.HasValue || !double.IsFinite(unitsPerDay.Value) || unitsPerDay <= 0 ||
            marketSharePercent is < 1 or > 100) return null;
        var days = quantity / unitsPerDay.Value * (100d / marketSharePercent);
        return double.IsFinite(days) ? days : null;
    }
    public static string Format(double? days)
    {
        if (!days.HasValue) return "Unknown";
        if (days < 1d / 24) return "<1 hour";
        if (days < 1) return $"~{Math.Ceiling(days.Value * 24):N0} hours";
        if (days > 365) return ">1 year";
        return $"~{days:N1} days";
    }
}
