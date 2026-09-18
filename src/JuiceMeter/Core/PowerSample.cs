namespace JuiceMeter.Core;

public enum PowerSource
{
    Unknown = 0,

    /// <summary>Running off the pack. System power is measured, wall power is zero.</summary>
    Battery = 1,

    /// <summary>Plugged in, battery neither charging nor discharging.</summary>
    AcIdle = 2,

    /// <summary>Plugged in and pushing energy into the pack.</summary>
    AcCharging = 3,
}

/// <summary>How much to trust <see cref="PowerSample.SystemWatts"/>.</summary>
public enum Confidence
{
    /// <summary>No usable reading at all.</summary>
    None = 0,

    /// <summary>On AC with no CPU/GPU sensors: a flat learned baseline. Rough.</summary>
    Estimated = 1,

    /// <summary>On AC with CPU + GPU sensors plus a calibrated baseline.</summary>
    Modelled = 2,

    /// <summary>On battery: the pack itself is telling us the number.</summary>
    Measured = 3,
}

public sealed class PowerSample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public PowerSource Source { get; init; }
    public Confidence Confidence { get; init; }

    /// <summary>What the internals are drawing, in watts, whatever the source.</summary>
    public double SystemWatts { get; init; }

    /// <summary>What the socket is delivering, in watts. Zero while on battery.</summary>
    public double WallWatts { get; init; }

    public double CpuWatts { get; init; }
    public double GpuWatts { get; init; }

    /// <summary>Everything that is not CPU or GPU: panel, board, RAM, SSD, fans, wifi.</summary>
    public double BaselineWatts { get; init; }

    /// <summary>Signed pack power: positive into the battery, negative out of it.</summary>
    public double BatteryWatts { get; init; }

    public bool BatteryPresent { get; init; }
    public double BatteryPercent { get; init; }
    public double BatteryVolts { get; init; }
    public double RemainingWh { get; init; }
    public double FullChargeWh { get; init; }
    public double DesignWh { get; init; }
    public TimeSpan? Runtime { get; init; }
    public int BrightnessPercent { get; init; } = -1;
    public bool HardwareSensorsLive { get; init; }

    /// <summary>CPU package power was readable for this sample (needs admin).</summary>
    public bool CpuSensorLive { get; init; }

    public bool GpuSensorLive { get; init; }

    public bool OnAc => Source is PowerSource.AcIdle or PowerSource.AcCharging;

    public double BatteryHealthPercent =>
        DesignWh > 0 && FullChargeWh > 0 ? FullChargeWh / DesignWh * 100.0 : double.NaN;

    public static PowerSample Empty { get; } = new();
}
