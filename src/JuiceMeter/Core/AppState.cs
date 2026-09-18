using System.Text.Json;

namespace JuiceMeter.Core;

/// <summary>
/// Running totals plus the learned calibration, flushed to disk once a minute so
/// an unclean shutdown costs at most one minute of accounting.
/// </summary>
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset FirstRunUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSavedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Energy pulled from the socket. This is what the electricity bill counts.</summary>
    public double LifetimeWallWh { get; set; }

    /// <summary>Energy the internals consumed, from socket or pack alike.</summary>
    public double LifetimeSystemWh { get; set; }

    public double LifetimeBatteryOutWh { get; set; }
    public double LifetimeBatteryInWh { get; set; }

    /// <summary>Seconds actually accounted for.</summary>
    public double LifetimeSeconds { get; set; }

    /// <summary>Seconds skipped because the machine was asleep or the sampler stalled.</summary>
    public double LifetimeGapSeconds { get; set; }

    public CalibrationModel Calibration { get; set; } = new();

    // Remembers how far we have already notified today so a restart does not re-toast.
    public string NotifiedDay { get; set; } = string.Empty;
    public double NotifiedUnits { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppState Load()
    {
        try
        {
            if (File.Exists(Paths.StateFile))
            {
                var loaded = JsonSerializer.Deserialize<AppState>(File.ReadAllText(Paths.StateFile), Json);
                if (loaded is not null)
                {
                    loaded.Calibration ??= new CalibrationModel();

                    // Before the lifetime totals are touched: a baseline learned
                    // by an older model is worse than no baseline at all, because
                    // it looks calibrated while quietly overstating every AC watt.
                    var discarded = loaded.Calibration.DiscardIfStale();
                    if (discarded > 0)
                    {
                        Log.Info($"Discarded {discarded:N0} calibration samples from an older model; " +
                                 "the baseline will relearn on battery");
                    }

                    loaded.Calibration.Rehydrate();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read state.json, starting counters from zero", ex);
            TryBackupCorruptState();
        }

        var fresh = new AppState();
        fresh.Calibration.DiscardIfStale();
        fresh.Calibration.Rehydrate();
        return fresh;
    }

    private static void TryBackupCorruptState()
    {
        try
        {
            if (!File.Exists(Paths.StateFile)) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(Paths.StateFile, Paths.StateFile + "." + stamp + ".bad", overwrite: true);
        }
        catch
        {
            // Nothing useful to do if even the rename fails.
        }
    }

    public void Save()
    {
        try
        {
            Calibration.Flush();
            LastSavedUtc = DateTimeOffset.UtcNow;
            Paths.WriteAtomic(Paths.StateFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write state.json", ex);
        }
    }

    public void ResetTotals()
    {
        LifetimeWallWh = 0;
        LifetimeSystemWh = 0;
        LifetimeBatteryOutWh = 0;
        LifetimeBatteryInWh = 0;
        LifetimeSeconds = 0;
        LifetimeGapSeconds = 0;
        FirstRunUtc = DateTimeOffset.UtcNow;
        NotifiedDay = string.Empty;
        NotifiedUnits = 0;
    }
}
