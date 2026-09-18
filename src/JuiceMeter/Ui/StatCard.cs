using System.Drawing.Drawing2D;

namespace JuiceMeter.Ui;

/// <summary>Label, one number, one line of detail. Flat, no decoration.</summary>
internal sealed class StatCard : Control
{
    private string _title = string.Empty;
    private string _value = "-";
    private string _sub = string.Empty;

    public StatCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Set(string title, string value, string sub)
    {
        _title = title;
        _value = value;
        _sub = sub;
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

        using var title = new SolidBrush(Theme.TextDim);
        using var value = new SolidBrush(Theme.Text);
        using var sub = new SolidBrush(Theme.TextFaint);

        var y = 12 * s;
        g.DrawString(_title, Theme.SmallBold, title, 14 * s, y);
        y += g.MeasureString(_title, Theme.SmallBold).Height + 2 * s;

        g.DrawString(_value, Theme.CardValue, value, 12 * s, y);

        if (_sub.Length == 0) return;

        var subSize = g.MeasureString(_sub, Theme.Small);
        var subY = card.Bottom - 8 * s - subSize.Height;

        // Never let the detail line ride up over the value.
        if (subY < y + g.MeasureString(_value, Theme.CardValue).Height - 6 * s) return;

        g.DrawString(_sub, Theme.Small, sub, 14 * s, subY);
    }
}
