using System.Globalization;
using System.Text;

namespace JuiceMeter.Core;

/// <summary>One accounted minute. The finest grain we keep long term.</summary>
public sealed record MinuteRow(
    DateTime LocalMinute,
    double Seconds,
    PowerSource Source,
    Confidence Confidence,
    double WallWh,
    double SystemWh,
    double BatteryOutWh,
    double BatteryInWh,
    double AvgSystemWatts,
    double AvgWallWatts,
    double AvgCpuWatts,
    double AvgGpuWatts,
    double AvgBaselineWatts,
    double BatteryPercent);

public sealed class DayTotal
{
    public DateTime Date { get; init; }
    public double WallWh { get; set; }
    public double SystemWh { get; set; }
    public double BatteryOutWh { get; set; }
    public double BatteryInWh { get; set; }
    public double Seconds { get; set; }
    public double AcSeconds { get; set; }
    public double BatterySeconds { get; set; }

    public double WallUnits => WallWh / 1000.0;
    public double SystemUnits => SystemWh / 1000.0;
    public double AverageWallWatts => Seconds > 0 ? WallWh * 3600.0 / Seconds : 0;
    public double AverageSystemWatts => Seconds > 0 ? SystemWh * 3600.0 / Seconds : 0;

    public void Add(DayTotal other)
    {
        WallWh += other.WallWh;
        SystemWh += other.SystemWh;
        BatteryOutWh += other.BatteryOutWh;
        BatteryInWh += other.BatteryInWh;
        Seconds += other.Seconds;
        AcSeconds += other.AcSeconds;
        BatterySeconds += other.BatterySeconds;
    }
}

/// <summary>
/// Minute rows land in one CSV per calendar month. Plain text on purpose: the
/// whole point of this app is a number you can audit, so the raw data stays
/// greppable and loads straight into a spreadsheet.
/// </summary>
public sealed class HistoryStore
{
    private const string Header =
        "local_time,unix_seconds,seconds,source,confidence,wall_wh,system_wh,batt_out_wh,batt_in_wh," +
        "avg_system_w,avg_wall_w,avg_cpu_w,avg_gpu_w,avg_base_w,batt_pct";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<DateTime, DayTotal>> _cache = new();

    public void AppendMinute(MinuteRow row)
    {
        lock (_gate)
        {
            try
            {
                var path = Paths.MonthFile(row.LocalMinute);
                var isNew = !File.Exists(path);

                var sb = new StringBuilder(256);
                if (isNew) sb.Append(Header).Append('\n');

                sb.Append(row.LocalMinute.ToString("yyyy-MM-dd HH:mm", Inv)).Append(',')
                  .Append(new DateTimeOffset(row.LocalMinute).ToUnixTimeSeconds().ToString(Inv)).Append(',')
                  .Append(F(row.Seconds, 1)).Append(',')
                  .Append(row.Source).Append(',')
                  .Append(row.Confidence).Append(',')
                  .Append(F(row.WallWh, 6)).Append(',')
                  .Append(F(row.SystemWh, 6)).Append(',')
                  .Append(F(row.BatteryOutWh, 6)).Append(',')
                  .Append(F(row.BatteryInWh, 6)).Append(',')
                  .Append(F(row.AvgSystemWatts, 2)).Append(',')
                  .Append(F(row.AvgWallWatts, 2)).Append(',')
                  .Append(F(row.AvgCpuWatts, 2)).Append(',')
                  .Append(F(row.AvgGpuWatts, 2)).Append(',')
                  .Append(F(row.AvgBaselineWatts, 2)).Append(',')
                  .Append(F(row.BatteryPercent, 1)).Append('\n');

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);

                // Keep the in-memory rollup in step instead of re-reading the file.
                var key = MonthKey(row.LocalMinute);
                if (_cache.TryGetValue(key, out var month))
                {
                    var date = row.LocalMinute.Date;
                    if (!month.TryGetValue(date, out var day))
                    {
                        day = new DayTotal { Date = date };
                        month[date] = day;
                    }
                    Accumulate(day, row);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not append a minute row", ex);
            }
        }
    }

    public DayTotal GetDay(DateTime localDate)
    {
        localDate = localDate.Date;
        lock (_gate)
        {
            var month = LoadMonth(localDate.Year, localDate.Month);
            return month.TryGetValue(localDate, out var day)
                ? Clone(day)
                : new DayTotal { Date = localDate };
        }
    }

