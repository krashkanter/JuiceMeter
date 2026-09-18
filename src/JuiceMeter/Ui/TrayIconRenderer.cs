using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace JuiceMeter.Ui;

/// <summary>
/// Draws the live wattage straight into the notification-area icon, so the
/// number is readable without opening anything.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Dictionary<int, Font> FontCache = new();

    public static int IconSize
    {
        get
        {
            var size = SystemInformation.SmallIconSize.Width;
            return size >= 16 ? size : 16;
        }
    }

    /// <summary>
    /// Caller owns the returned icon and must call <see cref="Native.DestroyIcon"/>
    /// on its handle. GDI icon handles are not freed by Icon.Dispose when they
    /// come from GetHicon, and this is repainted every second.
    /// </summary>
    public static Icon Render(string text, Color accent)
    {
        var size = IconSize;
        var light = Native.IsLightTaskbar();
        var ink = light ? Color.FromArgb(14, 14, 18) : Color.White;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // A bar along the bottom carries the power source colour.
            var barHeight = Math.Max(2f, size / 8f);
            using (var bar = new SolidBrush(accent))
            {
                g.FillRectangle(bar, 0, size - barHeight, size, barHeight);
            }

            var box = new RectangleF(0, -1, size, size - barHeight + 1);
            var font = FitFont(g, text, box, size);

            using var brush = new SolidBrush(ink);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString(text, font, brush, box, format);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Font FitFont(Graphics g, string text, RectangleF box, int iconSize)
    {
        // Three characters have to fit in 16 px, so start small and only grow
        // when the string is short.
        for (var points = iconSize switch { >= 32 => 16, >= 24 => 12, _ => 9 }; points >= 5; points--)
        {
            var font = GetFont(points);
            var measured = g.MeasureString(text, font);
            if (measured.Width <= box.Width && measured.Height <= box.Height + 3) return font;
        }

        return GetFont(5);
    }

    private static Font GetFont(int points)
    {
        if (FontCache.TryGetValue(points, out var cached)) return cached;

        var font = new Font("Segoe UI", points, FontStyle.Bold, GraphicsUnit.Pixel);
        FontCache[points] = font;
        return font;
    }

    /// <summary>What to print in the icon for a given wattage.</summary>
    public static string FormatWatts(double watts)
    {
        if (!double.IsFinite(watts) || watts < 0) return "-";
        if (watts >= 999) return "999";
        if (watts >= 99.5) return Math.Round(watts).ToString("F0");
        if (watts >= 9.95) return Math.Round(watts).ToString("F0");
        return watts.ToString("F1");
    }

    public static string FormatUnits(double units) =>
        units >= 100 ? Math.Round(units).ToString("F0")
        : units >= 10 ? units.ToString("F0")
        : units.ToString("F1");
}
