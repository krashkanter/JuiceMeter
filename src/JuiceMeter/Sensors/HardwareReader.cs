using System.Security.Principal;
using JuiceMeter.Core;
using LibreHardwareMonitor.Hardware;

namespace JuiceMeter.Sensors;

/// <summary>
/// One pass of the power sensors. Watts, or NaN for anything unreadable.
///
/// The important subtlety is that <see cref="IGpuWatts"/> is *inside*
/// <see cref="CpuPackageWatts"/>, not alongside it. Intel reports integrated
/// graphics as the RAPL PP1 domain, which the package domain already contains,
/// so the two must never be summed -- that is where a double count would creep
/// into the bill. Only the package and the discrete card are real, additive
/// draws; the integrated figure exists so the UI can carve the package open.
/// </summary>
internal readonly record struct HardwareReading(
    double CpuPackageWatts,
    double IGpuWatts,
    double DGpuWatts)
{
    public static HardwareReading None { get; } =
        new(double.NaN, double.NaN, double.NaN);

    /// <summary>Package power with the integrated graphics slice taken back out.</summary>
    public double CpuCoreWatts =>
        double.IsNaN(CpuPackageWatts) ? double.NaN
        : double.IsNaN(IGpuWatts) ? CpuPackageWatts
        : Math.Max(0, CpuPackageWatts - IGpuWatts);

    /// <summary>Everything the sensors can account for, with nothing counted twice.</summary>
    public double KnownWatts =>
        (double.IsNaN(CpuPackageWatts) ? 0 : CpuPackageWatts) +
        (double.IsNaN(DGpuWatts) ? 0 : DGpuWatts);
}

/// <summary>
/// CPU and GPU package power, the two numbers an overlay like Afterburner or
/// HWiNFO already surfaces.
///
/// The CPU figure comes from Intel RAPL energy counters read over MSRs, which
/// needs a kernel driver and therefore administrator rights. The NVIDIA figure
/// comes from NVML and usually works unelevated. Everything here degrades
/// quietly: without these sensors Juice Meter still measures accurately on
/// battery, it just has a coarser model while on AC.
///
/// Note the deliberate absence of an IVisitor. Traversing the whole tree would
/// call Update on the NVIDIA node even when the dGPU has been switched off, and
/// that is fatal -- see <see cref="DiscreteGpu"/>. Each device is updated
/// individually so the GPU can be skipped.
/// </summary>
internal sealed class HardwareReader : IDisposable
{
    private readonly DiscreteGpu _dgpu = new();
    private readonly object _gate = new();

    private Computer? _computer;

    /// <summary>LibreHardwareMonitor currently holds a live NVIDIA node we may poll.</summary>
    private bool _nvidiaInComputer;

    public bool Available { get; private set; }
    public bool CpuPowerAvailable { get; private set; }
    public bool GpuPowerAvailable { get; private set; }

    /// <summary>The RAPL graphics domain is readable, so the iGPU can be shown separately.</summary>
    public bool IGpuPowerAvailable { get; private set; }

    /// <summary>The machine has a dGPU but it is currently switched off (Eco mode).</summary>
    public bool GpuSwitchedOff { get; private set; }

