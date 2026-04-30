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
        try { f.Icon = AppIcon; } catch { /* sub-forms may already have one */ }
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
        ApplyRoundedRegion(b, 6);
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
        ApplyRoundedRegion(b, 6);
    }

    public static void ApplyRoundedRegion(Control c, int radius)
    {
        void Apply()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            c.Region = new Region(MakeRoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius));
        }
        Apply();
        c.Resize -= OnResizeApplyRounded;
        c.Resize += OnResizeApplyRounded;
        void OnResizeApplyRounded(object? s, EventArgs e) => Apply();
    }

    public static GraphicsPath MakeRoundedPath(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X,             r.Y,             d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Y,             d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d,   0, 90);
        p.AddArc(r.X,             r.Bottom - d - 1, d, d,  90, 90);
        p.CloseFigure();
        return p;
    }

    private static Icon? _appIcon;
    /// <summary>Procedural admin app icon: accent gradient ring with white dot.</summary>
    public static Icon AppIcon => _appIcon ??= BuildAppIcon();

    private static Icon BuildAppIcon()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Accent, AccentDown, 45f);
            g.FillEllipse(bg, 2, 2, size - 4, size - 4);
            // Admin marker: small amber ring around the white dot.
            using var ring = new Pen(Warning, 3);
            g.DrawEllipse(ring, 18, 18, 28, 28);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 22, 22, 20, 20);
        }
        var hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }
        finally { _ = NativeMethods.DestroyIcon(hicon); }
    }

    /// <summary>Apply ApplyForm + theme every Button as Primary (AcceptButton) or Secondary (rest).</summary>
    public static void ApplyDialog(Form f)
    {
        ApplyForm(f);
        f.HandleCreated += (_, _) => ThemeDialogButtons(f);
        if (f.IsHandleCreated) ThemeDialogButtons(f);
    }

    private static void ThemeDialogButtons(Control parent)
    {
        var accept = (parent.FindForm() ?? parent as Form)?.AcceptButton as Button;
        foreach (var b in EnumerateButtons(parent))
        {
            if (ReferenceEquals(b, accept)) Primary(b); else Secondary(b);
        }
    }

    private static IEnumerable<Button> EnumerateButtons(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is Button b) yield return b;
            else foreach (var inner in EnumerateButtons(c)) yield return inner;
        }
    }

    public sealed class ModernRenderer : ToolStripProfessionalRenderer
    {
        public ModernRenderer() : base(new ModernColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { /* no border */ }

        private sealed class ModernColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected              => Color.FromArgb(225, 238, 251);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(225, 238, 251);
            public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(225, 238, 251);
            public override Color MenuItemPressedGradientBegin  => SurfaceAlt;
            public override Color MenuItemPressedGradientEnd    => SurfaceAlt;
            public override Color MenuItemBorder                => Color.Transparent;
            public override Color MenuBorder                    => Border;
            public override Color ToolStripBorder               => Color.Transparent;
            public override Color ToolStripGradientBegin        => SurfaceCard;
            public override Color ToolStripGradientMiddle       => SurfaceCard;
            public override Color ToolStripGradientEnd          => SurfaceCard;
            public override Color ButtonSelectedHighlight       => Color.FromArgb(225, 238, 251);
            public override Color ButtonSelectedGradientBegin   => Color.FromArgb(225, 238, 251);
            public override Color ButtonSelectedGradientMiddle  => Color.FromArgb(225, 238, 251);
            public override Color ButtonSelectedGradientEnd     => Color.FromArgb(225, 238, 251);
            public override Color ButtonPressedGradientBegin    => SurfaceAlt;
            public override Color ButtonPressedGradientMiddle   => SurfaceAlt;
            public override Color ButtonPressedGradientEnd      => SurfaceAlt;
            public override Color ButtonSelectedBorder          => Color.Transparent;
            public override Color StatusStripGradientBegin      => SurfaceCard;
            public override Color StatusStripGradientEnd        => SurfaceCard;
            public override Color SeparatorDark                 => Border;
            public override Color SeparatorLight                => Border;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
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
