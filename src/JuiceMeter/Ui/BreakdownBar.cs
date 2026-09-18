using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>Stacked bar splitting current draw into CPU, GPU and everything else.</summary>
internal sealed class BreakdownBar : Control
{
    private double _cpu, _gpu, _rest;
    private bool _cpuLive, _gpuLive;

    public BreakdownBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    public void Update(double cpuWatts, double gpuWatts, double restWatts, bool cpuLive, bool gpuLive)
    {
        _cpu = Math.Max(0, cpuWatts);
        _gpu = Math.Max(0, gpuWatts);
        _rest = Math.Max(0, restWatts);
        _cpuLive = cpuLive;
        _gpuLive = gpuLive;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.FillCard(g, new RectangleF(0, 0, Width - 1, Height - 1));

        using (var caption = new SolidBrush(Theme.TextDim))
        {
            g.DrawString("Where the watts go", Theme.SmallBold, caption, 14, 12);
        }

        var total = _cpu + _gpu + _rest;
        var bar = new RectangleF(14, 36, Width - 28, 22);

        using (var track = new SolidBrush(Theme.PanelHi))
        using (var path = Theme.RoundedRect(bar, 6f))
        {
            g.FillPath(track, path);
        }

        if (total <= 0.01)
        {
            using var hint = new SolidBrush(Theme.TextFaint);
            g.DrawString("No reading yet", Theme.Small, hint, 14, bar.Bottom + 10);
            return;
        }

        // Clip the segments to the rounded track so the ends stay curved.
        using (var clip = Theme.RoundedRect(bar, 6f))
        {
            var saved = g.Save();
            g.SetClip(clip);

            var x = bar.X;
            x = Segment(g, x, bar, _cpu / total, Theme.Cpu);
            x = Segment(g, x, bar, _gpu / total, Theme.Gpu);
            Segment(g, x, bar, _rest / total, Theme.Base);

            g.Restore(saved);
        }

        var legendY = bar.Bottom + 12;
        var lx = 14f;

        lx = Legend(g, lx, legendY, Theme.Cpu, "CPU", _cpu, total);
        lx = Legend(g, lx, legendY, Theme.Gpu, "GPU", _gpu, total);
        Legend(g, lx, legendY, Theme.Base, "Rest", _rest, total);

        var missing = !_cpuLive && !_gpuLive ? "CPU and GPU power are not readable"
            : !_cpuLive ? "CPU package power needs administrator"
            : !_gpuLive ? "GPU power is not readable"
            : null;

        if (missing is not null)
        {
            using var note = new SolidBrush(Theme.TextFaint);
            g.DrawString($"{missing}, so that draw is folded into Rest.",
                Theme.Small, note, 14, legendY + 22);
        }
    }

    private static float Segment(Graphics g, float x, RectangleF bar, double fraction, Color colour)
    {
        var width = (float)(bar.Width * fraction);
        if (width <= 0.5f) return x;

        using var brush = new SolidBrush(colour);
        g.FillRectangle(brush, x, bar.Y, width, bar.Height);

        return x + width;
    }

    private static float Legend(Graphics g, float x, float y, Color colour, string label, double watts, double total)
    {
        using (var dot = new SolidBrush(colour))
        {
            g.FillEllipse(dot, x, y + 4, 8, 8);
        }

        var text = $"{label}  {watts:F1} W  ({watts / total * 100:F0}%)";

        using var brush = new SolidBrush(Theme.Text);
        g.DrawString(text, Theme.Small, brush, x + 13, y);

        return x + 13 + g.MeasureString(text, Theme.Small).Width + 14;
    }
}
