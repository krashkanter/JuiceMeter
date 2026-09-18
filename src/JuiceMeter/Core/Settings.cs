using System.Text.Json;
using System.Text.Json.Serialization;

namespace JuiceMeter.Core;

public sealed class Settings
{
    // --- sampling -----------------------------------------------------------
    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Longest gap we are willing to integrate across. Anything longer (sleep,
    /// hibernate, a stalled process) is recorded as a gap instead of being
    /// counted as energy at the last known wattage.
    /// </summary>
    public int MaxIntegrationSeconds { get; set; } = 10;

    /// <summary>Read CPU/GPU package power via LibreHardwareMonitor. Needs admin for CPU RAPL.</summary>
    public bool EnableHardwareSensors { get; set; } = true;

    /// <summary>Also dump every raw sample to raw\yyyy-MM-dd.csv. Off by default; it is chatty.</summary>
    public bool KeepRawSamples { get; set; }

    // --- power model --------------------------------------------------------
    /// <summary>AC brick efficiency, wall -> DC barrel. 0.88-0.92 is typical for a 240 W GaN/ODM brick.</summary>
    public double AdapterEfficiency { get; set; } = 0.90;

    /// <summary>Charger efficiency, DC-in -> energy actually stored in the cells.</summary>
    public double ChargeEfficiency { get; set; } = 0.92;

    /// <summary>Used until the calibrator has learned a real baseline from a battery session.</summary>
    public double FallbackBaselineWatts { get; set; } = 12.0;

    /// <summary>Set this to pin the non-CPU/GPU baseline yourself and skip the learned value.</summary>
    public double? ManualBaselineWatts { get; set; }

    // --- money --------------------------------------------------------------
    public string CurrencySymbol { get; set; } = "₹";
    public double TariffPerUnit { get; set; } = 8.0;

    // --- shell --------------------------------------------------------------
    public bool AutoStart { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool ShowWattsInTray { get; set; } = true;
    public bool Notifications { get; set; } = true;

    /// <summary>Toast every N units consumed from the wall in a day. 0 disables.</summary>
    public double NotifyEveryUnits { get; set; } = 1.0;

    [JsonIgnore]
    public TimeSpan SampleInterval => TimeSpan.FromMilliseconds(Math.Clamp(SampleIntervalMs, 250, 60_000));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Json);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings.json, falling back to defaults", ex);
        }

        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Clamp();
            Paths.WriteAtomic(Paths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write settings.json", ex);
        }
    }

    private void Clamp()
    {
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, 250, 60_000);
        MaxIntegrationSeconds = Math.Clamp(MaxIntegrationSeconds, 2, 300);
        AdapterEfficiency = Math.Clamp(AdapterEfficiency, 0.50, 1.00);
        ChargeEfficiency = Math.Clamp(ChargeEfficiency, 0.50, 1.00);
        FallbackBaselineWatts = Math.Clamp(FallbackBaselineWatts, 0.5, 100.0);
        if (ManualBaselineWatts is { } m) ManualBaselineWatts = Math.Clamp(m, 0.5, 100.0);
        TariffPerUnit = Math.Max(0, TariffPerUnit);
        NotifyEveryUnits = Math.Max(0, NotifyEveryUnits);
        if (string.IsNullOrWhiteSpace(CurrencySymbol)) CurrencySymbol = "₹";
    }
}