    public DayTotal GetMonth(int year, int month)
    {
        lock (_gate)
        {
            var total = new DayTotal { Date = new DateTime(year, month, 1) };
            foreach (var day in LoadMonth(year, month).Values) total.Add(day);
            return total;
        }
    }

    /// <summary>Inclusive range of daily rollups, oldest first, with empty days filled in.</summary>
    public List<DayTotal> GetDays(DateTime fromLocalDate, DateTime toLocalDate)
    {
        fromLocalDate = fromLocalDate.Date;
        toLocalDate = toLocalDate.Date;

        var result = new List<DayTotal>();
        lock (_gate)
        {
            for (var date = fromLocalDate; date <= toLocalDate; date = date.AddDays(1))
            {
                var month = LoadMonth(date.Year, date.Month);
                result.Add(month.TryGetValue(date, out var day) ? Clone(day) : new DayTotal { Date = date });
            }
        }
        return result;
    }

    /// <summary>Totals across every month file on disk. Used to sanity-check state.json.</summary>
    public DayTotal GetAllTime()
    {
        var total = new DayTotal { Date = DateTime.MinValue };
        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(Paths.HistoryDir, "????-??.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name.AsSpan(0, 4), out var year)) continue;
                if (!int.TryParse(name.AsSpan(5, 2), out var month)) continue;
                foreach (var day in LoadMonth(year, month).Values) total.Add(day);
            }
        }
        return total;
    }

    public void InvalidateCache()
    {
        lock (_gate) _cache.Clear();
    }

    private Dictionary<DateTime, DayTotal> LoadMonth(int year, int month)
    {
        var key = $"{year:D4}-{month:D2}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var days = new Dictionary<DateTime, DayTotal>();
        var path = Paths.MonthFile(year, month);

        try
        {
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == 'l') continue;   // header
                    var row = TryParse(line);
                    if (row is null) continue;

                    var date = row.LocalMinute.Date;
                    if (!days.TryGetValue(date, out var day))
                    {
                        day = new DayTotal { Date = date };
                        days[date] = day;
                    }
                    Accumulate(day, row);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read history for {key}", ex);
        }

        _cache[key] = days;
        return days;
    }

    private static void Accumulate(DayTotal day, MinuteRow row)
    {
        day.WallWh += row.WallWh;
        day.SystemWh += row.SystemWh;
        day.BatteryOutWh += row.BatteryOutWh;
        day.BatteryInWh += row.BatteryInWh;
        day.Seconds += row.Seconds;
        if (row.Source == PowerSource.Battery) day.BatterySeconds += row.Seconds;
        else if (row.Source is PowerSource.AcIdle or PowerSource.AcCharging) day.AcSeconds += row.Seconds;
    }

    private static MinuteRow? TryParse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 15) return null;

        try
        {
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out var minute))
                return null;

            Enum.TryParse<PowerSource>(parts[3], out var source);
            Enum.TryParse<Confidence>(parts[4], out var confidence);

            return new MinuteRow(
                minute,
                D(parts[2]),
                source,
                confidence,
                D(parts[5]),
                D(parts[6]),
                D(parts[7]),
                D(parts[8]),
                D(parts[9]),
                D(parts[10]),
                D(parts[11]),
                D(parts[12]),
                D(parts[13]),
                D(parts[14]));
        }
        catch
        {
            return null;
        }
    }

    private static DayTotal Clone(DayTotal source) => new()
    {
        Date = source.Date,
        WallWh = source.WallWh,
        SystemWh = source.SystemWh,
        BatteryOutWh = source.BatteryOutWh,
        BatteryInWh = source.BatteryInWh,
        Seconds = source.Seconds,
        AcSeconds = source.AcSeconds,
        BatterySeconds = source.BatterySeconds,
    };

    private static string MonthKey(DateTime date) => $"{date.Year:D4}-{date.Month:D2}";

    private static string F(double value, int decimals) =>
        double.IsFinite(value) ? Math.Round(value, decimals).ToString(Inv) : "0";

    private static double D(string s) =>
        double.TryParse(s, NumberStyles.Float, Inv, out var v) && double.IsFinite(v) ? v : 0;
}
