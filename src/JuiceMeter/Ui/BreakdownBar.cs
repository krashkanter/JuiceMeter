using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>Stacked bar splitting current draw into CPU, GPU and everything else.</summary>
internal sealed class BreakdownBar : Control
{
    private double _cpu, _gpu, _rest;
    private bool _cpuLive, _gpuLive, _gpuOff;

    public BreakdownBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Update(double cpuWatts, double gpuWatts, double restWatts, bool cpuLive, bool gpuLive, bool gpuOff)
    {
        _cpu = Math.Max(0, cpuWatts);
        _gpu = Math.Max(0, gpuWatts);
        _rest = Math.Max(0, restWatts);
        _cpuLive = cpuLive;
        _gpuLive = gpuLive;
        _gpuOff = gpuOff;
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

        using (var caption = new SolidBrush(Theme.TextDim))
        {
            g.DrawString("Where the watts go", Theme.SmallBold, caption, 14, 12);
        }

        var total = _cpu + _gpu + _rest;
        var bar = new RectangleF(14, 36, Width - 28, 20);

        using (var track = new SolidBrush(Theme.Grid))
        using (var path = Theme.RoundedRect(bar, 4f))
        {
            g.FillPath(track, path);
        }

        if (total <= 0.01)
        {
            using var hint = new SolidBrush(Theme.TextFaint);
            g.DrawString("No reading yet", Theme.Small, hint, 14, bar.Bottom + 10);
            return;
        }

        using (var clip = Theme.RoundedRect(bar, 4f))
        {
            var saved = g.Save();
            g.SetClip(clip);

            var x = bar.X;
            x = Segment(g, x, bar, _cpu / total, Theme.Cpu);
            x = Segment(g, x, bar, _gpu / total, Theme.Gpu);
            Segment(g, x, bar, _rest / total, Theme.Rest);

            g.Restore(saved);
        }

        var legendY = bar.Bottom + 12;
        var lx = 14f;

        lx = Legend(g, lx, legendY, Theme.Cpu, "CPU", _cpu, total);
        lx = Legend(g, lx, legendY, Theme.Gpu, "GPU", _gpu, total);
        Legend(g, lx, legendY, Theme.Rest, "Rest", _rest, total);

        var note = _gpuOff && !_cpuLive
            ? "dGPU is off (Eco) and CPU package power needs administrator."
            : _gpuOff ? "dGPU is off (Eco), so it genuinely draws nothing."
            : !_cpuLive && !_gpuLive ? "CPU and GPU power are not readable, so all of it lands in Rest."
            : !_cpuLive ? "CPU package power needs administrator, so it lands in Rest."
            : !_gpuLive ? "GPU power is not readable, so it lands in Rest."
            : null;

        if (note is not null)
        {
            using var brush = new SolidBrush(Theme.TextFaint);
            g.DrawString(note, Theme.Small, brush, 14, legendY + 22);
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
        using (var swatch = new SolidBrush(colour))
        {
            g.FillRectangle(swatch, x, y + 4, 9, 9);
        }

        var text = $"{label}  {watts:F1} W  ({watts / total * 100:F0}%)";

        using var brush = new SolidBrush(Theme.Text);
        g.DrawString(text, Theme.Small, brush, x + 14, y);

        return x + 14 + g.MeasureString(text, Theme.Small).Width + 16;
    }
}
