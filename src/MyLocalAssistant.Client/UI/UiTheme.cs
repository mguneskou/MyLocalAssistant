using System.Drawing.Drawing2D;

namespace MyLocalAssistant.Client.UI;

/// <summary>
/// Centralized colors, fonts and styling helpers so every form/control across the
/// client looks consistent. Picked to match Windows 11 "Mica/Acrylic" sensibilities
/// while still rendering correctly on Windows Server (no transparency required).
/// </summary>
internal static class UiTheme
{
    // ── Light palette ──────────────────────────────────────────────────
    private static readonly Color L_Accent        = Color.FromArgb(  0, 120, 212);
    private static readonly Color L_AccentHover   = Color.FromArgb(  0, 103, 184);
    private static readonly Color L_AccentDown    = Color.FromArgb(  0,  90, 158);
    private static readonly Color L_Surface       = Color.FromArgb(250, 250, 252);
    private static readonly Color L_SurfaceAlt    = Color.FromArgb(243, 243, 247);
    private static readonly Color L_SurfaceCard   = Color.White;
    private static readonly Color L_Border        = Color.FromArgb(225, 225, 230);
    private static readonly Color L_TextPrimary   = Color.FromArgb( 32,  32,  32);
    private static readonly Color L_TextSecondary = Color.FromArgb( 96,  96, 100);
    private static readonly Color L_Danger        = Color.FromArgb(196,  43,  28);
    private static readonly Color L_Success       = Color.FromArgb( 15, 123,  15);
    private static readonly Color L_UserBubble    = Color.FromArgb(220, 235, 255);
    private static readonly Color L_UserBubbleBrd = Color.FromArgb(175, 210, 255);
    private static readonly Color L_AssistBubble  = Color.FromArgb(243, 244, 248);
    private static readonly Color L_UserName      = Color.FromArgb( 15,  95, 168);
    private static readonly Color L_AssistName    = Color.FromArgb( 31, 124,  67);
    private static readonly Color L_CodeBg        = Color.FromArgb(245, 245, 250);
    private static readonly Color L_NoteBg        = Color.FromArgb(255, 247, 225);
    private static readonly Color L_ErrorBg       = Color.FromArgb(253, 232, 230);
    private static readonly Color L_AttachChip    = Color.FromArgb(232, 240, 254);

    // ── Dark palette ───────────────────────────────────────────────────
    private static readonly Color D_Accent        = Color.FromArgb( 60, 145, 230);
    private static readonly Color D_AccentHover   = Color.FromArgb( 45, 130, 210);
    private static readonly Color D_AccentDown    = Color.FromArgb( 30, 110, 190);
    private static readonly Color D_Surface       = Color.FromArgb( 30,  30,  30);
    private static readonly Color D_SurfaceAlt    = Color.FromArgb( 38,  38,  38);
    private static readonly Color D_SurfaceCard   = Color.FromArgb( 48,  48,  50);
    private static readonly Color D_Border        = Color.FromArgb( 68,  68,  75);
    private static readonly Color D_TextPrimary   = Color.FromArgb(218, 218, 218);
    private static readonly Color D_TextSecondary = Color.FromArgb(148, 148, 155);
    private static readonly Color D_Danger        = Color.FromArgb(240,  80,  70);
    private static readonly Color D_Success       = Color.FromArgb( 50, 185,  80);
    private static readonly Color D_UserBubble    = Color.FromArgb( 22,  50,  95);
    private static readonly Color D_UserBubbleBrd = Color.FromArgb( 38,  75, 145);
    private static readonly Color D_AssistBubble  = Color.FromArgb( 55,  55,  65);
    private static readonly Color D_UserName      = Color.FromArgb(100, 175, 235);
    private static readonly Color D_AssistName    = Color.FromArgb( 80, 195, 120);
    private static readonly Color D_CodeBg        = Color.FromArgb( 24,  24,  30);
    private static readonly Color D_NoteBg        = Color.FromArgb( 65,  55,  20);
    private static readonly Color D_ErrorBg       = Color.FromArgb( 70,  25,  25);
    private static readonly Color D_AttachChip    = Color.FromArgb( 25,  55,  95);

    // ── Active theme ──────────────────────────────────────────────────
    public static bool IsDark { get; private set; }
    public static void SetDark(bool dark) => IsDark = dark;

    // ── Dynamic color properties (all callers compile unchanged) ──────
    public static Color Accent        => IsDark ? D_Accent        : L_Accent;
    public static Color AccentHover   => IsDark ? D_AccentHover   : L_AccentHover;
    public static Color AccentDown    => IsDark ? D_AccentDown    : L_AccentDown;
    public static Color AccentText    => Color.White;
    public static Color Surface       => IsDark ? D_Surface       : L_Surface;
    public static Color SurfaceAlt    => IsDark ? D_SurfaceAlt    : L_SurfaceAlt;
    public static Color SurfaceCard   => IsDark ? D_SurfaceCard   : L_SurfaceCard;
    public static Color Border        => IsDark ? D_Border        : L_Border;
    public static Color TextPrimary   => IsDark ? D_TextPrimary   : L_TextPrimary;
    public static Color TextSecondary => IsDark ? D_TextSecondary : L_TextSecondary;
    public static Color Danger        => IsDark ? D_Danger        : L_Danger;
    public static Color Success       => IsDark ? D_Success       : L_Success;

