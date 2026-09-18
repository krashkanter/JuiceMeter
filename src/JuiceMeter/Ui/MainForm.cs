using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using JuiceMeter.Core;

namespace JuiceMeter.Ui;

internal sealed class MainForm : Form
{
    private readonly PowerMonitor _monitor;
    private readonly Settings _settings;

    private readonly HeaderPanel _header;
    private readonly SidePanel _side;
    private readonly SparklineControl _spark = new();
    private readonly BreakdownBar _breakdown = new();
    private readonly StatCard _today = new();
    private readonly StatCard _month = new();
    private readonly StatCard _lifetime = new();
    private readonly StatCard _battery = new();
    private readonly Label _status = new();
    private readonly CheckBox _runOnStartup = new();
    private readonly Panel _banner = new();

    private DateTime _nextTotalsRefresh = DateTime.MinValue;
    private bool _suppressStartupEvent;

    public bool ExitRequested { get; set; }

    public MainForm(PowerMonitor monitor, Settings settings)
    {
        _monitor = monitor;
        _settings = settings;
        _header = new HeaderPanel();
        _side = new SidePanel(monitor, settings);

        Text = "Juice Meter";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(1000, 752);
        MinimumSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        TryLoadIcon();
        BuildLayout();

        _monitor.Sampled += OnSampled;
        Theme.Changed += OnThemeChanged;

        RefreshTotals();
    }

