using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>Title, one big number, and a line of supporting detail.</summary>
internal sealed class StatCard : Control
{
    private string _title = string.Empty;
    private string _value = "-";
    private string _sub = string.Empty;
    private Color _accent = Theme.Accent;

    public StatCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    public void Set(string title, string value, string sub, Color? accent = null)
    {
        _title = title;
        _value = value;
        _sub = sub;
        _accent = accent ?? Theme.Accent;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillCard(g, bounds);

        // Accent stripe down the left edge.
        using (var clip = Theme.RoundedRect(bounds, 8f))
        {
            var saved = g.Save();
            g.SetClip(clip);

            using var stripe = new SolidBrush(_accent);
            g.FillRectangle(stripe, 0, 0, 3, Height);

            g.Restore(saved);
        }

        using var title = new SolidBrush(Theme.TextDim);
        using var value = new SolidBrush(Theme.Text);
        using var sub = new SolidBrush(Theme.TextFaint);

        g.DrawString(_title, Theme.SmallBold, title, 16, 12);
        g.DrawString(_value, Theme.CardValue, value, 14, 30);

        if (_sub.Length > 0) g.DrawString(_sub, Theme.Small, sub, 16, Height - 24);
    }
}
