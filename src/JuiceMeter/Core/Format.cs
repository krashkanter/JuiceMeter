using System.Globalization;

namespace JuiceMeter.Core;

internal static class Format
{
    /// <summary>
    /// Units of electricity, which is to say kilowatt-hours. Shown with enough
    /// decimals to still move visibly over a coffee break.
    /// </summary>
    public static string Units(double units)
    {
        if (!double.IsFinite(units) || units < 0) return "0.000";
        if (units < 10) return units.ToString("F3", CultureInfo.CurrentCulture);
        if (units < 100) return units.ToString("F2", CultureInfo.CurrentCulture);
        return units.ToString("F1", CultureInfo.CurrentCulture);
    }

    public static string Hours(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 24) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalMinutes}m";
    }

    public static string Money(double units, Settings settings) =>
        settings.TariffPerUnit > 0
            ? $"{settings.CurrencySymbol}{(units * settings.TariffPerUnit).ToString("F2", CultureInfo.CurrentCulture)}"
            : "—";
}
