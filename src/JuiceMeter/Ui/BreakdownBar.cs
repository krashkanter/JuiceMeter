using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>
/// Stacked bar splitting current draw into CPU, integrated GPU, discrete GPU
/// and everything else.
///
/// The integrated slice is carved out of the CPU package rather than added to
/// it, because that is where the silicon actually reports it. The four segments
/// therefore still sum to the system total, exactly as the three used to.
/// </summary>
internal sealed class BreakdownBar : Control
{
    private double _cpu, _igpu, _gpu, _rest;
    private bool _cpuLive, _igpuLive, _gpuLive, _gpuOff;

    public BreakdownBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <param name="cpuWatts">Package power with the integrated slice already removed.</param>
    /// <param name="igpuWatts">The integrated slice taken out of the package.</param>
    /// <param name="gpuWatts">Discrete card board power.</param>
    public void Update(double cpuWatts, double igpuWatts, double gpuWatts, double restWatts,
        bool cpuLive, bool igpuLive, bool gpuLive, bool gpuOff)
    {
        _cpu = Math.Max(0, cpuWatts);
        _igpu = Math.Max(0, igpuWatts);
        _gpu = Math.Max(0, gpuWatts);
        _rest = Math.Max(0, restWatts);
        _cpuLive = cpuLive;
        _igpuLive = igpuLive;
        _gpuLive = gpuLive;
        _gpuOff = gpuOff;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var s = Theme.Scale(g);

        using (var background = new SolidBrush(Theme.Bg))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        var card = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillCard(g, card);

        using (var caption = new SolidBrush(Theme.TextDim))
        {
            g.DrawString("Where the watts go", Theme.SmallBold, caption, 14 * s, 12 * s);
        }

        // The integrated GPU only earns a segment of its own once it is readable;
        // otherwise its draw is simply still inside the CPU figure, unsplit.
        var splitIGpu = _igpuLive && _cpuLive;

        var total = _cpu + (splitIGpu ? _igpu : 0) + _gpu + _rest;
        var bar = new RectangleF(14 * s, 36 * s, Width - 28 * s, 20 * s);

        using (var track = new SolidBrush(Theme.Grid))
        using (var path = Theme.RoundedRect(bar, 4f * s))
        {
            g.FillPath(track, path);
        }

        if (total <= 0.01)
        {
            using var hint = new SolidBrush(Theme.TextFaint);
            g.DrawString("No reading yet", Theme.Small, hint, 14 * s, bar.Bottom + 10 * s);
            return;
        }

        using (var clip = Theme.RoundedRect(bar, 4f * s))
        {
            var saved = g.Save();
            g.SetClip(clip);

            var x = bar.X;
            x = Segment(g, x, bar, _cpu / total, Theme.Cpu);
            if (splitIGpu) x = Segment(g, x, bar, _igpu / total, Theme.IGpu);
            x = Segment(g, x, bar, _gpu / total, Theme.Gpu);
            Segment(g, x, bar, _rest / total, Theme.Rest);

            g.Restore(saved);
        }

        var entries = new List<(Color Colour, string Label, double Watts)>(4)
        {
            (Theme.Cpu, "CPU", _cpu),
        };

        if (splitIGpu) entries.Add((Theme.IGpu, "iGPU", _igpu));
        entries.Add((Theme.Gpu, splitIGpu ? "dGPU" : "GPU", _gpu));
        entries.Add((Theme.Rest, "Rest", _rest));

        var legendY = bar.Bottom + 12 * s;

        // Four entries plus percentages can outgrow a narrow window, and a
        // legend that runs off the card looks broken. Measure first, and drop
        // the percentages before letting that happen.
        var available = card.Width - 28 * s;
        var withPercent = entries.Sum(entry => EntryWidth(g, entry.Label, entry.Watts, total, true));
        var showPercent = withPercent <= available;

        var lx = 14f * s;
        foreach (var entry in entries)
        {
            lx = Legend(g, lx, legendY, entry.Colour, entry.Label, entry.Watts, total, showPercent);
        }

        var note = Note(splitIGpu);
        if (note is null) return;

        var noteSize = g.MeasureString(note, Theme.Small);
        var noteY = legendY + noteSize.Height + 8 * s;
        if (noteY + noteSize.Height > card.Bottom - 4 * s) return;

        using var noteBrush = new SolidBrush(Theme.TextFaint);
        g.DrawString(note, Theme.Small, noteBrush, 14 * s, noteY);
    }

    private string? Note(bool splitIGpu)
    {
        if (_gpuOff && !_cpuLive) return "dGPU is off (Eco) and CPU package power needs administrator.";
        if (!_cpuLive && !_gpuLive) return "CPU and GPU power are not readable, so all of it lands in Rest.";
        if (!_cpuLive) return "CPU package power needs administrator, so it lands in Rest.";

        if (_gpuOff)
        {
            return splitIGpu
                ? "dGPU is off (Eco), so the integrated GPU is doing the drawing."
                : "dGPU is off (Eco), so it genuinely draws nothing.";
        }

        if (!_gpuLive) return "dGPU power is not readable, so it lands in Rest.";

        // Worth saying once the split is on: the number is a slice, not an extra.
        return splitIGpu ? "iGPU is measured inside the CPU package, not on top of it." : null;
    }

    private static float Segment(Graphics g, float x, RectangleF bar, double fraction, Color colour)
    {
        var width = (float)(bar.Width * fraction);
        if (width <= 0.5f) return x;

        using var brush = new SolidBrush(colour);
        g.FillRectangle(brush, x, bar.Y, width, bar.Height);

        return x + width;
    }

    private static string LegendText(string label, double watts, double total, bool showPercent) =>
        showPercent
            ? $"{label}  {watts:F1} W  ({watts / total * 100:F0}%)"
            : $"{label}  {watts:F1} W";

    private static float EntryWidth(Graphics g, string label, double watts, double total, bool showPercent)
    {
        var s = Theme.Scale(g);
        var text = LegendText(label, watts, total, showPercent);
        return 14 * s + g.MeasureString(text, Theme.Small).Width + 16 * s;
    }

    private static float Legend(Graphics g, float x, float y, Color colour, string label,
        double watts, double total, bool showPercent)
    {
        var s = Theme.Scale(g);

        using (var swatch = new SolidBrush(colour))
        {
            g.FillRectangle(swatch, x, y + 4 * s, 9 * s, 9 * s);
        }

        var text = LegendText(label, watts, total, showPercent);

        using var brush = new SolidBrush(Theme.Text);
        g.DrawString(text, Theme.Small, brush, x + 14 * s, y);

        return x + 14 * s + g.MeasureString(text, Theme.Small).Width + 16 * s;
    }
}
