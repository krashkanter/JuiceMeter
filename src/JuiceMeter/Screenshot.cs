using System.Drawing.Imaging;
using JuiceMeter.Core;
using JuiceMeter.Ui;

namespace JuiceMeter;

/// <summary>
/// Renders the dashboard to a PNG and exits. Handy for bug reports about the
/// UI, and for checking a layout change without disturbing a running copy.
///
/// The meter it builds is read-only, so nothing is written to the ledger: a
/// screenshot must never add phantom watt-hours to the bill.
/// </summary>
internal static class Screenshot
{
    public static int Run(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        var path = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "juicemeter.png");


        ApplicationConfiguration.Initialize();

        if (args.Any(a => a.Equals("--light", StringComparison.OrdinalIgnoreCase))) Theme.Force(true);
        else if (args.Any(a => a.Equals("--dark", StringComparison.OrdinalIgnoreCase))) Theme.Force(false);

        var settings = Settings.Load();
        var state = AppState.Load();

        using var monitor = new PowerMonitor(settings, state, new HistoryStore(), readOnly: true);
        monitor.Start();

        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            using var dialog = new SettingsForm(settings, monitor)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
            };

            dialog.Show();
            for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }

            using var shot = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(shot, new Rectangle(0, 0, dialog.Width, dialog.Height));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            shot.Save(path, ImageFormat.Png);

            dialog.Close();
            return 0;
        }

        using var form = new MainForm(monitor, settings)
        {
            StartPosition = FormStartPosition.Manual,
            // Far enough off-screen that nothing flashes up in front of anyone.
            Location = new Point(-20000, -20000),
            ShowInTaskbar = false,
        };

        form.Show();

        // Let a couple of samples land so the chart and cards have something in
        // them, pumping messages so the marshalled updates actually run.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        // WM_PRINT on a Form includes the frame, so the bitmap has to be the
        // whole window, not just the client area, or the bottom gets cropped.
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);

        form.ExitRequested = true;
        form.Close();

        Log.Info($"Screenshot written to {path}");
        return 0;
    }
}
