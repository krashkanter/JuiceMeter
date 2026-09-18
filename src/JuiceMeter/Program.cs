using System.Globalization;
using JuiceMeter.Core;
using JuiceMeter.Sensors;
using JuiceMeter.Ui;

namespace JuiceMeter;

internal static class Program
{
    private const string MutexName = @"Local\JuiceMeter.SingleInstance";
    private const string ShowEventName = @"Local\JuiceMeter.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            return Probe.Run(args);
        }

        if (args.Any(a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)))
        {
            return Screenshot.Run(args);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // Hand the request over to the copy that is already running.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (Exception ex)
            {
                Log.Error("Another instance is running but would not come to the front", ex);
            }

            return 0;
        }

        // Visual styles, PerMonitorV2 DPI and text rendering all come from the
        // Application* properties in the csproj.
        ApplicationConfiguration.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
            Log.Error("Unhandled UI exception", e.Exception);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Log.Info($"Juice Meter starting (elevated={HardwareReader.IsElevated}, data={Paths.DataDir})");

        var settings = Settings.Load();
        var state = AppState.Load();

        var startHidden = settings.StartMinimized
                          || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        JuiceMeterContext? context = null;
        RegisteredWaitHandle? registration = null;
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        try
        {
            context = new JuiceMeterContext(settings, state, startHidden);

            // A second launch pokes this event instead of starting a new copy.
            var ctx = context;
            registration = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (_, _) =>
                {
                    try
                    {
                        ctx.ShowWindow();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not honour a show request", ex);
                    }
                },
                null, Timeout.Infinite, executeOnlyOnce: false);

            Application.Run(context);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);

            MessageBox.Show(
                $"Juice Meter hit a problem and has to close.\n\n{ex.Message}\n\nDetails are in:\n{Paths.LogFile}",
                "Juice Meter", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return 1;
        }
        finally
        {
            registration?.Unregister(null);
            context?.Dispose();
            mutex.ReleaseMutex();
        }

        return 0;
    }
}

