using System.Drawing.Drawing2D;

namespace MyLocalAssistant.Client.UI;

/// <summary>
/// Centralized colors, fonts and styling helpers so every form/control across the
/// client looks consistent. Picked to match Windows 11 "Mica/Acrylic" sensibilities
/// while still rendering correctly on Windows Server (no transparency required).
/// </summary>
internal static class UiTheme
{
    // ---------- Palette ----------
    public static readonly Color Accent       = Color.FromArgb(0,   120, 212); // Win11 system accent
    public static readonly Color AccentHover  = Color.FromArgb(0,   103, 184);
    public static readonly Color AccentDown   = Color.FromArgb(0,    90, 158);
    public static readonly Color AccentText   = Color.White;

    public static readonly Color Surface      = Color.FromArgb(250, 250, 252);
    public static readonly Color SurfaceAlt   = Color.FromArgb(243, 243, 247);
    public static readonly Color SurfaceCard  = Color.White;
    public static readonly Color Border       = Color.FromArgb(225, 225, 230);

    public static readonly Color TextPrimary   = Color.FromArgb(32, 32, 32);
    public static readonly Color TextSecondary = Color.FromArgb(96, 96, 100);
    public static readonly Color Danger        = Color.FromArgb(196, 43, 28);
    public static readonly Color Success       = Color.FromArgb(15, 123, 15);

    // Conversation bubble colors.
    public static readonly Color UserName      = Color.FromArgb(15, 95, 168);
    public static readonly Color AssistantName = Color.FromArgb(31, 124, 67);

    // ---------- Fonts ----------
    public static readonly Font BaseFont   = new("Segoe UI", 10F);
    public static readonly Font BaseBold   = new("Segoe UI Semibold", 10F);
    public static readonly Font Caption    = new("Segoe UI", 9F);
    public static readonly Font Heading    = new("Segoe UI Semibold", 14F);
    public static readonly Font Subheading = new("Segoe UI", 10F);
    public static readonly Font Mono       = new("Consolas", 10F);

    /// <summary>Apply the standard look (font + surface bg) to a top-level form.</summary>
    public static void ApplyForm(Form f)
    {
        f.Font = BaseFont;
        f.BackColor = Surface;
        f.ForeColor = TextPrimary;
    }

    /// <summary>Style a button as the form's primary call-to-action (filled accent).</summary>
    public static void Primary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Accent;
        b.ForeColor = AccentText;
        b.Font = BaseBold;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentDown;
        b.Cursor = Cursors.Hand;
        b.Height = Math.Max(b.Height, 32);
    }

    /// <summary>Style a button as a flat secondary action (transparent fill, subtle border).</summary>
    public static void Secondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = SurfaceCard;
        b.ForeColor = TextPrimary;
        b.Font = BaseFont;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = SurfaceAlt;
        b.FlatAppearance.MouseDownBackColor = Border;
        b.Cursor = Cursors.Hand;
        b.Height = Math.Max(b.Height, 32);
    }

    /// <summary>Render a thin separator line at the bottom of a control.</summary>
    public static void DrawBottomBorder(Graphics g, Rectangle bounds, Color? color = null)
    {
        using var p = new Pen(color ?? Border);
        g.DrawLine(p, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }
}
