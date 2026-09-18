using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>Rolling watts chart: flat area for system draw, dashed line for wall draw.</summary>
internal sealed class SparklineControl : Control
{
    private readonly struct Point3
    {
        public readonly float System;
        public readonly float Wall;
        public readonly bool OnAc;

        public Point3(float system, float wall, bool onAc)
        {
            System = system;
            Wall = wall;
            OnAc = onAc;
        }
    }

    private readonly Queue<Point3> _points = new();
    private int _capacity = 900;

    public string Caption { get; set; } = "Power";

    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(30, value);
            Trim();
        }
    }

    public SparklineControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Push(double systemWatts, double wallWatts, bool onAc)
    {
        _points.Enqueue(new Point3((float)Math.Max(0, systemWatts), (float)Math.Max(0, wallWatts), onAc));
        Trim();
        Invalidate();
    }

    public void Clear()
    {
        _points.Clear();
        Invalidate();
    }

    private void Trim()
    {
        while (_points.Count > _capacity) _points.Dequeue();
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

        var plot = new RectangleF(46, 36, Width - 62, Height - 76);
        if (plot.Width <= 10 || plot.Height <= 10) return;

        using (var captionBrush = new SolidBrush(Theme.TextDim))
        {
            g.DrawString(Caption, Theme.SmallBold, captionBrush, 14, 12);
        }

        var data = _points.ToArray();
        if (data.Length < 2)
        {
            using var hint = new SolidBrush(Theme.TextFaint);
            g.DrawString("Collecting samples...", Theme.Small, hint,
                plot.X + plot.Width / 2 - 52, plot.Y + plot.Height / 2 - 8);
            return;
        }

        var peak = 1f;
        foreach (var p in data) peak = Math.Max(peak, Math.Max(p.System, p.Wall));

        var ceiling = NiceCeiling(peak);
        DrawGrid(g, plot, ceiling);

        var step = plot.Width / (data.Length - 1);

        var systemPoints = new PointF[data.Length];
        var wallPoints = new PointF[data.Length];

        for (var i = 0; i < data.Length; i++)
        {
            var x = plot.X + step * i;
            systemPoints[i] = new PointF(x, plot.Bottom - data[i].System / ceiling * plot.Height);
            wallPoints[i] = new PointF(x, plot.Bottom - data[i].Wall / ceiling * plot.Height);
        }

        var area = new PointF[systemPoints.Length + 2];
        Array.Copy(systemPoints, area, systemPoints.Length);
        area[^2] = new PointF(plot.Right, plot.Bottom);
        area[^1] = new PointF(plot.X, plot.Bottom);

        using (var fill = new SolidBrush(Color.FromArgb(Theme.IsLight ? 34 : 46, Theme.Accent)))
        {
            g.FillPolygon(fill, area);
        }

        using (var pen = new Pen(Theme.Accent, 1.5f))
        {
            g.DrawLines(pen, systemPoints);
        }

        if (data.Any(p => p.OnAc))
        {
            using var pen = new Pen(Theme.Cpu, 1.3f) { DashStyle = DashStyle.Dash };
            g.DrawLines(pen, wallPoints);
        }

        DrawLegend(g, plot, data[^1]);
    }

    private static void DrawGrid(Graphics g, RectangleF plot, float ceiling)
    {
        using var pen = new Pen(Theme.Grid);
        using var text = new SolidBrush(Theme.TextFaint);

        using var right = new StringFormat { Alignment = StringAlignment.Far };

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height / 4f * i;
            g.DrawLine(pen, plot.X, y, plot.Right, y);

            var label = (ceiling / 4f * i).ToString("F0");
            g.DrawString(label, Theme.Small, text, new RectangleF(0, y - 7, plot.X - 8, 16), right);
        }
    }

    private void DrawLegend(Graphics g, RectangleF plot, Point3 latest)
    {
        var y = plot.Bottom + 8;
        var x = plot.X;

        x = LegendItem(g, x, y, Theme.Accent, $"System {latest.System:F1} W", false);
        if (latest.OnAc) LegendItem(g, x, y, Theme.Cpu, $"Wall {latest.Wall:F1} W", true);
    }

    private static float LegendItem(Graphics g, float x, float y, Color colour, string label, bool dashed)
    {
        using (var pen = new Pen(colour, 2f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid })
        {
            g.DrawLine(pen, x, y + 7, x + 14, y + 7);
        }

        using var brush = new SolidBrush(Theme.TextDim);
        g.DrawString(label, Theme.Small, brush, x + 17, y);

        return x + 17 + g.MeasureString(label, Theme.Small).Width + 16;
    }

    private static float NiceCeiling(float peak)
    {
        float[] steps = { 5, 10, 15, 20, 30, 40, 60, 80, 100, 125, 150, 200, 250, 300, 400, 500 };
        foreach (var step in steps)
        {
            if (peak <= step * 0.92f) return step;
        }
        return MathF.Ceiling(peak / 100f) * 100f;
    }
}