    public string? CpuName { get; private set; }
    public string? GpuName { get; private set; }
    public string? IGpuName { get; private set; }
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
        lock (_gate) return OpenLocked();
    }

    private bool OpenLocked()
    {
        try
        {
            var nvidiaRunning = _dgpu.IsRunning();

            // GPU support stays on for machines with an AMD or no discrete part;
            // only the NVIDIA node is gated, because NVML is the one that faults
            // when its device disappears.
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = nvidiaRunning || !_dgpu.EverSeen,
                IsMotherboardEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };

            _computer.Open();

            CpuPowerAvailable = false;
            GpuPowerAvailable = false;
            IGpuPowerAvailable = false;
            _nvidiaInComputer = false;

            foreach (var hardware in _computer.Hardware)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        hardware.Update();
                        CpuName ??= hardware.Name;
                        if (FindCpuPower(hardware) is not null) CpuPowerAvailable = true;
                        if (FindIGpuPower(hardware) is not null) IGpuPowerAvailable = true;
                        break;

                    // The integrated GPU is reported, but only as a slice of the
                    // package it already lives in -- see HardwareReading. Its node
                    // is used for the name, and as a fallback for parts that do
                    // not surface a graphics domain on the CPU itself.
                    case HardwareType.GpuIntel:
                        hardware.Update();
                        IGpuName ??= hardware.Name;
                        if (FindGpuPower(hardware) is not null) IGpuPowerAvailable = true;
                        break;

                    case HardwareType.GpuNvidia:
                        if (!nvidiaRunning) break;
                        _nvidiaInComputer = true;
                        goto case HardwareType.GpuAmd;

                    case HardwareType.GpuAmd:
                        hardware.Update();
                        GpuName = hardware.Name;
                        if (FindGpuPower(hardware) is not null) GpuPowerAvailable = true;
                        break;
                }
            }

            GpuSwitchedOff = _dgpu.EverSeen && !nvidiaRunning;
            Available = CpuPowerAvailable || GpuPowerAvailable;

            Status = BuildStatus();
            Log.Info($"Hardware sensors: {Status} (cpu={CpuName}, gpu={GpuName}, " +
                     $"igpu={IGpuName ?? "-"}/{(IGpuPowerAvailable ? "ok" : "unavailable")}, elevated={IsElevated})");

            return Available;
        }
        catch (Exception ex)
        {
            Status = IsElevated ? "Sensor driver failed to load" : "Needs administrator for CPU/GPU power sensors";
            Log.Error("Could not start hardware sensors", ex);
            CloseLocked();
            return false;
        }
    }

    private string BuildStatus()
    {
        if (GpuSwitchedOff)
        {
            return CpuPowerAvailable
                ? "CPU ok, GPU switched off (Eco)"
                : "CPU unavailable, GPU switched off (Eco)";
        }

        if (!Available)
        {
            return IsElevated
                ? "No power sensors exposed by this hardware"
                : "Needs administrator for CPU package power";
        }

        return $"CPU {(CpuPowerAvailable ? "ok" : "unavailable")}, GPU {(GpuPowerAvailable ? "ok" : "unavailable")}";
    }

    /// <summary>Package power in watts. NaN for anything we cannot read.</summary>
    public HardwareReading Read()
    {
        lock (_gate)
        {
            if (_computer is null) return HardwareReading.None;

            // Checked immediately before use, on purpose: the gap between this
            // and the NVML call is the entire window in which a mode switch
            // could still catch us out.
            var nvidiaRunning = _dgpu.IsRunning();

            if (!nvidiaRunning && _nvidiaInComputer)
            {
                // The dGPU just went away. Do NOT tear the monitor down here:
                // closing it would run NVML shutdown against a device that has
                // already gone. Park the GPU node instead and never touch it
                // again, which costs nothing and cannot fault.
                _nvidiaInComputer = false;
                GpuSwitchedOff = true;
                GpuPowerAvailable = false;
                Available = CpuPowerAvailable;
                Status = BuildStatus();

                Log.Info("Discrete GPU switched off; GPU sensors parked");
            }
            else if (nvidiaRunning && !_nvidiaInComputer)
            {
                // It is back, and alive, so a full rebuild is safe now: the
                // teardown will run against a device that exists again.
                Log.Info("Discrete GPU came back; restarting sensors");

                CloseLocked();
                OpenLocked();

                if (_computer is null) return HardwareReading.None;
            }

            double cpu = double.NaN, gpu = double.NaN, igpu = double.NaN;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            hardware.Update();
                            if (FindCpuPower(hardware) is { } c) cpu = double.IsNaN(cpu) ? c : cpu + c;
                            if (FindIGpuPower(hardware) is { } ig) igpu = double.IsNaN(igpu) ? ig : igpu + ig;
                            break;

                        case HardwareType.GpuIntel:
                            // Only consulted when the CPU node had no graphics
                            // domain of its own; it reads the same silicon.
                            if (!double.IsNaN(igpu)) break;
                            hardware.Update();
                            if (FindGpuPower(hardware) is { } ig2) igpu = ig2;
                            break;

                        case HardwareType.GpuNvidia when !_nvidiaInComputer:
                            // Parked. Touching this node is what kills the process.
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                            hardware.Update();
                            if (FindGpuPower(hardware) is { } g) gpu = double.IsNaN(gpu) ? g : gpu + g;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Hardware sensor read failed", ex);
            }

            // A dGPU that is switched off genuinely draws nothing, so report a
            // real zero rather than "unknown" and keep the AC model calibrated.
            if (GpuSwitchedOff) gpu = 0;

            var package = Sane(cpu, 200);

            // Never let the slice exceed the whole: a stale or half-updated PP1
            // counter would otherwise show a negative CPU bar.
            var graphics = Sane(igpu, 200);
            if (!double.IsNaN(package) && !double.IsNaN(graphics) && graphics > package) graphics = package;

            return new HardwareReading(package, graphics, Sane(gpu, 400));
        }
    }

    private static double Sane(double watts, double max) =>
        double.IsFinite(watts) && watts >= 0 && watts <= max ? watts : double.NaN;

    private static double? FindCpuPower(IHardware hardware)
    {
        // "CPU Package" is the RAPL package domain: cores, cache, iGPU and the
        // uncore, which is exactly the boundary we want.
        return FindPower(hardware, "cpu package", "package");
    }

    /// <summary>
    /// The RAPL graphics domain, PP1 on Intel. This is the same counter that
    /// Afterburner and HWiNFO show as integrated-GPU power, and it is a subset
    /// of the package figure above, never an addition to it.
    /// </summary>
    private static double? FindIGpuPower(IHardware hardware) =>
        FindPower(hardware, "cpu graphics", "graphics", "igpu");

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

    private void CloseLocked()
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
            GpuPowerAvailable = false;
            CpuPowerAvailable = false;
        }
    }

    public void Dispose()
    {
        lock (_gate) CloseLocked();
    }
}
