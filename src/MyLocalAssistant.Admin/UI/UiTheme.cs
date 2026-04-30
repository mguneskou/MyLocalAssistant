using System.Drawing.Drawing2D;

namespace MyLocalAssistant.Admin.UI;

/// <summary>
/// Centralized colors, fonts and styling helpers so every form/tab in the admin
/// console looks consistent. Mirrors the client theme intentionally so admins and
/// users see the same visual language.
/// </summary>
internal static class UiTheme
{
    // ---------- Palette ----------
    public static readonly Color Accent       = Color.FromArgb(0,   120, 212);
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
    public static readonly Color Warning       = Color.FromArgb(204, 115, 0);

    // Owner-only surfaces use a faint amber tint so the global admin can see
    // at a glance that they're inside an owner-restricted screen.
    public static readonly Color OwnerTint     = Color.FromArgb(255, 250, 230);

    // ---------- Fonts ----------
    public static readonly Font BaseFont   = new("Segoe UI", 9.5F);
    public static readonly Font BaseBold   = new("Segoe UI Semibold", 9.5F);
    public static readonly Font Caption    = new("Segoe UI", 8.5F);
    public static readonly Font Heading    = new("Segoe UI Semibold", 13F);
    public static readonly Font Subheading = new("Segoe UI", 9.5F);
    public static readonly Font Mono       = new("Consolas", 9.5F);

    public static void ApplyForm(Form f)
    {
        f.Font = BaseFont;
        f.BackColor = Surface;
        f.ForeColor = TextPrimary;
    }

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
        b.Height = Math.Max(b.Height, 30);
    }

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
        b.Height = Math.Max(b.Height, 30);
    }

    /// <summary>Apply consistent flat styling to any DataGridView in the admin UI.</summary>
    public static void StyleGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = SurfaceCard;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToResizeRows = false;

        grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.Font = BaseBold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 32;

        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 238, 251);
        grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
        grid.RowTemplate.Height = 26;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Surface;
    }
}
