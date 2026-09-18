using System.Security.Principal;
using JuiceMeter.Core;
using LibreHardwareMonitor.Hardware;

namespace JuiceMeter.Sensors;

/// <summary>
/// CPU and GPU package power, the two numbers an overlay like Afterburner or
/// HWiNFO already surfaces.
///
/// The CPU figure comes from Intel RAPL energy counters read over MSRs, which
/// needs a kernel driver and therefore administrator rights. The NVIDIA figure
/// comes from NVML and usually works unelevated. Everything here degrades
/// quietly: without these sensors Juice Meter still measures accurately on
/// battery, it just has a coarser model while on AC.
/// </summary>
internal sealed class HardwareReader : IDisposable
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();

    public bool Available { get; private set; }
    public bool CpuPowerAvailable { get; private set; }
    public bool GpuPowerAvailable { get; private set; }
    public string? CpuName { get; private set; }
    public string? GpuName { get; private set; }
    public string Status { get; private set; } = "Not started";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryOpen()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };

            _computer.Open();
            _computer.Accept(_visitor);

            foreach (var hardware in _computer.Hardware)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        CpuName ??= hardware.Name;
                        if (FindCpuPower(hardware) is not null) CpuPowerAvailable = true;
                        break;

                    // Deliberately excluding HardwareType.GpuIntel: the integrated
                    // GPU already sits inside the CPU package reading, and counting
                    // it separately would double up.
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        GpuName ??= hardware.Name;
                        if (FindGpuPower(hardware) is not null) GpuPowerAvailable = true;
                        break;
                }
            }

            Available = CpuPowerAvailable || GpuPowerAvailable;

            Status = Available
                ? $"CPU {(CpuPowerAvailable ? "ok" : "unavailable")}, GPU {(GpuPowerAvailable ? "ok" : "unavailable")}"
                : IsElevated
                    ? "No power sensors exposed by this hardware"
                    : "Needs administrator for CPU package power";

            Log.Info($"Hardware sensors: {Status} (cpu={CpuName}, gpu={GpuName}, elevated={IsElevated})");
            return Available;
        }
        catch (Exception ex)
        {
            Status = IsElevated
                ? "Sensor driver failed to load"
                : "Needs administrator for CPU/GPU power sensors";
            Log.Error("Could not start hardware sensors", ex);
            Close();
            return false;
        }
    }

    /// <summary>Package power in watts. NaN for anything we cannot read.</summary>
    public (double CpuWatts, double GpuWatts) Read()
    {
        if (_computer is null) return (double.NaN, double.NaN);

        double cpu = double.NaN, gpu = double.NaN;

        try
        {
            _computer.Accept(_visitor);

            foreach (var hardware in _computer.Hardware)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        if (FindCpuPower(hardware) is { } c) cpu = double.IsNaN(cpu) ? c : cpu + c;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        if (FindGpuPower(hardware) is { } g) gpu = double.IsNaN(gpu) ? g : gpu + g;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hardware sensor read failed", ex);
        }

        return (Sane(cpu, 200), Sane(gpu, 400));
    }

    private static double Sane(double watts, double max) =>
        double.IsFinite(watts) && watts >= 0 && watts <= max ? watts : double.NaN;

    private static double? FindCpuPower(IHardware hardware)
    {
        // "CPU Package" is the RAPL package domain: cores, cache, iGPU and the
        // uncore, which is exactly the boundary we want.
        return FindPower(hardware, "cpu package", "package");
    }

    private static double? FindGpuPower(IHardware hardware)
    {
        var named = FindPower(hardware, "gpu package", "gpu power", "package", "board power");
        if (named is not null) return named;

        // Some drivers name it something else entirely; fall back to the
        // largest power sensor on the device.
        double? best = null;
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Power || sensor.Value is not { } value) continue;
            if (!double.IsFinite(value)) continue;
            if (best is null || value > best) best = value;
        }
        return best;
    }

    private static double? FindPower(IHardware hardware, params string[] namesInPriorityOrder)
    {
        foreach (var wanted in namesInPriorityOrder)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Power) continue;
                if (!sensor.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (sensor.Value is { } value && double.IsFinite(value)) return value;
            }
        }
        return null;
    }

    private void Close()
    {
        try
        {
            _computer?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not close hardware sensors cleanly", ex);
        }
        finally
        {
            _computer = null;
            Available = false;
        }
    }

    public void Dispose() => Close();
}
