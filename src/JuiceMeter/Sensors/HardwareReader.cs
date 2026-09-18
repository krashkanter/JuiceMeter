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

    /// <summary>The machine has a dGPU but it is currently switched off (Eco mode).</summary>
    public bool GpuSwitchedOff { get; private set; }

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
            _nvidiaInComputer = false;

            foreach (var hardware in _computer.Hardware)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        hardware.Update();
                        CpuName ??= hardware.Name;
                        if (FindCpuPower(hardware) is not null) CpuPowerAvailable = true;
                        break;

                    // Deliberately excluding HardwareType.GpuIntel: the integrated
                    // GPU already sits inside the CPU package reading, and counting
                    // it separately would double up.
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
            Log.Info($"Hardware sensors: {Status} (cpu={CpuName}, gpu={GpuName}, elevated={IsElevated})");

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
    public (double CpuWatts, double GpuWatts) Read()
    {
        lock (_gate)
        {
            if (_computer is null) return (double.NaN, double.NaN);

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

                if (_computer is null) return (double.NaN, double.NaN);
            }

            double cpu = double.NaN, gpu = double.NaN;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            hardware.Update();
                            if (FindCpuPower(hardware) is { } c) cpu = double.IsNaN(cpu) ? c : cpu + c;
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

            return (Sane(cpu, 200), Sane(gpu, 400));
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