    private void TryLoadIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("JuiceMeter.AppIcon");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the window icon", ex);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetTitleBarTheme(Handle, !Theme.IsLight);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) SyncStartupCheckbox();
    }

    private void BuildLayout()
    {
        var chartArea = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _breakdown.Dock = DockStyle.Bottom;
        _breakdown.Height = 120;
        _spark.Dock = DockStyle.Fill;
        _spark.Caption = "Power draw, last 15 minutes";
        _spark.Capacity = SparkCapacity();

        chartArea.Controls.Add(_spark);
        chartArea.Controls.Add(Spacer(DockStyle.Bottom, 12));
        chartArea.Controls.Add(_breakdown);

        _side.Dock = DockStyle.Right;
        _side.Width = 320;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(16, 0, 16, 0) };
        body.Controls.Add(chartArea);
        body.Controls.Add(Spacer(DockStyle.Right, 12));
        body.Controls.Add(_side);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 106,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 0, 16, 12),
        };

        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

        foreach (var card in new[] { _today, _month, _lifetime, _battery })
        {
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 12, 0);
        }

        _battery.Margin = new Padding(0);
        cards.Controls.AddRange(new Control[] { _today, _month, _lifetime, _battery });

        _header.Dock = DockStyle.Top;
        _header.Height = 104;

        BuildFooter(out var footer);
        BuildBanner();

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(cards);
        Controls.Add(_header);
        Controls.Add(_banner);
    }

    private int SparkCapacity() =>
        Math.Max(120, (int)(TimeSpan.FromMinutes(15).TotalMilliseconds / _settings.SampleIntervalMs));

    private void BuildFooter(out Panel footer)
    {
        footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 12, 16, 14),
        };

        _runOnStartup.Text = "Run on startup";
        _runOnStartup.AutoSize = true;
        _runOnStartup.Dock = DockStyle.Left;
        _runOnStartup.ForeColor = Theme.Text;
        _runOnStartup.BackColor = Color.Transparent;
        _runOnStartup.Padding = new Padding(0, 7, 0, 0);
        _runOnStartup.CheckedChanged += OnRunOnStartupChanged;
        SyncStartupCheckbox();

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Theme.TextFaint;
        _status.Font = Theme.Small;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(18, 0, 0, 0);

        footer.Controls.Add(_status);
        footer.Controls.Add(_runOnStartup);

        foreach (var button in new[]
                 {
                     MakeButton("Settings", OnSettings, primary: true),
                     MakeButton("Open data folder", (_, _) => OpenDataFolder()),
                     MakeButton("Reset counters", OnReset),
                     MakeButton("Hide to tray", (_, _) => Hide()),
                 })
        {
            button.Dock = DockStyle.Right;
            footer.Controls.Add(button);
            footer.Controls.Add(Spacer(DockStyle.Right, 8));
        }
    }

    private void SyncStartupCheckbox()
    {
        _suppressStartupEvent = true;
        _runOnStartup.Checked = Startup.IsEnabled;
        _suppressStartupEvent = false;
    }

    private void OnRunOnStartupChanged(object? sender, EventArgs e)
    {
        if (_suppressStartupEvent) return;

        if (!Startup.SetEnabled(_runOnStartup.Checked))
        {
            MessageBox.Show(this,
                "Could not change the run-at-login setting. See juicemeter.log for details.",
                "Juice Meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            SyncStartupCheckbox();
            return;
        }

        _settings.AutoStart = Startup.IsEnabled;
        _settings.Save();
    }

    private void BuildBanner()
    {
        _banner.Dock = DockStyle.Top;
        _banner.Height = 0;
        _banner.BackColor = Theme.IsLight ? Color.FromArgb(255, 244, 222) : Color.FromArgb(56, 44, 22);
        _banner.Padding = new Padding(18, 6, 12, 6);
        _banner.Visible = false;

        // CPU package power is the one that matters: without it the baseline
        // cannot be calibrated, so AC figures stay coarse.
        if (_monitor.CpuPowerAvailable || !_settings.EnableHardwareSensors) return;

        var message = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Accent,
            Font = Theme.Small,
            Tag = "accent",
            TextAlign = ContentAlignment.MiddleLeft,
            Text = Sensors.HardwareReader.IsElevated
                ? $"CPU package power unavailable: {_monitor.HardwareStatus}. Battery readings are unaffected, but AC figures stay estimates."
                : "Run as administrator to read CPU package power. Without it the baseline cannot calibrate and AC figures stay rough estimates.",
        };

        var action = new Button
        {
            Dock = DockStyle.Right,
            Width = 152,
            Text = Sensors.HardwareReader.IsElevated ? "Dismiss" : "Restart as admin",
        };

        Theme.StyleButton(action);
        action.Font = Theme.SmallBold;

        action.Click += (_, _) =>
        {
            if (Sensors.HardwareReader.IsElevated)
            {
                _banner.Visible = false;
                _banner.Height = 0;
            }
            else
            {
                RestartElevated();
            }
        };

        _banner.Controls.Add(message);
        _banner.Controls.Add(action);
        _banner.Height = 40;
        _banner.Visible = true;
    }

    private void RestartElevated()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
            });

            ExitRequested = true;
            Application.Exit();
        }
        catch (Exception ex)
        {
            // The user cancelling the UAC prompt lands here, which is fine.
            Log.Info("Elevated restart did not happen: " + ex.Message);
        }
    }

    private static Control Spacer(DockStyle dock, int size) => new Panel
    {
        Dock = dock,
        Width = size,
        Height = size,
        BackColor = Theme.Bg,
    };

    private static Button MakeButton(string text, EventHandler onClick, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Width = TextRenderer.MeasureText(text, Theme.Body).Width + 32,
            Height = 34,
        };

        Theme.StyleButton(button, primary);
        button.Click += onClick;

        return button;
    }

    // --------------------------------------------------------------- updates

    private void OnThemeChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;

                BackColor = Theme.Bg;
                ForeColor = Theme.Text;
                _banner.BackColor = Theme.IsLight ? Color.FromArgb(255, 244, 222) : Color.FromArgb(56, 44, 22);
                _status.ForeColor = Theme.TextFaint;

                Theme.ApplyTo(this);
                Native.SetTitleBarTheme(Handle, !Theme.IsLight);
                Invalidate(true);
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnSampled(PowerSample sample)
    {
        if (!IsHandleCreated || IsDisposed) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;

                _header.Update(sample, _monitor);
                _spark.Push(sample.SystemWatts, sample.WallWatts, sample.OnAc);
                _breakdown.Update(sample.CpuWatts, sample.GpuWatts, sample.BaselineWatts,
                    sample.CpuSensorLive, sample.GpuSensorLive, _monitor.GpuSwitchedOff);

                if (DateTime.UtcNow >= _nextTotalsRefresh)
                {
                    _nextTotalsRefresh = DateTime.UtcNow.AddSeconds(2);
                    RefreshTotals();
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // The form went away between the check and the marshal.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed mid-flight.
        }
    }

    private void RefreshTotals()
    {
        var today = _monitor.Today();
        var month = _monitor.ThisMonth();
        var sample = _monitor.Latest;

        _today.Set("TODAY",
            Format.Units(today.WallUnits),
            $"{Money(today.WallUnits)}   avg {today.AverageWallWatts:F0} W");

        _month.Set(DateTime.Today.ToString("MMMM", CultureInfo.CurrentCulture).ToUpperInvariant(),
            Format.Units(month.WallUnits),
            $"{Money(month.WallUnits)}   {Format.Hours(month.Seconds)} metered");

        _lifetime.Set("LIFETIME",
            Format.Units(_monitor.LifetimeWallUnits),
            $"{Money(_monitor.LifetimeWallUnits)}   since {_monitor.State.FirstRunUtc.ToLocalTime():d MMM yyyy}");

        if (sample.BatteryPresent)
        {
            var health = sample.BatteryHealthPercent;
            _battery.Set("BATTERY",
                $"{sample.BatteryPercent:F0}%",
                double.IsFinite(health) && health > 0
                    ? $"{sample.RemainingWh:F1} / {sample.FullChargeWh:F1} Wh   {health:F0}% health"
                    : $"{sample.RemainingWh:F1} Wh");
        }
        else
        {
            _battery.Set("BATTERY", "None", "No system battery detected");
        }

        _side.Refresh();

        var gap = _monitor.State.LifetimeGapSeconds;
        _status.Text = $"Metered {Format.Hours(_monitor.State.LifetimeSeconds)}"
                       + (gap > 60 ? $"   {Format.Hours(gap)} skipped while asleep" : string.Empty);
    }

    private string Money(double units) => Format.Money(units, _settings);

    // --------------------------------------------------------------- actions

    private void OnSettings(object? sender, EventArgs e)
    {
        using var dialog = new SettingsForm(_settings, _monitor);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.Save();
        _monitor.Reconfigure();
        _spark.Capacity = SparkCapacity();
        SyncStartupCheckbox();
        RefreshTotals();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Zero every counter and start fresh?\n\n" +
            "Your existing history is not deleted — it is moved into an archive folder " +
            "next to the data, so you can still get at it.",
            "Reset counters",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK) return;

        var archive = _monitor.ResetCounters();
        _spark.Clear();
        RefreshTotals();

        MessageBox.Show(this, $"Counters reset.\n\nPrevious history archived to:\n{archive}",
            "Juice Meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Paths.DataDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the data folder", ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window parks it in the tray; quitting is done from there.
        if (!ExitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _monitor.Sampled -= OnSampled;
        Theme.Changed -= OnThemeChanged;
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------ subcontrols

    private sealed class HeaderPanel : Control
    {
        private PowerSample _sample = PowerSample.Empty;
        private string _subtitle = string.Empty;

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Update(PowerSample sample, PowerMonitor monitor)
        {
            _sample = sample;

            if (_subtitle.Length == 0)
            {
                var parts = new List<string>();
                if (monitor.CpuName is { Length: > 0 } cpu) parts.Add(cpu);
                if (monitor.GpuName is { Length: > 0 } gpu) parts.Add(gpu);
                if (parts.Count == 0 && monitor.BatteryName is { Length: > 0 } pack) parts.Add("Battery " + pack);
                _subtitle = string.Join("   –   ", parts);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var background = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            var bounds = new RectangleF(16, 0, Width - 32, Height - 12);
            Theme.FillCard(g, bounds);

            using var title = new SolidBrush(Theme.Text);
            using var dim = new SolidBrush(Theme.TextDim);

            g.DrawString("Juice Meter", Theme.Title, title, 30, 14);
            if (_subtitle.Length > 0) g.DrawString(_subtitle, Theme.Small, dim, 32, 38);

            var sourceLabel = Theme.LabelFor(_sample.Source);
            Theme.DrawChip(g, sourceLabel, new PointF(32, 62), Theme.ForSource(_sample.Source));

            var chipWidth = Theme.ChipSize(g, sourceLabel).Width;
            Theme.DrawChip(g, Theme.LabelFor(_sample.Confidence), new PointF(32 + chipWidth + 8, 62),
                Theme.ForConfidence(_sample.Confidence));

            // Live wattage, right aligned.
            var watts = _sample.OnAc ? _sample.WallWatts : _sample.SystemWatts;
            var value = watts.ToString("F1", CultureInfo.CurrentCulture);

            var valueSize = g.MeasureString(value, Theme.Huge);
            var unitSize = g.MeasureString("W", Theme.Title);
            var right = bounds.Right - 24;

            g.DrawString("W", Theme.Title, dim, right - unitSize.Width, 38);
            g.DrawString(value, Theme.Huge, title, right - unitSize.Width - valueSize.Width + 8, 10);

            var caption = _sample.OnAc ? "from the wall" : "from the battery";
            var captionSize = g.MeasureString(caption, Theme.Small);
            g.DrawString(caption, Theme.Small, dim, right - captionSize.Width, 70);

            string? detail = null;

            if (_sample.OnAc && _sample.SystemWatts > 0)
            {
                detail = $"system {_sample.SystemWatts:F1} W";
                if (_sample.Source == PowerSource.AcCharging && _sample.BatteryWatts > 0)
                {
                    detail += $"   charging {_sample.BatteryWatts:F1} W";
                }
            }
            else if (_sample.Runtime is { } runtime && runtime.TotalMinutes > 0)
            {
                detail = $"{Format.Hours(runtime.TotalSeconds)} remaining";
            }

            if (detail is not null)
            {
                var detailSize = g.MeasureString(detail, Theme.Small);
                g.DrawString(detail, Theme.Small, dim, right - detailSize.Width, 86);
            }
        }
    }

    private sealed class SidePanel : Control
    {
        private readonly PowerMonitor _monitor;
        private readonly Settings _settings;
        private List<DayTotal> _days = new();

        public SidePanel(PowerMonitor monitor, Settings settings)
        {
            _monitor = monitor;
            _settings = settings;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public override void Refresh()
        {
            _days = _monitor.RecentDays(7);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var background = new SolidBrush(Theme.Bg))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            Theme.FillCard(g, new RectangleF(0, 0, Width - 1, Height - 1));

            using var heading = new SolidBrush(Theme.TextDim);
            using var text = new SolidBrush(Theme.Text);
            using var faint = new SolidBrush(Theme.TextFaint);

            g.DrawString("Last 7 days", Theme.SmallBold, heading, 14, 12);

            var peak = 0.001;
            foreach (var day in _days) peak = Math.Max(peak, day.WallUnits);

            var y = 36f;
            foreach (var day in _days)
            {
                var isToday = day.Date == DateTime.Today;
                var label = isToday ? "Today" : day.Date.ToString("ddd d", CultureInfo.CurrentCulture);

                g.DrawString(label, isToday ? Theme.SmallBold : Theme.Small, isToday ? text : faint, 14, y);

                var track = new RectangleF(72, y + 4, Width - 72 - 84, 9);
                using (var trackBrush = new SolidBrush(Theme.Grid))
                using (var path = Theme.RoundedRect(track, 2f))
                {
                    g.FillPath(trackBrush, path);
                }

                var fraction = (float)(day.WallUnits / peak);
                if (fraction > 0.005f)
                {
                    var fill = new RectangleF(track.X, track.Y, Math.Max(3f, track.Width * fraction), track.Height);
                    using var fillBrush = new SolidBrush(isToday ? Theme.Accent : Color.FromArgb(130, Theme.Accent));
                    using var path = Theme.RoundedRect(fill, 2f);
                    g.FillPath(fillBrush, path);
                }

                var units = Format.Units(day.WallUnits);
                var unitsSize = g.MeasureString(units, Theme.Small);
                g.DrawString(units, Theme.Small, isToday ? text : faint, Width - 16 - unitsSize.Width, y);

                y += 23;
            }

            y += 12;
            using (var divider = new Pen(Theme.Border))
            {
                g.DrawLine(divider, 14, y, Width - 14, y);
            }
            y += 14;

            g.DrawString("Details", Theme.SmallBold, heading, 14, y);
            y += 22;

            var sample = _monitor.Latest;
            var calibration = _monitor.State.Calibration;

            Row(g, ref y, "Baseline", calibration.IsCalibrated
                ? $"{sample.BaselineWatts:F1} W learned"
                : $"{sample.BaselineWatts:F1} W (uncalibrated)");

            Row(g, ref y, "Calibration", !_monitor.CpuPowerAvailable
                ? "paused, needs admin"
                : calibration.IsCalibrated
                    ? $"{calibration.GlobalSamples:N0} samples"
                    : $"{calibration.GlobalSamples:N0} / 120 samples");

            if (_monitor.GpuSwitchedOff) Row(g, ref y, "Discrete GPU", "off (Eco)");
            if (sample.BrightnessPercent >= 0) Row(g, ref y, "Brightness", $"{sample.BrightnessPercent}%");
            if (sample.BatteryVolts > 0) Row(g, ref y, "Pack voltage", $"{sample.BatteryVolts:F2} V");

            if (sample.BatteryPresent)
            {
                Row(g, ref y, "Pack energy", $"{sample.RemainingWh:F1} Wh");
                Row(g, ref y, "Battery in", $"{_monitor.State.LifetimeBatteryInWh / 1000.0:F3} units");
                Row(g, ref y, "Battery out", $"{_monitor.State.LifetimeBatteryOutWh / 1000.0:F3} units");
            }

            Row(g, ref y, "Tariff", $"{_settings.CurrencySymbol}{_settings.TariffPerUnit:F2} / unit");
            Row(g, ref y, "Adapter", $"{_settings.AdapterEfficiency * 100:F0}% efficient");

            if (_monitor.BatteryManufacturer is { Length: > 0 } maker)
            {
                Row(g, ref y, "Pack", $"{maker} {_monitor.BatteryName}".Trim());
            }
        }

        private void Row(Graphics g, ref float y, string label, string value)
        {
            if (y > Height - 24) return;

            using var labelBrush = new SolidBrush(Theme.TextFaint);
            using var valueBrush = new SolidBrush(Theme.Text);

            g.DrawString(label, Theme.Small, labelBrush, 14, y);

            var maxWidth = Width - 28 - 92;
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString(value, Theme.Small, valueBrush,
                new RectangleF(Width - 14 - maxWidth, y, maxWidth, 18), right);

            y += 19;
        }
    }
}
