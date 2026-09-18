using System.Diagnostics;
using System.Globalization;
using JuiceMeter.Sensors;
using Microsoft.Win32;

namespace JuiceMeter.Core;

/// <summary>
/// The meter itself: samples power once a second, integrates it into energy, and
/// rolls the result up into minute rows on disk.
///
/// Two ledgers are kept, and the difference matters:
///
///   Wall energy   -- what the socket delivered. Only accrues on AC, and includes
///                    the energy poured into the battery plus conversion losses.
///                    This is the number the electricity bill charges for.
///   System energy -- what the internals consumed, from socket or pack alike.
///                    Running an hour on battery adds nothing to the wall ledger,
///                    because that energy was already paid for while charging.
/// </summary>
public sealed class PowerMonitor : IDisposable
{
    private readonly Settings _settings;
    private readonly AppState _state;
    private readonly HistoryStore _history;
    private readonly BatteryReader _battery = new();
    private readonly HardwareReader _hardware = new();

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private int _ticking;
    private bool _disposed;

    private long _lastStamp;
    private PowerSample? _previous;

    private DateTime _minute = DateTime.MinValue;
    private double _mSeconds, _mWallWh, _mSystemWh, _mBattOutWh, _mBattInWh;
    private double _mSystemWs, _mWallWs, _mCpuWs, _mGpuWs, _mIGpuWs, _mBaseWs, _mBattPctS;
    private PowerSource _mSource = PowerSource.Unknown;
    private Confidence _mConfidence = Confidence.Measured;

    private int _brightness = -1;
    private DateTime _nextBrightnessRead = DateTime.MinValue;
    private DateTime _nextStateSave = DateTime.MinValue;

    public event Action<PowerSample>? Sampled;
    public event Action<MinuteRow>? MinuteFlushed;
    public event Action<double>? UnitMilestone;

    public PowerSample Latest { get; private set; } = PowerSample.Empty;
    public HistoryStore History => _history;
    public AppState State => _state;

    public bool HardwareSensorsAvailable => _hardware.Available;
    public bool CpuPowerAvailable => _hardware.CpuPowerAvailable;
    public bool GpuPowerAvailable => _hardware.GpuPowerAvailable;
    public bool GpuSwitchedOff => _hardware.GpuSwitchedOff;
    public string HardwareStatus => _hardware.Status;
    public string? CpuName => _hardware.CpuName;
    public string? GpuName => _hardware.GpuName;
    public string? BatteryName => _battery.DeviceName;
    public string? BatteryManufacturer => _battery.Manufacturer;
    public string? BatteryChemistry => _battery.Chemistry;

    /// <summary>
    /// Read-only meters sample the sensors and raise events but never write to
    /// the ledger. Used by the screenshot mode so it cannot double-count against
    /// a copy that is already running.
    /// </summary>
    private readonly bool _readOnly;

    public PowerMonitor(Settings settings, AppState state, HistoryStore history, bool readOnly = false)
    {
        _settings = settings;
        _state = state;
        _history = history;
        _readOnly = readOnly;
    }

    public void Start()
    {
        if (_settings.EnableHardwareSensors) _hardware.TryOpen();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _lastStamp = Stopwatch.GetTimestamp();
        Tick(null);

        _timer = new System.Threading.Timer(Tick, null, _settings.SampleInterval, _settings.SampleInterval);
        Log.Info($"Meter started at {_settings.SampleIntervalMs} ms intervals");
    }