    // Bubble / code-block colours (used by ChatBubble).
    public static Color UserBubbleFill      => IsDark ? D_UserBubble   : L_UserBubble;
    public static Color UserBubbleBorder    => IsDark ? D_UserBubbleBrd: L_UserBubbleBrd;
    public static Color AssistantBubbleFill => IsDark ? D_AssistBubble : L_AssistBubble;
    public static Color UserName            => IsDark ? D_UserName     : L_UserName;
    public static Color AssistantName       => IsDark ? D_AssistName   : L_AssistName;
    public static Color CodeBlockBg         => IsDark ? D_CodeBg       : L_CodeBg;
    public static Color NoteBg              => IsDark ? D_NoteBg       : L_NoteBg;
    public static Color ErrorBg             => IsDark ? D_ErrorBg      : L_ErrorBg;
    public static Color AttachChipBg        => IsDark ? D_AttachChip   : L_AttachChip;

    // ---------- Fonts ----------
    public static readonly Font BaseFont   = new("Segoe UI", 10F);
    public static readonly Font BaseBold   = new("Segoe UI Semibold", 10F);
    public static readonly Font Caption    = new("Segoe UI", 9F);
    public static readonly Font Heading    = new("Segoe UI Semibold", 14F);
    public static readonly Font Subheading = new("Segoe UI", 10F);
    public static readonly Font Mono       = new("Consolas", 10F);

    /// <summary>Apply the standard look (font + surface bg + app icon) to a top-level form.</summary>
    public static void ApplyForm(Form f)
    {
        f.Font = BaseFont;
        f.BackColor = Surface;
        f.ForeColor = TextPrimary;
        try { f.Icon = AppIcon; } catch { /* design-time / sub-forms may already have one */ }
    }

    /// <summary>Style a button as the form's primary call-to-action (filled accent, rounded).</summary>
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
        ApplyRoundedRegion(b, 6);
    }

    /// <summary>Style a button as a flat secondary action (transparent fill, subtle border, rounded).</summary>
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
        ApplyRoundedRegion(b, 6);
    }

    /// <summary>
    /// Walk a control tree and re-apply theme colours. Custom-painted controls (bubbles)
    /// only need Invalidate(); standard controls get explicit colour assignments.
    /// Call after <see cref="SetDark"/> to refresh an open form.
    /// </summary>
    public static void ApplyTheme(Control root)
    {
        root.SuspendLayout();
        try { WalkTheme(root); }
        finally { root.ResumeLayout(true); }
        root.Invalidate(true);
    }

    private static void WalkTheme(Control c)
    {
        switch (c)
        {
            case Form f:        f.BackColor  = Surface;     f.ForeColor = TextPrimary;    break;
            case StatusStrip s: s.BackColor  = SurfaceCard; break;
            case ToolStrip ts:  ts.BackColor = SurfaceCard; ts.Renderer = new ModernRenderer(); break;
            case ListBox lb:    lb.BackColor = SurfaceAlt;  lb.ForeColor = TextPrimary;   break;
            case TextBox tb when !tb.Multiline:
                tb.BackColor = SurfaceCard; tb.ForeColor = TextPrimary; break;
            case Label lbl when lbl.Tag is string role:
                lbl.ForeColor = role == "secondary" ? TextSecondary : TextPrimary;
                if (lbl.BackColor != Color.Transparent) lbl.BackColor = Surface;
                break;
        }
        foreach (Control child in c.Controls) WalkTheme(child);
    }

    /// <summary>Render a thin separator line at the bottom of a control.</summary>
    public static void DrawBottomBorder(Graphics g, Rectangle bounds, Color? color = null)
    {
        using var p = new Pen(color ?? Border);
        g.DrawLine(p, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    /// <summary>Clip a control to a rounded rectangle. Re-applied on resize.</summary>
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
    /// <summary>Procedural app icon: accent circle with a white dot, matching <see cref="BrandHeader"/>.</summary>
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

    /// <summary>Renderer that flattens menu/toolstrip backgrounds and uses a soft accent tint on hover.</summary>
    public sealed class ModernRenderer : ToolStripProfessionalRenderer
    {
        public ModernRenderer() : base(new ModernColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { /* no border */ }

        private sealed class ModernColors : ProfessionalColorTable
        {
            private static Color Sel => IsDark ? Color.FromArgb(40, 80, 130) : Color.FromArgb(225, 238, 251);
            public override Color MenuItemSelected             => Sel;
            public override Color MenuItemSelectedGradientBegin=> Sel;
            public override Color MenuItemSelectedGradientEnd  => Sel;
            public override Color MenuItemPressedGradientBegin => SurfaceAlt;
            public override Color MenuItemPressedGradientEnd   => SurfaceAlt;
            public override Color MenuItemBorder               => Color.Transparent;
            public override Color MenuBorder                   => Border;
            public override Color ToolStripBorder              => Color.Transparent;
            public override Color ToolStripGradientBegin       => SurfaceCard;
            public override Color ToolStripGradientMiddle      => SurfaceCard;
            public override Color ToolStripGradientEnd         => SurfaceCard;
            public override Color ButtonSelectedHighlight      => Sel;
            public override Color ButtonSelectedGradientBegin  => Sel;
            public override Color ButtonSelectedGradientMiddle => Sel;
            public override Color ButtonSelectedGradientEnd    => Sel;
            public override Color ButtonPressedGradientBegin   => SurfaceAlt;
            public override Color ButtonPressedGradientMiddle  => SurfaceAlt;
            public override Color ButtonPressedGradientEnd     => SurfaceAlt;
            public override Color ButtonSelectedBorder         => Color.Transparent;
            public override Color StatusStripGradientBegin     => SurfaceCard;
            public override Color StatusStripGradientEnd       => SurfaceCard;
            public override Color SeparatorDark                => Border;
            public override Color SeparatorLight               => Border;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