/// <summary>
/// Console mode: dump what every sensor reports and how the model turns that
/// into watts, then exit. Useful for checking a machine without the GUI, and for
/// filing a bug with real numbers attached.
/// </summary>
internal static class Probe
{
    public static int Run(string[] args)
    {
        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) Native.AllocConsole();

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        var seconds = 8;
        var index = Array.FindIndex(args, a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
        {
            seconds = Math.Clamp(parsed, 1, 600);
        }

        var inv = CultureInfo.InvariantCulture;

        Console.WriteLine();
        Console.WriteLine("Juice Meter probe");
        Console.WriteLine("=================");
        Console.WriteLine($"Elevated      : {HardwareReader.IsElevated}");
        Console.WriteLine($"Data folder   : {Paths.DataDir}");
        Console.WriteLine();

        using var battery = new BatteryReader();
        var first = battery.Read();

        Console.WriteLine("Battery");
        Console.WriteLine($"  present     : {first.Present}");

        if (first.Present)
        {
            Console.WriteLine($"  device      : {battery.Manufacturer} {battery.DeviceName} ({battery.Chemistry})");
            Console.WriteLine($"  packs       : {battery.PackCount}");
            Console.WriteLine($"  full charge : {first.FullChargeWh.ToString("F1", inv)} Wh");
            Console.WriteLine($"  design      : {first.DesignWh.ToString("F1", inv)} Wh");
            Console.WriteLine($"  cycles      : {first.CycleCount}");
            Console.WriteLine($"  rate known  : {first.RateKnown}");
            Console.WriteLine($"  relative    : {first.RelativeCapacity}");
        }

        Console.WriteLine();

        using var hardware = new HardwareReader();
        hardware.TryOpen();

        var dgpu = new DiscreteGpu();

        Console.WriteLine("Hardware sensors");
        Console.WriteLine($"  status      : {hardware.Status}");
        Console.WriteLine($"  cpu         : {hardware.CpuName ?? "-"} (power {(hardware.CpuPowerAvailable ? "yes" : "no")})");
        Console.WriteLine($"  gpu         : {hardware.GpuName ?? "-"} (power {(hardware.GpuPowerAvailable ? "yes" : "no")})");
        var dgpuRunning = dgpu.IsRunning();
        Console.WriteLine($"  dgpu devnode: {dgpu.DeviceInstanceId ?? "not present (Eco mode removes it entirely)"}");
        Console.WriteLine($"  dgpu running: {dgpuRunning}"
                          + (dgpuRunning ? string.Empty : "   <- NVML will not be called"));
        Console.WriteLine();

        Console.WriteLine("Sensors exposed");
        foreach (var line in hardware.DescribeSensors()) Console.WriteLine(line);
        Console.WriteLine();

        var brightness = BrightnessReader.TryRead();
        Console.WriteLine($"Panel brightness: {(brightness >= 0 ? brightness + "%" : "not reported")}");
        Console.WriteLine();

        // cpu_W is the whole package and igpu_W is the graphics slice inside it,
        // so cpu_W + gpu_W is the total; adding igpu_W would count it twice.
        Console.WriteLine("time      source       batt_W    cpu_W   igpu_W    gpu_W   base_W  system_W    wall_W");
        Console.WriteLine("--------  -----------  -------  -------  -------  -------  -------  --------  --------");

        var settings = Settings.Load();
        var state = AppState.Load();

        for (var i = 0; i < seconds; i++)
        {
            var reading = battery.Read();
            var probe = hardware.Read();
            var cpu = probe.CpuPackageWatts;
            var igpu = probe.IGpuWatts;
            var gpu = probe.DGpuWatts;

            var source = !reading.AcOnline
                ? "battery"
                : reading.Charging && reading.Watts > 0.5 ? "ac+charging" : "ac";

            double systemWatts, baseline;

            if (!reading.AcOnline && reading.RateKnown && reading.Watts < 0)
            {
                systemWatts = -reading.Watts;
                baseline = systemWatts - (double.IsNaN(cpu) ? 0 : cpu) - (double.IsNaN(gpu) ? 0 : gpu);
            }
            else
            {
                baseline = settings.ManualBaselineWatts
                           ?? state.Calibration.Estimate(brightness, settings.FallbackBaselineWatts);
                systemWatts = (double.IsNaN(cpu) ? 0 : cpu) + (double.IsNaN(gpu) ? 0 : gpu) + baseline;
            }

            var wallWatts = 0.0;
            if (reading.AcOnline)
            {
                var dc = reading.Watts >= 0
                    ? systemWatts + reading.Watts / settings.ChargeEfficiency
                    : systemWatts + reading.Watts;
                wallWatts = Math.Max(0, dc) / settings.AdapterEfficiency;
            }

            Console.WriteLine(string.Join("  ",
                DateTime.Now.ToString("HH:mm:ss", inv),
                source.PadRight(11),
                Col(reading.RateKnown ? reading.Watts : double.NaN, 7),
                Col(cpu, 7),
                Col(igpu, 7),
                Col(gpu, 7),
                Col(baseline, 7),
                Col(systemWatts, 8),
                Col(wallWatts, 8)));

            if (i < seconds - 1) Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine(state.Calibration.IsCalibrated
            ? $"Calibrated baseline: {state.Calibration.GlobalWatts.ToString("F1", inv)} W " +
              $"from {state.Calibration.GlobalSamples} samples."
            : "Not calibrated yet. Run on battery for a couple of minutes to teach the baseline.");
        Console.WriteLine();

        stdout.Flush();
        Native.FreeConsole();
        return 0;
    }

    private static string Col(double value, int width) =>
        (double.IsNaN(value) ? "-" : value.ToString("F2", CultureInfo.InvariantCulture)).PadLeft(width);
}
