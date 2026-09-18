using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(22, 22, 26);
    public static readonly Color Panel = Color.FromArgb(31, 31, 37);
    public static readonly Color PanelHi = Color.FromArgb(41, 41, 49);
    public static readonly Color Border = Color.FromArgb(54, 54, 63);

    public static readonly Color Text = Color.FromArgb(240, 240, 246);
    public static readonly Color TextDim = Color.FromArgb(148, 148, 162);
    public static readonly Color TextFaint = Color.FromArgb(104, 104, 118);

    public static readonly Color Accent = Color.FromArgb(255, 176, 32);
    public static readonly Color AccentDim = Color.FromArgb(158, 108, 18);

    public static readonly Color Cpu = Color.FromArgb(86, 156, 255);
    public static readonly Color Gpu = Color.FromArgb(118, 214, 128);
    public static readonly Color Base = Color.FromArgb(166, 138, 240);

    public static readonly Color Good = Color.FromArgb(84, 200, 128);
    public static readonly Color Warn = Color.FromArgb(255, 132, 92);
    public static readonly Color Charge = Color.FromArgb(96, 206, 168);

    private const string Face = "Segoe UI";
    private const string Numeric = "Segoe UI Semibold";

    public static readonly Font Body = new(Face, 9f);
    public static readonly Font BodyBold = new(Face, 9f, FontStyle.Bold);
    public static readonly Font Small = new(Face, 8f);
    public static readonly Font SmallBold = new(Face, 8f, FontStyle.Bold);
    public static readonly Font Title = new(Face, 13f, FontStyle.Bold);
    public static readonly Font CardValue = new(Numeric, 19f);
    public static readonly Font Huge = new(Numeric, 38f);

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

    public static void FillCard(Graphics g, RectangleF bounds, float radius = 8f, Color? fill = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(fill ?? Panel);
        using var pen = new Pen(Border);

        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    /// <summary>Small rounded label, used for the source and confidence chips.</summary>
    public static void DrawPill(Graphics g, string text, PointF location, Color colour, Font? font = null)
    {
        font ??= SmallBold;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var size = g.MeasureString(text, font);
        var bounds = new RectangleF(location.X, location.Y, size.Width + 16f, size.Height + 6f);

        using var path = RoundedRect(bounds, bounds.Height / 2f);
        using var fill = new SolidBrush(Color.FromArgb(38, colour));
        using var pen = new Pen(Color.FromArgb(150, colour));
        using var textBrush = new SolidBrush(colour);

        g.FillPath(fill, path);
        g.DrawPath(pen, path);
        g.DrawString(text, font, textBrush, bounds.X + 8f, bounds.Y + 3f);
    }

    public static SizeF PillSize(Graphics g, string text, Font? font = null)
    {
        font ??= SmallBold;
        var size = g.MeasureString(text, font);
        return new SizeF(size.Width + 16f, size.Height + 6f);
    }

    public static Color ForSource(Core.PowerSource source) => source switch
    {
        Core.PowerSource.Battery => Accent,
        Core.PowerSource.AcCharging => Charge,
        Core.PowerSource.AcIdle => Cpu,
        _ => TextDim,
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
}
