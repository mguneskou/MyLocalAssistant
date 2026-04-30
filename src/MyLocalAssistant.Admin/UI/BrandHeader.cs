using System.Drawing.Drawing2D;

namespace MyLocalAssistant.Admin.UI;

/// <summary>
/// Branded header strip used at the top of the LoginForm. Paints a subtle
/// horizontal accent gradient with the product title and a subtitle.
/// </summary>
internal sealed class BrandHeader : Panel
{
    private readonly string _title;
    private readonly string _subtitle;

    public BrandHeader(string title, string subtitle, int height = 84)
    {
        _title = title;
        _subtitle = subtitle;
        Dock = DockStyle.Top;
        Height = height;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var brush = new LinearGradientBrush(
            ClientRectangle,
            UiTheme.Accent,
            UiTheme.AccentDown,
            LinearGradientMode.Horizontal))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        const int dotSize = 14;
        using (var dotBrush = new SolidBrush(Color.FromArgb(220, Color.White)))
        {
            g.FillEllipse(dotBrush, 22, (Height - dotSize) / 2 - 8, dotSize, dotSize);
        }

        using var titleFont = new Font("Segoe UI Semibold", 16F);
        using var subFont   = new Font("Segoe UI", 10F);
        using var fg        = new SolidBrush(Color.White);
        using var fgSub     = new SolidBrush(Color.FromArgb(220, Color.White));

        const int textLeft = 50;
        var titleSize = g.MeasureString(_title, titleFont);
        g.DrawString(_title, titleFont, fg, textLeft, (Height - titleSize.Height) / 2 - 10);
        g.DrawString(_subtitle, subFont, fgSub, textLeft, (Height - titleSize.Height) / 2 + titleSize.Height - 12);
    }
}