    /// <summary>Applies a changed sample interval or hardware-sensor toggle without a restart.</summary>
    public void Reconfigure()
    {
        lock (_gate)
        {
            _timer?.Change(_settings.SampleInterval, _settings.SampleInterval);

            if (_settings.EnableHardwareSensors && !_hardware.Available) _hardware.TryOpen();
            else if (!_settings.EnableHardwareSensors && _hardware.Available) _hardware.Dispose();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Resume or PowerModes.Suspend)
        {
            // Force a gap rather than integrating across the sleep.
            lock (_gate)
            {
                _previous = null;
                _lastStamp = Stopwatch.GetTimestamp();
            }
            Log.Info($"Power mode {e.Mode}; integration restarted");
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => Flush();

    private void Tick(object? _)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;

        try
        {
            PowerSample sample;
            lock (_gate)
            {
                if (_disposed) return;
                sample = Sample();
            }

            Latest = sample;
            Sampled?.Invoke(sample);
        }
        catch (Exception ex)
        {
            Log.Error("Sample tick failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private PowerSample Sample()
    {
        var now = DateTimeOffset.Now;

        var stamp = Stopwatch.GetTimestamp();
        var dt = (stamp - _lastStamp) / (double)Stopwatch.Frequency;
        _lastStamp = stamp;

        if (DateTime.UtcNow >= _nextBrightnessRead)
        {
            var read = BrightnessReader.TryRead();
            if (read >= 0) _brightness = read;
            _nextBrightnessRead = DateTime.UtcNow.AddSeconds(10);
        }

        var battery = _battery.Read();
        // Always go through Read when sensors are enabled, even if nothing is
        // currently readable: that call is also what notices the discrete GPU
        // being switched off or coming back.
        var reading = _settings.EnableHardwareSensors ? _hardware.Read() : HardwareReading.None;

        // cpu is the whole package, which already contains igpu. Only these two
        // ever enter a total; igpu exists purely so the UI can split the bar.
        var cpu = reading.CpuPackageWatts;
        var gpu = reading.DGpuWatts;
        var igpu = reading.IGpuWatts;

        var source = ResolveSource(battery);
        var batteryWatts = battery.RateKnown ? battery.Watts : 0;

        double systemWatts, baseline;
        Confidence confidence;

        if (source == PowerSource.Battery && battery.RateKnown && batteryWatts < 0)
        {
            // The pack is telling us exactly what the machine draws.
            systemWatts = -batteryWatts;
            confidence = Confidence.Measured;

            var known = reading.KnownWatts;
            baseline = systemWatts - known;

            // Only bank the residual when CPU package power is actually readable.
            // Without it the leftover still contains the CPU, which swings by tens
            // of watts, and a baseline learned from that would be both noisy and
            // double-counted the moment we add CPU power back in on AC.
            if (!double.IsNaN(cpu)) _state.Calibration.Observe(baseline, _brightness);
        }
        else
        {
            baseline = _settings.ManualBaselineWatts
                       ?? _state.Calibration.Estimate(_brightness, _settings.FallbackBaselineWatts);

            var haveCpu = !double.IsNaN(cpu);
            var haveGpu = !double.IsNaN(gpu);

            if (haveCpu || haveGpu)
            {
                systemWatts = (haveCpu ? cpu : 0) + (haveGpu ? gpu : 0) + baseline;
                confidence = haveCpu ? Confidence.Modelled : Confidence.Estimated;
            }
            else
            {
                systemWatts = baseline;
                confidence = Confidence.Estimated;
            }

            if (!_state.Calibration.IsCalibrated && _settings.ManualBaselineWatts is null)
            {
                confidence = Confidence.Estimated;
            }
        }

        var wallWatts = 0.0;
        if (source is PowerSource.AcIdle or PowerSource.AcCharging)
        {
            // Charging costs more at the wall than what lands in the cells, and
            // under a heavy load the pack can be discharging while still on AC,
            // in which case the socket is supplying less than the system draws.
            var dcWatts = batteryWatts >= 0
                ? systemWatts + batteryWatts / _settings.ChargeEfficiency
                : systemWatts + batteryWatts;

            wallWatts = Math.Max(0, dcWatts) / _settings.AdapterEfficiency;
        }

        var sample = new PowerSample
        {
            Timestamp = now,
            Source = source,
            Confidence = battery.Present || confidence != Confidence.Measured ? confidence : Confidence.None,
            SystemWatts = Math.Max(0, systemWatts),
            WallWatts = wallWatts,
            CpuWatts = double.IsNaN(cpu) ? 0 : cpu,
            GpuWatts = double.IsNaN(gpu) ? 0 : gpu,
            IGpuWatts = double.IsNaN(igpu) ? 0 : igpu,
            BaselineWatts = Math.Max(0, baseline),
            BatteryWatts = batteryWatts,
            BatteryPresent = battery.Present,
            BatteryPercent = battery.Percent,
            BatteryVolts = battery.Volts,
            RemainingWh = battery.RemainingWh,
            FullChargeWh = battery.FullChargeWh,
            DesignWh = battery.DesignWh,
            Runtime = battery.Runtime,
            BrightnessPercent = _brightness,
            HardwareSensorsLive = _hardware.Available,
            CpuSensorLive = !double.IsNaN(cpu),
            GpuSensorLive = !double.IsNaN(gpu),
            IGpuSensorLive = !double.IsNaN(igpu),
        };

        Integrate(sample, dt);
        if (_settings.KeepRawSamples) WriteRaw(sample);

        return sample;
    }

    private static PowerSource ResolveSource(BatteryReader.Reading battery)
    {
        if (!battery.AcOnline) return battery.Present ? PowerSource.Battery : PowerSource.Unknown;
        return battery.Charging && battery.Watts > 0.5 ? PowerSource.AcCharging : PowerSource.AcIdle;
    }

    private void Integrate(PowerSample current, double dt)
    {
        var previous = _previous;
        _previous = current;

        if (_readOnly) return;

        if (previous is null || dt <= 0)
        {
            RollMinuteIfNeeded(current.Timestamp.LocalDateTime);
            return;
        }

        if (dt > _settings.MaxIntegrationSeconds)
        {
            // Asleep, hibernated, or the sampler was starved. Record the hole
            // instead of pretending the last wattage held the whole time.
            _state.LifetimeGapSeconds += dt;
            RollMinuteIfNeeded(current.Timestamp.LocalDateTime);
            return;
        }

        var hours = dt / 3600.0;

        var systemW = (previous.SystemWatts + current.SystemWatts) / 2.0;
        var wallW = (previous.WallWatts + current.WallWatts) / 2.0;
        var outW = (Math.Max(0, -previous.BatteryWatts) + Math.Max(0, -current.BatteryWatts)) / 2.0;
        var inW = (Math.Max(0, previous.BatteryWatts) + Math.Max(0, current.BatteryWatts)) / 2.0;
        var cpuW = (previous.CpuWatts + current.CpuWatts) / 2.0;
        var gpuW = (previous.GpuWatts + current.GpuWatts) / 2.0;
        var igpuW = (previous.IGpuWatts + current.IGpuWatts) / 2.0;
        var baseW = (previous.BaselineWatts + current.BaselineWatts) / 2.0;

        RollMinuteIfNeeded(current.Timestamp.LocalDateTime);

        _mSeconds += dt;
        _mSystemWh += systemW * hours;
        _mWallWh += wallW * hours;
        _mBattOutWh += outW * hours;
        _mBattInWh += inW * hours;

        _mSystemWs += systemW * dt;
        _mWallWs += wallW * dt;
        _mCpuWs += cpuW * dt;
        _mGpuWs += gpuW * dt;
        _mIGpuWs += igpuW * dt;
        _mBaseWs += baseW * dt;
        _mBattPctS += current.BatteryPercent * dt;

        _mSource = current.Source;
        if (current.Confidence < _mConfidence) _mConfidence = current.Confidence;

        _state.LifetimeSeconds += dt;
        _state.LifetimeSystemWh += systemW * hours;
        _state.LifetimeWallWh += wallW * hours;
        _state.LifetimeBatteryOutWh += outW * hours;
        _state.LifetimeBatteryInWh += inW * hours;

        if (DateTime.UtcNow >= _nextStateSave)
        {
            _state.Save();
            _nextStateSave = DateTime.UtcNow.AddSeconds(60);
        }

        CheckMilestone();
    }

    private void RollMinuteIfNeeded(DateTime localNow)
    {
        var minute = new DateTime(localNow.Year, localNow.Month, localNow.Day,
            localNow.Hour, localNow.Minute, 0, DateTimeKind.Local);

        if (_minute == DateTime.MinValue)
        {
            _minute = minute;
            return;
        }

        if (minute == _minute) return;

        FlushMinute();
        _minute = minute;
    }

    private void FlushMinute()
    {
        if (_mSeconds <= 0)
        {
            ResetMinute();
            return;
        }

        var row = new MinuteRow(
            _minute,
            _mSeconds,
            _mSource,
            _mConfidence,
            _mWallWh,
            _mSystemWh,
            _mBattOutWh,
            _mBattInWh,
            _mSystemWs / _mSeconds,
            _mWallWs / _mSeconds,
            _mCpuWs / _mSeconds,
            _mGpuWs / _mSeconds,
            _mIGpuWs / _mSeconds,
            _mBaseWs / _mSeconds,
            _mBattPctS / _mSeconds);

        _history.AppendMinute(row);
        ResetMinute();

        try
        {
            MinuteFlushed?.Invoke(row);
        }
        catch (Exception ex)
        {
            Log.Error("A minute-flush handler threw", ex);
        }
    }

    private void ResetMinute()
    {
        _mSeconds = _mWallWh = _mSystemWh = _mBattOutWh = _mBattInWh = 0;
        _mSystemWs = _mWallWs = _mCpuWs = _mGpuWs = _mIGpuWs = _mBaseWs = _mBattPctS = 0;
        _mConfidence = Confidence.Measured;
    }

    private void CheckMilestone()
    {
        if (!_settings.Notifications || _settings.NotifyEveryUnits <= 0) return;

        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_state.NotifiedDay != today)
        {
            _state.NotifiedDay = today;
            _state.NotifiedUnits = 0;
        }

        var units = Today().WallUnits;
        var step = _settings.NotifyEveryUnits;
        var crossed = Math.Floor(units / step) * step;

        if (crossed > _state.NotifiedUnits + 1e-9)
        {
            _state.NotifiedUnits = crossed;
            try
            {
                UnitMilestone?.Invoke(crossed);
            }
            catch (Exception ex)
            {
                Log.Error("A milestone handler threw", ex);
            }
        }
    }

    private void WriteRaw(PowerSample s)
    {
        try
        {
            Directory.CreateDirectory(Paths.RawDir);
            var path = Paths.RawFile(s.Timestamp.LocalDateTime);
            var inv = CultureInfo.InvariantCulture;

            if (!File.Exists(path))
            {
                File.AppendAllText(path,
                    "time,source,confidence,system_w,wall_w,cpu_w,igpu_w,gpu_w,base_w,batt_w,batt_pct,brightness\n");
            }

            File.AppendAllText(path, string.Join(',',
                s.Timestamp.ToString("HH:mm:ss", inv),
                s.Source,
                s.Confidence,
                s.SystemWatts.ToString("F2", inv),
                s.WallWatts.ToString("F2", inv),
                s.CpuWatts.ToString("F2", inv),
                s.IGpuWatts.ToString("F2", inv),
                s.GpuWatts.ToString("F2", inv),
                s.BaselineWatts.ToString("F2", inv),
                s.BatteryWatts.ToString("F2", inv),
                s.BatteryPercent.ToString("F1", inv),
                s.BrightnessPercent.ToString(inv)) + "\n");
        }
        catch (Exception ex)
        {
            Log.Error("Raw sample write failed", ex);
        }
    }

    // ------------------------------------------------------------- reporting

    /// <summary>The partially-accumulated minute that is not on disk yet.</summary>
    private DayTotal InFlight()
    {
        lock (_gate)
        {
            return new DayTotal
            {
                Date = _minute.Date,
                WallWh = _mWallWh,
                SystemWh = _mSystemWh,
                BatteryOutWh = _mBattOutWh,
                BatteryInWh = _mBattInWh,
                Seconds = _mSeconds,
                AcSeconds = _mSource is PowerSource.AcIdle or PowerSource.AcCharging ? _mSeconds : 0,
                BatterySeconds = _mSource == PowerSource.Battery ? _mSeconds : 0,
            };
        }
    }

    public DayTotal Day(DateTime localDate)
    {
        var total = _history.GetDay(localDate);
        var live = InFlight();
        if (live.Date == localDate.Date) total.Add(live);
        return total;
    }

    public DayTotal Today() => Day(DateTime.Today);

    public DayTotal Month(int year, int month)
    {
        var total = _history.GetMonth(year, month);
        var live = InFlight();
        if (live.Date.Year == year && live.Date.Month == month) total.Add(live);
        return total;
    }

    public DayTotal ThisMonth() => Month(DateTime.Today.Year, DateTime.Today.Month);

    public List<DayTotal> RecentDays(int count)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(count - 1));
        var days = _history.GetDays(from, to);

        var live = InFlight();
        var match = days.FirstOrDefault(d => d.Date == live.Date);
        match?.Add(live);

        return days;
    }

    public double LifetimeWallUnits => (_state.LifetimeWallWh + InFlight().WallWh) / 1000.0;
    public double LifetimeSystemUnits => (_state.LifetimeSystemWh + InFlight().SystemWh) / 1000.0;

    /// <summary>Writes the in-flight minute out and saves state. Called on exit.</summary>
    public void Flush()
    {
        if (_readOnly) return;

        lock (_gate)
        {
            try
            {
                FlushMinute();
                _state.Save();
            }
            catch (Exception ex)
            {
                Log.Error("Flush failed", ex);
            }
        }
    }

    /// <summary>
    /// Archives the history folder and zeroes the counters. Nothing is deleted:
    /// the old CSVs are moved aside so a mistaken reset stays recoverable.
    /// </summary>
    public string ResetCounters()
    {
        lock (_gate)
        {
            Flush();

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var archive = Path.Combine(Paths.DataDir, "archive-" + stamp);

            try
            {
                if (Directory.Exists(Paths.HistoryDir) &&
                    Directory.EnumerateFileSystemEntries(Paths.HistoryDir).Any())
                {
                    Directory.Move(Paths.HistoryDir, archive);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not archive history during reset", ex);
            }

            Directory.CreateDirectory(Paths.HistoryDir);
            _history.InvalidateCache();

            _state.ResetTotals();
            _state.Save();

            ResetMinute();
            _minute = DateTime.MinValue;

            Log.Info($"Counters reset; previous history archived to {archive}");
            return archive;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;

        _timer?.Dispose();
        _timer = null;

        Flush();

        _battery.Dispose();
        _hardware.Dispose();
    }
}
