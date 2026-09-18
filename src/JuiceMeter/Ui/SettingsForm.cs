using System.Globalization;
using JuiceMeter.Core;

namespace JuiceMeter.Ui;

internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly PowerMonitor _monitor;

    private readonly NumericUpDown _interval = Spin(250, 10_000, 250);
    private readonly CheckBox _hardware = Check("Read CPU and GPU package power (needs administrator for CPU)");
    private readonly CheckBox _raw = Check("Also log every raw sample to raw\\*.csv");

    private readonly NumericUpDown _adapter = Spin(50, 100, 1);
    private readonly NumericUpDown _charge = Spin(50, 100, 1);
    private readonly NumericUpDown _fallback = Spin(1, 100, 1);
    private readonly CheckBox _manualOn = Check("Pin the baseline myself instead of learning it");
    private readonly NumericUpDown _manual = Spin(1, 100, 1);

    private readonly TextBox _currency = new();
    private readonly NumericUpDown _tariff = Spin(0, 1000, 1, decimals: 2);

    private readonly CheckBox _autoStart = Check("Start Juice Meter when I log in");
    private readonly CheckBox _startMinimized = Check("Start hidden in the notification area");
    private readonly CheckBox _trayWatts = Check("Show live watts in the tray icon (otherwise units today)");
    private readonly CheckBox _notify = Check("Notify me as units add up");
    private readonly NumericUpDown _notifyEvery = Spin(0, 100, 1, decimals: 1);

    private readonly Label _calibration = new();

    public SettingsForm(Settings settings, PowerMonitor monitor)
    {
        _settings = settings;
        _monitor = monitor;

        Text = "Juice Meter settings";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(600, 700);
        MinimumSize = new Size(560, 560);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;

        Build();
        LoadValues();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    private void Build()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 14, 18, 14),
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Section(layout, "Sampling");
        Row(layout, "Sample interval (ms)", _interval,
            "One second gives a smooth chart. Longer intervals use less power themselves.");
        Wide(layout, _hardware);
        Wide(layout, _raw);

        Section(layout, "Power model");
        Row(layout, "Adapter efficiency (%)", _adapter,
            "Wall to DC losses in the brick. 88-92% is typical.");
        Row(layout, "Charging efficiency (%)", _charge,
            "DC to cells. Energy lost heating the pack while it charges.");
        Row(layout, "Fallback baseline (W)", _fallback,
            "Used only until a battery session teaches the real number.");
        Wide(layout, _manualOn);
        Row(layout, "Manual baseline (W)", _manual, null);

        Section(layout, "Cost");
        Row(layout, "Currency symbol", _currency, null);
        Row(layout, "Price per unit (kWh)", _tariff,
            "Your tariff. One unit is one kilowatt-hour.");

        Section(layout, "Shell");
        Wide(layout, _autoStart);
        Wide(layout, _startMinimized);
        Wide(layout, _trayWatts);
        Wide(layout, _notify);
        Row(layout, "Notify every (units)", _notifyEvery, "0 turns the running notifications off.");

        Section(layout, "Calibration");

        _calibration.AutoSize = true;
        _calibration.MaximumSize = new Size(540, 0);
        _calibration.ForeColor = Theme.TextDim;
        _calibration.Font = Theme.Small;
        _calibration.Margin = new Padding(0, 2, 0, 8);
        Wide(layout, _calibration);

        var resetCalibration = new Button
        {
            Text = "Forget the learned baseline",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 10),
        };

        resetCalibration.FlatAppearance.BorderColor = Theme.Border;
        resetCalibration.Click += (_, _) =>
        {
            _monitor.State.Calibration.Reset();
            _monitor.State.Save();
            UpdateCalibrationText();
        };

        Wide(layout, resetCalibration);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Panel, Padding = new Padding(18, 12, 18, 12) };

        var ok = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Width = 110,
            Height = 34,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentDim,
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderColor = Theme.Accent;
        ok.Click += OnSave;

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 110,
            Height = 34,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Margin = new Padding(8, 0, 0, 0),
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;

        footer.Controls.Add(cancel);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Panel });
        footer.Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(layout);
        Controls.Add(footer);

        _manualOn.CheckedChanged += (_, _) => _manual.Enabled = _manualOn.Checked;
        _notify.CheckedChanged += (_, _) => _notifyEvery.Enabled = _notify.Checked;
    }

    private void LoadValues()
    {
        _interval.Value = Clamp(_settings.SampleIntervalMs, _interval);
        _hardware.Checked = _settings.EnableHardwareSensors;
        _raw.Checked = _settings.KeepRawSamples;

        _adapter.Value = Clamp((decimal)(_settings.AdapterEfficiency * 100), _adapter);
        _charge.Value = Clamp((decimal)(_settings.ChargeEfficiency * 100), _charge);
        _fallback.Value = Clamp((decimal)_settings.FallbackBaselineWatts, _fallback);

        _manualOn.Checked = _settings.ManualBaselineWatts is not null;
        _manual.Value = Clamp((decimal)(_settings.ManualBaselineWatts ?? _settings.FallbackBaselineWatts), _manual);
        _manual.Enabled = _manualOn.Checked;

        _currency.Text = _settings.CurrencySymbol;
        _tariff.Value = Clamp((decimal)_settings.TariffPerUnit, _tariff);

        _autoStart.Checked = Startup.IsEnabled;
        _startMinimized.Checked = _settings.StartMinimized;
        _trayWatts.Checked = _settings.ShowWattsInTray;
        _notify.Checked = _settings.Notifications;
        _notifyEvery.Value = Clamp((decimal)_settings.NotifyEveryUnits, _notifyEvery);
        _notifyEvery.Enabled = _notify.Checked;

        UpdateCalibrationText();
    }

    private void UpdateCalibrationText()
    {
        var model = _monitor.State.Calibration;

        if (!_monitor.CpuPowerAvailable)
        {
            _calibration.Text =
                "Calibration is paused because CPU package power cannot be read. The leftover after " +
                "CPU and GPU is what gets banked as the baseline, and without the CPU figure that " +
                "leftover is mostly CPU, which would be counted twice on AC. Run Juice Meter as " +
                "administrator to enable it.";
            return;
        }

        if (!model.IsCalibrated)
        {
            _calibration.Text =
                $"Not calibrated yet ({model.GlobalSamples:N0} of 120 samples). " +
                "Unplug and use the laptop on battery for a couple of minutes: the pack reports true " +
                "system power, and whatever is left after CPU and GPU becomes the baseline used on AC.";
            return;
        }

        var lines = new List<string>
        {
            $"Learned from {model.GlobalSamples:N0} battery samples. Overall baseline {model.GlobalWatts:F1} W.",
        };

        foreach (var bucket in model.LearnedBuckets())
        {
            lines.Add($"   brightness {bucket.FromPercent}-{bucket.ToPercent}%: {bucket.Watts:F1} W  ({bucket.Samples:N0} samples)");
        }

        _calibration.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _settings.SampleIntervalMs = (int)_interval.Value;
        _settings.EnableHardwareSensors = _hardware.Checked;
        _settings.KeepRawSamples = _raw.Checked;

        _settings.AdapterEfficiency = (double)_adapter.Value / 100.0;
        _settings.ChargeEfficiency = (double)_charge.Value / 100.0;
        _settings.FallbackBaselineWatts = (double)_fallback.Value;
        _settings.ManualBaselineWatts = _manualOn.Checked ? (double)_manual.Value : null;

        _settings.CurrencySymbol = string.IsNullOrWhiteSpace(_currency.Text) ? "₹" : _currency.Text.Trim();
        _settings.TariffPerUnit = (double)_tariff.Value;

        _settings.StartMinimized = _startMinimized.Checked;
        _settings.ShowWattsInTray = _trayWatts.Checked;
        _settings.Notifications = _notify.Checked;
        _settings.NotifyEveryUnits = (double)_notifyEvery.Value;

        if (_autoStart.Checked != Startup.IsEnabled)
        {
            if (!Startup.SetEnabled(_autoStart.Checked))
            {
                MessageBox.Show(this, "Could not change the run-at-login setting. See juicemeter.log for details.",
                    "Juice Meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        _settings.AutoStart = Startup.IsEnabled;
    }

    // ------------------------------------------------------------- scaffolding

    private static void Section(TableLayoutPanel layout, string title)
    {
        var label = new Label
        {
            Text = title.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Theme.Accent,
            Font = Theme.SmallBold,
            Margin = new Padding(0, 16, 0, 6),
        };

        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
    }

    private static void Row(TableLayoutPanel layout, string label, Control control, string? hint)
    {
        var text = new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 6, 8, 4),
        };

        Style(control);
        control.Margin = new Padding(0, 3, 0, 4);
        if (control is TextBox) control.Width = 140;

        layout.Controls.Add(text);
        layout.Controls.Add(control);

        if (hint is null) return;

        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = Theme.TextFaint,
            Font = Theme.Small,
            Margin = new Padding(0, 0, 0, 8),
        };

        layout.Controls.Add(hintLabel);
        layout.SetColumnSpan(hintLabel, 2);
    }

    private static void Wide(TableLayoutPanel layout, Control control)
    {
        Style(control);
        layout.Controls.Add(control);
        layout.SetColumnSpan(control, 2);
    }

    private static void Style(Control control)
    {
        switch (control)
        {
            case NumericUpDown spin:
                spin.BackColor = Theme.PanelHi;
                spin.ForeColor = Theme.Text;
                spin.BorderStyle = BorderStyle.FixedSingle;
                spin.Width = 120;
                break;

            case TextBox box:
                box.BackColor = Theme.PanelHi;
                box.ForeColor = Theme.Text;
                box.BorderStyle = BorderStyle.FixedSingle;
                break;

            case CheckBox check:
                check.ForeColor = Theme.Text;
                check.AutoSize = true;
                check.Margin = new Padding(0, 4, 0, 4);
                break;
        }
    }

    private static NumericUpDown Spin(decimal min, decimal max, decimal step, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = decimals,
        TextAlign = HorizontalAlignment.Right,
    };

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    private static decimal Clamp(decimal value, NumericUpDown spin) =>
        Math.Clamp(value, spin.Minimum, spin.Maximum);

    private static decimal Clamp(int value, NumericUpDown spin) =>
        Math.Clamp((decimal)value, spin.Minimum, spin.Maximum);

    private static string N(double value) => value.ToString("F2", CultureInfo.CurrentCulture);
}
