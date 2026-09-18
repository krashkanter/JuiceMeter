using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace JuiceMeter.Ui;

/// <summary>
/// Flat surfaces, one accent, and whatever the system theme says. No gradients
/// anywhere on purpose: the app should look like it belongs next to G-Helper in
/// the tray, not like a dashboard template.
/// </summary>
internal static class Theme
{
    public static bool IsLight { get; private set; }

    public static Color Bg { get; private set; }
    public static Color Card { get; private set; }
    public static Color CardHover { get; private set; }
    public static Color Border { get; private set; }
    public static Color Grid { get; private set; }

    public static Color Text { get; private set; }
    public static Color TextDim { get; private set; }
    public static Color TextFaint { get; private set; }

    public static Color Accent { get; private set; }
    public static Color OnAccent { get; private set; }

    public static Color Cpu { get; private set; }

    /// <summary>Integrated graphics: a slice of the CPU package, so a colour next to it.</summary>
    public static Color IGpu { get; private set; }

    public static Color Gpu { get; private set; }
    public static Color Rest { get; private set; }

    public static Color Good { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Charge { get; private set; }

    /// <summary>Raised when the system flips between light and dark.</summary>
    public static event Action? Changed;

    public const int Radius = 8;

    private const string Face = "Segoe UI";

    public static readonly Font Body = new(Face, 9f);
    public static readonly Font BodyBold = new(Face, 9f, FontStyle.Bold);
    public static readonly Font Small = new(Face, 8.25f);
    public static readonly Font SmallBold = new(Face, 8.25f, FontStyle.Bold);
    public static readonly Font Title = new(Face, 12f, FontStyle.Bold);
    public static readonly Font CardValue = new(Face, 20f);
    public static readonly Font Huge = new(Face, 30f);

    static Theme()
    {
        Apply(DetectLight());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) Refresh();
        };
    }

    public static void Refresh()
    {
        if (_forced) return;

        var light = DetectLight();
        if (light == IsLight) return;

        Apply(light);
        Changed?.Invoke();
    }

    private static bool _forced;

    /// <summary>Pins the palette regardless of the system setting. Screenshot mode only.</summary>
    public static void Force(bool light)
    {
        _forced = true;
        if (light == IsLight) return;

        Apply(light);
        Changed?.Invoke();
    }

    private static bool DetectLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply(bool light)
    {
        IsLight = light;

        if (light)
        {
            Bg = Color.FromArgb(243, 243, 243);
            Card = Color.FromArgb(255, 255, 255);
            CardHover = Color.FromArgb(237, 237, 237);
            Border = Color.FromArgb(222, 222, 222);
            Grid = Color.FromArgb(232, 232, 232);

            Text = Color.FromArgb(26, 26, 26);
            TextDim = Color.FromArgb(94, 94, 94);
            TextFaint = Color.FromArgb(138, 138, 138);

            Accent = Color.FromArgb(214, 126, 0);
            OnAccent = Color.White;

            Cpu = Color.FromArgb(0, 103, 192);
            IGpu = Color.FromArgb(0, 140, 152);
            Gpu = Color.FromArgb(16, 137, 62);
            Rest = Color.FromArgb(122, 106, 190);

            Good = Color.FromArgb(16, 137, 62);
            Warn = Color.FromArgb(196, 89, 17);
            Charge = Color.FromArgb(0, 130, 114);
        }
        else
        {
            Bg = Color.FromArgb(32, 32, 32);
            Card = Color.FromArgb(43, 43, 43);
            CardHover = Color.FromArgb(56, 56, 56);
            Border = Color.FromArgb(61, 61, 61);
            Grid = Color.FromArgb(56, 56, 56);

            Text = Color.FromArgb(240, 240, 240);
            TextDim = Color.FromArgb(162, 162, 162);
            TextFaint = Color.FromArgb(124, 124, 124);

            Accent = Color.FromArgb(255, 176, 46);
            OnAccent = Color.FromArgb(28, 22, 8);

            Cpu = Color.FromArgb(96, 165, 250);
            IGpu = Color.FromArgb(56, 195, 205);
            Gpu = Color.FromArgb(110, 206, 126);
            Rest = Color.FromArgb(167, 148, 240);

            Good = Color.FromArgb(110, 206, 126);
            Warn = Color.FromArgb(255, 145, 100);
            Charge = Color.FromArgb(94, 205, 180);
        }
    }

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
        var d = radius * 2f;

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>
    /// Device pixels per layout unit. Fonts are in points so GDI+ already scales
    /// them with the monitor, but hand-drawn coordinates do not scale by
    /// themselves: every literal offset in a custom-painted control has to be
    /// multiplied by this or the layout falls apart above 100%.
    /// </summary>
    public static float Scale(Graphics g) => g.DpiY / 96f;

    /// <summary>Flat panel with a hairline border. The only surface treatment used.</summary>
    public static void FillCard(Graphics g, RectangleF bounds, Color? fill = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedRect(bounds, Radius * Scale(g));
        using var brush = new SolidBrush(fill ?? Card);
        using var pen = new Pen(Border);

        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    /// <summary>A quiet status chip, borrowed from the way G-Helper labels modes.</summary>
    public static void DrawChip(Graphics g, string text, PointF location, Color colour)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var s = Scale(g);
        var bounds = new RectangleF(location, ChipSize(g, text));

        using var path = RoundedRect(bounds, 4f * s);
        using var fill = new SolidBrush(Color.FromArgb(IsLight ? 28 : 46, colour));
        using var pen = new Pen(Color.FromArgb(IsLight ? 90 : 120, colour));
        using var textBrush = new SolidBrush(colour);

        g.FillPath(fill, path);
        g.DrawPath(pen, path);
        g.DrawString(text, SmallBold, textBrush, bounds.X + 8f * s, bounds.Y + 3f * s);
    }

    public static SizeF ChipSize(Graphics g, string text)
    {
        var s = Scale(g);
        var size = g.MeasureString(text, SmallBold);
        return new SizeF(size.Width + 16f * s, size.Height + 6f * s);
    }

    public static Color ForSource(Core.PowerSource source) => source switch
    {
        Core.PowerSource.Battery => Accent,
        Core.PowerSource.AcCharging => Charge,
        Core.PowerSource.AcIdle => Cpu,
        _ => TextFaint,
    };

    public static string LabelFor(Core.PowerSource source) => source switch
    {
        Core.PowerSource.Battery => "ON BATTERY",
        Core.PowerSource.AcCharging => "CHARGING",
        Core.PowerSource.AcIdle => "ON AC",
        _ => "UNKNOWN",
    };

    public static Color ForConfidence(Core.Confidence confidence) => confidence switch
    {
        Core.Confidence.Measured => Good,
        Core.Confidence.Modelled => Cpu,
        Core.Confidence.Estimated => Warn,
        _ => TextFaint,
    };

    public static string LabelFor(Core.Confidence confidence) => confidence switch
    {
        Core.Confidence.Measured => "MEASURED",
        Core.Confidence.Modelled => "MODELLED",
        Core.Confidence.Estimated => "ESTIMATED",
        _ => "NO DATA",
    };

    /// <summary>Walks a control tree re-applying theme colours after a light/dark flip.</summary>
    public static void ApplyTo(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case Button button:
                    StyleButton(button, button.Tag as string == "primary");
                    break;

                case CheckBox check:
                    check.BackColor = Color.Transparent;
                    check.ForeColor = Text;
                    break;

                case Label label:
                    label.BackColor = Color.Transparent;
                    if (label.Tag as string != "accent") label.ForeColor = TextDim;
                    break;

                case NumericUpDown spin:
                    spin.BackColor = IsLight ? Color.White : CardHover;
                    spin.ForeColor = Text;
                    break;

                case TextBox box:
                    box.BackColor = IsLight ? Color.White : CardHover;
                    box.ForeColor = Text;
                    break;

                case TableLayoutPanel or Panel or Form:
                    control.BackColor = Bg;
                    control.ForeColor = Text;
                    break;
            }

            if (control.HasChildren) ApplyTo(control);
            control.Invalidate();
        }

        root.Invalidate();
    }

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : Card;
        button.ForeColor = primary ? OnAccent : Text;
        button.Font = Body;
        button.Cursor = Cursors.Hand;
        button.Tag = primary ? "primary" : button.Tag;

        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.1f) : CardHover;
        button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(Accent, 0.05f) : Border;
    }
}
