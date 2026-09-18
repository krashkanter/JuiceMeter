using System.Globalization;
using JuiceMeter.Core;
using JuiceMeter.Ui;

namespace JuiceMeter;

/// <summary>
/// The notification-area applet. Owns the meter, the tray icon and the window,
/// and keeps running with no window open.
/// </summary>
internal sealed class JuiceMeterContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly AppState _state;
    private readonly PowerMonitor _monitor;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _todayItem;
    private readonly ToolStripMenuItem _monthItem;
    private readonly ToolStripMenuItem _nowItem;
    private readonly ToolStripMenuItem _autoStartItem;

    private Ui.MainForm? _window;
    private IntPtr _iconHandle = IntPtr.Zero;
    private string _iconText = string.Empty;
    private Color _iconAccent = Color.Empty;
    private bool _exiting;

    public JuiceMeterContext(Settings settings, AppState state, bool startHidden)
    {
        _settings = settings;
        _state = state;

        _monitor = new PowerMonitor(settings, state, new HistoryStore());

        _nowItem = new ToolStripMenuItem("Starting up...") { Enabled = false };
        _todayItem = new ToolStripMenuItem("Today: -") { Enabled = false };
        _monthItem = new ToolStripMenuItem("This month: -") { Enabled = false };
        _autoStartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };

        _menu = BuildMenu();

        _tray = new NotifyIcon
        {
            Text = "Juice Meter",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = LoadAppIcon(),
        };

        _tray.DoubleClick += (_, _) => ShowWindow();

        _monitor.Sampled += OnSampled;
        _monitor.UnitMilestone += OnMilestone;
        _monitor.Start();

        if (!startHidden) ShowWindow();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(JuiceMeterContext).Assembly.GetManifestResourceStream("JuiceMeter.AppIcon");
            if (stream is not null) return new Icon(stream, TrayIconRenderer.IconSize, TrayIconRenderer.IconSize);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the tray icon", ex);
        }

        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
        };

        var open = new ToolStripMenuItem("Open dashboard", null, (_, _) => ShowWindow())
        {
            Font = new Font(Theme.Body, FontStyle.Bold),
        };

        _autoStartItem.Checked = Startup.IsEnabled;
        _autoStartItem.Click += (_, _) =>
        {
            if (!Startup.SetEnabled(_autoStartItem.Checked))
            {
                _autoStartItem.Checked = Startup.IsEnabled;
            }
            _settings.AutoStart = Startup.IsEnabled;
            _settings.Save();
        };

        menu.Items.AddRange(new ToolStripItem[]
        {
            _nowItem,
            new ToolStripSeparator(),
            _todayItem,
            _monthItem,
            new ToolStripSeparator(),
            open,
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()),
            _autoStartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        });

        menu.Opening += (_, _) => RefreshMenu();
        return menu;
    }

    private void RefreshMenu()
    {
        var sample = _monitor.Latest;
        var watts = sample.OnAc ? sample.WallWatts : sample.SystemWatts;

        _nowItem.Text = sample.Source == PowerSource.Unknown
            ? "Waiting for a reading"
            : $"{watts:F1} W  ·  {Theme.LabelFor(sample.Source).ToLowerInvariant()}";

        var today = _monitor.Today();
        var month = _monitor.ThisMonth();

        _todayItem.Text = $"Today: {Format.Units(today.WallUnits)} units  ({Money(today.WallUnits)})";
        _monthItem.Text = $"This month: {Format.Units(month.WallUnits)} units  ({Money(month.WallUnits)})";
        _autoStartItem.Checked = Startup.IsEnabled;
    }

    private string Money(double units) =>
        _settings.TariffPerUnit > 0
            ? $"{_settings.CurrencySymbol}{units * _settings.TariffPerUnit:F2}"
            : "no tariff set";

    private void OnSampled(PowerSample sample)
    {
        try
        {
            var watts = sample.OnAc ? sample.WallWatts : sample.SystemWatts;

            var text = _settings.ShowWattsInTray
                ? TrayIconRenderer.FormatWatts(watts)
                : TrayIconRenderer.FormatUnits(_monitor.Today().WallUnits);

            var accent = Theme.ForSource(sample.Source);

            if (text != _iconText || accent != _iconAccent)
            {
                _iconText = text;
                _iconAccent = accent;
                UpdateTrayIcon(text, accent);
            }

            _tray.Text = BuildTooltip(sample);
        }
        catch (Exception ex)
        {
            Log.Error("Tray update failed", ex);
        }
    }

    private void UpdateTrayIcon(string text, Color accent)
    {
        var icon = TrayIconRenderer.Render(text, accent);
        var previous = _iconHandle;

        _tray.Icon = icon;
        _iconHandle = icon.Handle;

        // GetHicon handles are not reclaimed by Icon.Dispose, and this runs
        // every time the number changes, so the old one has to go explicitly.
        if (previous != IntPtr.Zero) Native.DestroyIcon(previous);
    }

    private string BuildTooltip(PowerSample sample)
    {
        // NotifyIcon.Text is capped at 63 characters.
        var units = Format.Units(_monitor.Today().WallUnits);
        var watts = sample.OnAc ? sample.WallWatts : sample.SystemWatts;

        var text = sample.Source switch
        {
            PowerSource.Battery => $"{watts:F1} W on battery {sample.BatteryPercent:F0}%",
            PowerSource.AcCharging => $"{watts:F1} W charging {sample.BatteryPercent:F0}%",
            PowerSource.AcIdle => $"{watts:F1} W from the wall",
            _ => "Waiting for a reading",
        };

        var full = $"Juice Meter\n{text}\nToday {units} units";
        return full.Length <= 63 ? full : full[..63];
    }

    private void OnMilestone(double units)
    {
        if (!_settings.Notifications) return;

        try
        {
            var money = _settings.TariffPerUnit > 0
                ? $" That is about {_settings.CurrencySymbol}{units * _settings.TariffPerUnit:F2}."
                : string.Empty;

            _tray.BalloonTipTitle = "Juice Meter";
            _tray.BalloonTipText =
                $"{units.ToString("0.##", CultureInfo.CurrentCulture)} units drawn from the wall today.{money}";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the milestone notification", ex);
        }
    }

    public void ShowWindow()
    {
        try
        {
            if (_window is null || _window.IsDisposed)
            {
                _window = new Ui.MainForm(_monitor, _settings);
                _window.FormClosed += (_, _) => _window = null;
            }

            _window.Show();

            if (_window.WindowState == FormWindowState.Minimized)
            {
                _window.WindowState = FormWindowState.Normal;
            }

            _window.BringToFront();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the main window", ex);
        }
    }

    private void ShowSettings()
    {
        ShowWindow();

        using var dialog = new SettingsForm(_settings, _monitor);
        if (dialog.ShowDialog(_window) != DialogResult.OK) return;

        _settings.Save();
        _monitor.Reconfigure();

        // Force the tray icon to repaint with the new mode.
        _iconText = string.Empty;
    }

    private void Quit()
    {
        _exiting = true;

        if (_window is { IsDisposed: false })
        {
            _window.ExitRequested = true;
            _window.Close();
        }

        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _monitor.Sampled -= OnSampled;
            _monitor.UnitMilestone -= OnMilestone;
            _monitor.Dispose();

            _tray.Visible = false;
            _tray.Dispose();

            if (_iconHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }

            _menu.Dispose();
            _settings.Save();

            Log.Info(_exiting ? "Quit from the tray menu" : "Shutting down");
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown was not clean", ex);
        }

        base.ExitThreadCore();
    }

    /// <summary>Flat dark menu so the applet matches the dashboard.</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColours()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        private sealed class DarkColours : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Panel;
            public override Color MenuItemSelected => Theme.PanelHi;
            public override Color MenuItemSelectedGradientBegin => Theme.PanelHi;
            public override Color MenuItemSelectedGradientEnd => Theme.PanelHi;
            public override Color MenuItemBorder => Theme.AccentDim;
            public override Color MenuBorder => Theme.Border;
            public override Color ImageMarginGradientBegin => Theme.Panel;
            public override Color ImageMarginGradientMiddle => Theme.Panel;
            public override Color ImageMarginGradientEnd => Theme.Panel;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
        }
    }
}
