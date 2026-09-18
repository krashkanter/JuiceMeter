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

        using (var background = new SolidBrush(Theme.Bg))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        Theme.FillCard(g, new RectangleF(0, 0, Width - 1, Height - 1));

        using var title = new SolidBrush(Theme.TextDim);
        using var value = new SolidBrush(Theme.Text);
        using var sub = new SolidBrush(Theme.TextFaint);

        g.DrawString(_title, Theme.SmallBold, title, 14, 12);
        g.DrawString(_value, Theme.CardValue, value, 12, 28);

        if (_sub.Length > 0) g.DrawString(_sub, Theme.Small, sub, 14, Height - 24);
    }
}
