using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyLocalAssistant.Client.UI;

internal enum BubbleKind { User, Assistant, Note, Error }

/// <summary>
/// Chat transcript panel. Renders each turn as a rounded bubble, supports streaming,
/// code-block highlighting, animated "thinking" dots, empty-state overlay,
/// scroll-to-bottom FAB, and live dark/light theme refresh.
/// </summary>
internal sealed class ChatTranscript : Panel
{
    private readonly FlowLayoutPanel _flow;
    private readonly Panel _emptyPanel;
    private readonly Panel _emptyIcon;
    private readonly Label _emptyTitle;
    private readonly Label _emptyHint;
    private readonly Panel _fab;
    private ChatBubble? _streaming;
    private bool _pinnedToBottom = true;
    private string _agentName = "Assistant";

    public ChatTranscript()
    {
        BackColor    = UiTheme.SurfaceCard;
        DoubleBuffered = true;

        _flow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            BackColor     = UiTheme.SurfaceCard,
            Padding       = new Padding(20, 16, 20, 16),
        };
        Controls.Add(_flow);

        // â”€â”€ Empty-state overlay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        _emptyTitle = new Label
        {
            AutoSize  = true,
            Font      = UiTheme.Heading,
            ForeColor = UiTheme.TextSecondary,
            Text      = "Start a conversation",
        };
        _emptyHint = new Label
        {
            AutoSize  = true,
            Font      = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            Text      = "Type a message below",
        };
        _emptyIcon = new Panel { BackColor = Color.Transparent, Size = new Size(56, 56) };
        _emptyIcon.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(UiTheme.Accent, 2.5f);
            using var bp = UiTheme.MakeRoundedPath(new Rectangle(2, 2, 50, 36), 10);
            e.Graphics.DrawPath(pen, bp);
            var tail = new[] { new Point(8, 38), new Point(12, 34), new Point(22, 34) };
            e.Graphics.DrawPolygon(pen, tail);
            using var db = new SolidBrush(UiTheme.Accent);
            for (int i = 0; i < 3; i++) e.Graphics.FillEllipse(db, 13 + i * 12, 15, 6, 6);
        };
        int emptyW = Math.Max(Math.Max(_emptyTitle.PreferredWidth, _emptyHint.PreferredWidth) + 20, 80);
        _emptyPanel = new Panel { BackColor = Color.Transparent, Size = new Size(emptyW, 64 + _emptyTitle.PreferredHeight + 10 + _emptyHint.PreferredHeight) };
        _emptyIcon.Location  = new Point((emptyW - 56) / 2, 0);
        _emptyTitle.Location = new Point(0, 64);
        _emptyHint.Location  = new Point(2, 64 + _emptyTitle.PreferredHeight + 8);
        _emptyPanel.Controls.Add(_emptyIcon);
        _emptyPanel.Controls.Add(_emptyTitle);
        _emptyPanel.Controls.Add(_emptyHint);

        // â”€â”€ Scroll-to-bottom FAB â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        _fab = new Panel { Size = new Size(38, 38), BackColor = UiTheme.SurfaceCard, Visible = false, Cursor = Cursors.Hand };
        var fabLbl = new Label
        {
            Text      = "\u2193",
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = new Font("Segoe UI", 14F),
            ForeColor = UiTheme.TextPrimary,
            BackColor = Color.Transparent,
        };
        _fab.Controls.Add(fabLbl);
        _fab.Click    += (_, _) => ScrollToEnd();
        fabLbl.Click  += (_, _) => ScrollToEnd();
        _fab.Paint    += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(UiTheme.Border, 1.5f);
            using var path = UiTheme.MakeRoundedPath(new Rectangle(0, 0, _fab.Width - 1, _fab.Height - 1), 19);
            using var bg = new SolidBrush(_fab.BackColor);
            e.Graphics.FillPath(bg, path);
            e.Graphics.DrawPath(p, path);
        };

        Controls.Add(_emptyPanel);
        Controls.Add(_fab);
        _emptyPanel.BringToFront();
        _fab.BringToFront();

        _flow.Resize     += (_, _) => { RelayoutAll(); PositionOverlays(); };
        _flow.Scroll     += (_, _) => { _pinnedToBottom = IsScrolledToBottom(); UpdateFab(); };
        _flow.MouseWheel += (_, _) => { _pinnedToBottom = IsScrolledToBottom(); UpdateFab(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy whole transcript", null, (_, _) =>
        {
            var t = GetTranscriptText();
            if (!string.IsNullOrEmpty(t)) Clipboard.SetText(t);
        });
        _flow.ContextMenuStrip = menu;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PositionOverlays(); }

    private void PositionOverlays()
    {
        _emptyPanel.Location = new Point(
            Math.Max(0, (Width  - _emptyPanel.Width)  / 2),
            Math.Max(0, (Height - _emptyPanel.Height) / 2) - 30);
        _fab.Location = new Point(Width - _fab.Width - 18, Height - _fab.Height - 18);
    }

    public void SetAgentName(string name)
    {
        _agentName       = name;
        _emptyTitle.Text = name;
        _emptyHint.Text  = "Start typing to begin";
        int panelW = Math.Max(Math.Max(_emptyTitle.PreferredWidth, _emptyHint.PreferredWidth) + 20, 80);
        _emptyPanel.Size = new Size(panelW, 64 + _emptyTitle.PreferredHeight + 10 + _emptyHint.PreferredHeight);
        _emptyIcon.Location  = new Point((panelW - 56) / 2, 0);
        _emptyTitle.Location = new Point(0, 64);
        _emptyHint.Location  = new Point(2, 64 + _emptyTitle.PreferredHeight + 8);
        PositionOverlays();
    }

    public void Clear()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls) c.Dispose();
        _flow.Controls.Clear();
        _flow.ResumeLayout();
        _streaming      = null;
        _pinnedToBottom = true;
        UpdateEmptyState();
        UpdateFab();
    }

    private void UpdateEmptyState() => _emptyPanel.Visible = _flow.Controls.Count == 0;
    private void UpdateFab()        => _fab.Visible = !IsScrolledToBottom() && _flow.Controls.Count > 0;

    public void AppendUserMessage(string text)
        => Add(new ChatBubble(BubbleKind.User, text, "You", DateTime.UtcNow));

    public void AppendAssistantMessage(string speaker, string text)
        => Add(new ChatBubble(BubbleKind.Assistant, text, speaker, DateTime.UtcNow));

    public void AppendNote(string text, BubbleKind kind = BubbleKind.Note)
        => Add(new ChatBubble(kind, text, "", DateTime.UtcNow));

    /// <summary>
    /// If <paramref name="toolJson"/> contains <c>{"type":"image","path":"...","filename":"..."}</c>
    /// adds an inline image bubble and returns <c>true</c>. Otherwise returns <c>false</c>.
    /// </summary>
    public bool TryAppendImageResult(string? toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) ||
                !typeEl.GetString()!.Equals("image", StringComparison.OrdinalIgnoreCase))
                return false;

            var path     = root.TryGetProperty("path",     out var p) ? p.GetString() ?? "" : "";
            var filename = root.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : path;
            if (string.IsNullOrWhiteSpace(path)) return false;

            Add(new ImageBubble(path, filename));
            return true;
        }
        catch { return false; }
    }

    public void BeginAssistantStream(string speaker)
    {
        _streaming = new ChatBubble(BubbleKind.Assistant, "", speaker, DateTime.UtcNow);
        _streaming.BeginStreaming();
        Add(_streaming);
    }

    public void AppendAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_streaming is null) BeginAssistantStream(_agentName);
        _streaming!.AppendStreamingText(text);
        if (_pinnedToBottom) ScrollToEnd();
    }

    public void EndAssistantStream(int tokens = 0, double elapsedSeconds = 0)
    {
        if (_streaming is not null && tokens > 0)
            _streaming.SetStats(tokens, elapsedSeconds);
        _streaming?.FinishStreaming();
        _streaming = null;
    }

    private void Add(ChatBubble b)
    {
        _pinnedToBottom = IsScrolledToBottom();
        b.SetAvailableWidth(ContentWidth());
        _flow.Controls.Add(b);
        UpdateEmptyState();
        UpdateFab();
        if (_pinnedToBottom) ScrollToEnd();
    }

    private void Add(ImageBubble b)
    {
        _pinnedToBottom = IsScrolledToBottom();
        b.SetAvailableWidth(ContentWidth());
        _flow.Controls.Add(b);
        UpdateEmptyState();
        UpdateFab();
        if (_pinnedToBottom) ScrollToEnd();
    }

    private void RelayoutAll()
    {
        _flow.SuspendLayout();
        var w = ContentWidth();
        foreach (Control c in _flow.Controls)
        {
            if (c is ChatBubble b) b.SetAvailableWidth(w);
            else if (c is ImageBubble ib) ib.SetAvailableWidth(w);
        }
        _flow.ResumeLayout();
    }

    private int ContentWidth()
    {
        var w = _flow.ClientSize.Width - _flow.Padding.Horizontal;
        if (_flow.VerticalScroll.Visible) w -= SystemInformation.VerticalScrollBarWidth;
        return Math.Max(220, w);
    }

    private bool IsScrolledToBottom()
    {
        var v = _flow.VerticalScroll;
        if (!v.Visible) return true;
        return v.Value + _flow.ClientSize.Height >= v.Maximum - 32;
    }

    public void ScrollToEnd()
    {
        if (_flow.Controls.Count == 0) return;
        _flow.ScrollControlIntoView(_flow.Controls[_flow.Controls.Count - 1]);
        _pinnedToBottom = true;
        UpdateFab();
    }

    public string GetTranscriptText()
    {
        var sb = new StringBuilder();
        foreach (Control c in _flow.Controls)
        {
            if (c is not ChatBubble b) continue;
            var who = string.IsNullOrEmpty(b.SpeakerName) ? b.Kind.ToString() : b.SpeakerName;
            sb.Append(who).Append(' ').Append(b.CreatedAt.ToLocalTime().ToString("HH:mm")).AppendLine();
            sb.AppendLine(b.PlainText);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void RefreshTheme()
    {
        BackColor       = UiTheme.SurfaceCard;
        _flow.BackColor = UiTheme.SurfaceCard;
        _emptyTitle.ForeColor = UiTheme.TextSecondary;
        _emptyHint.ForeColor  = UiTheme.TextSecondary;
        _fab.BackColor  = UiTheme.SurfaceCard;
        foreach (Control c in _flow.Controls)
        {
            if (c is ChatBubble b) b.RefreshTheme();
            else if (c is ImageBubble ib) ib.RefreshTheme();
        }
        _emptyIcon.Invalidate();
        _fab.Invalidate();
        Invalidate(true);
    }
}

// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// ChatBubble
// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>
/// One message rendered as a rounded bubble. Supports streaming animation (pulsing
/// dots â†’ text), code-block segments with dark background and Copy button, and
/// an always-visible âŽ˜ glyph for quick message copy.
/// </summary>
internal sealed class ChatBubble : Panel
{
    private static readonly Font BodyFont    = new("Segoe UI", 10.5F);
    private static readonly Font NoteFont    = new("Segoe UI", 9.5F, FontStyle.Italic);
    private static readonly Font CodeFont    = new("Consolas", 9.5F);
    private static readonly Font CodeHdrFont = new("Segoe UI", 8.5F);

    private const int BubbleRadius    = 12;
    private const int InnerPadX       = 14;
    private const int InnerPadY       = 10;
    private const int SegGap          = 6;
    private const int CodeTopBar      = 24;
    private const int CodePadH        = 10;
    private const int CodePadV        = 6;
    private const int BubbleBottomGap = 4;
    private const int MetaHeight      = 16;
    private const double MaxFraction  = 0.82;

    private readonly TextBox             _mainText;
    private readonly List<Control>       _segControls = new();
    private readonly Label               _meta;
    private readonly Label               _copyBtn;
    private Rectangle                    _bubbleBounds;
    private int                          _availableWidth = 600;
    private int                          _pendingChars;
    private string                       _fullText = "";
    private bool                         _hasCodeBlocks;

    // Streaming animation
    private readonly System.Windows.Forms.Timer _dotsTimer;
    private int _dotsPhase;

    public BubbleKind Kind        { get; }
    public string     SpeakerName { get; }
    public DateTime   CreatedAt   { get; }
    public bool       IsStreaming { get; private set; }
    public string     PlainText   => _fullText;

    private string _statsSuffix = "";  // e.g. "  ·  2.1s · 312 tok"

    /// <summary>Sets response stats shown in the meta line (assistant bubbles only).</summary>
    public void SetStats(int tokens, double elapsedSeconds)
    {
        if (tokens <= 0) return;
        var rate = elapsedSeconds > 0.1 ? $" · {tokens / elapsedSeconds:F0} tok/s" : "";
        _statsSuffix = $"  \u00b7  {elapsedSeconds:F1}s · {tokens:N0} tok{rate}";
        UpdateMeta();
        Relayout();
    }

    public ChatBubble(BubbleKind kind, string text, string speakerName, DateTime when)
    {
        Kind        = kind;
        SpeakerName = speakerName ?? "";
        CreatedAt   = when;
        _fullText   = text ?? "";

        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin    = new Padding(0, 0, 0, 8);

        // Dots timer (streaming animation — 3 sequentially lit dots)
        _dotsTimer = new System.Windows.Forms.Timer { Interval = 420 };
        _dotsTimer.Tick += (_, _) => { _dotsPhase = (_dotsPhase + 1) % 3; Invalidate(); };

        // Main text control (always present; hidden when segments exist)
        _mainText = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = ScrollBars.None,
            WordWrap    = true,
            Font        = kind == BubbleKind.Note ? NoteFont : BodyFont,
            BackColor   = GetFill(kind),
            ForeColor   = GetTextColor(kind),
            TabStop     = false,
            Cursor      = Cursors.IBeam,
            HideSelection = false,
            Text        = _fullText,
        };
        Controls.Add(_mainText);

        // Meta line: "Speaker Â· HH:mm"
        _meta = new Label
        {
            AutoSize  = true,
            Font      = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(_meta);
        UpdateMeta();

        // Inline copy glyph (âŽ˜ = U+2398 copy symbol)
        _copyBtn = new Label
        {
            Text      = "\u29c9",   // â§‰ overlapping squares, universally readable
            AutoSize  = true,
            Font      = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Hand,
        };
        _copyBtn.Click += (_, _) => { if (!string.IsNullOrEmpty(_fullText)) Clipboard.SetText(_fullText); };
        Controls.Add(_copyBtn);

        // Context menu
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy message", null, (_, _) => { if (!string.IsNullOrEmpty(_fullText)) Clipboard.SetText(_fullText); });
        menu.Items.Add("Select all",   null, (_, _) => { _mainText.Focus(); _mainText.SelectAll(); });
        ContextMenuStrip = menu;
        _mainText.ContextMenuStrip = menu;

        // Build segments immediately for non-streaming assistant messages
        if (_fullText.Length > 0 && kind == BubbleKind.Assistant)
            RebuildContent();
    }

    // â”€â”€ Streaming â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void BeginStreaming()
    {
        IsStreaming = true;
        _dotsTimer.Start();
        Invalidate();
    }

    public void AppendStreamingText(string s)
    {
        _fullText    += s;
        _mainText.AppendText(s);
        _pendingChars += s.Length;
        // Only relayout when enough text has accumulated OR a newline forces a height change.
        // Measure the new required height first; skip expensive Relayout if it won't change.
        if (_pendingChars >= 64 || s.Contains('\n'))
        {
            _pendingChars = 0;
            var prevH = Height;
            Relayout();
            // If height changed, parent flow already repositioned; otherwise just paint.
            if (Height == prevH) Invalidate();
        }
    }

    public void FinishStreaming()
    {
        IsStreaming   = false;
        _dotsTimer.Stop();
        _pendingChars = 0;
        RebuildContent();
        Relayout();
        Invalidate();
    }

    public void SetAvailableWidth(int w)
    {
        _availableWidth = Math.Max(240, w);
        Relayout();
    }

    // â”€â”€ Code-block parsing and segment construction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static readonly Regex s_code =
        new(@"```(\w*)\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    private static List<(string text, bool isCode, string lang)> ParseSegments(string text)
    {
        var result = new List<(string, bool, string)>();
        int pos = 0;
        foreach (Match m in s_code.Matches(text))
        {
            if (m.Index > pos) result.Add((text[pos..m.Index], false, ""));
            result.Add((m.Groups[2].Value, true, m.Groups[1].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add((text[pos..], false, ""));
        if (result.Count == 0) result.Add((text, false, ""));
        return result;
    }

    private void RebuildContent()
    {
        if (Kind != BubbleKind.Assistant) return;
        var segs    = ParseSegments(_fullText);
        var hasCode = segs.Any(s => s.isCode);
        if (!hasCode && !_hasCodeBlocks) return;

        // Dispose old segment controls
        foreach (var old in _segControls) { Controls.Remove(old); old.Dispose(); }
        _segControls.Clear();

        _hasCodeBlocks = hasCode;
        if (!hasCode) { _mainText.Visible = true; _mainText.Text = _fullText; return; }

        _mainText.Visible = false;
        foreach (var (text, isCode, lang) in segs)
        {
            if (string.IsNullOrEmpty(text) && !isCode) continue;
            Control seg = isCode ? CreateCodePanel(text, lang) : CreatePlainSegment(text);
            _segControls.Add(seg);
            Controls.Add(seg);
        }
    }

    private TextBox CreatePlainSegment(string text)
    {
        var tb = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = ScrollBars.None,
            WordWrap    = true,
            Font        = BodyFont,
            BackColor   = GetFill(Kind),
            ForeColor   = GetTextColor(Kind),
            TabStop     = false,
            Cursor      = Cursors.IBeam,
            HideSelection = false,
            Text        = text.Trim('\n'),
        };
        tb.ContextMenuStrip = _mainText.ContextMenuStrip;
        return tb;
    }

    private Panel CreateCodePanel(string code, string lang)
    {
        var codeBg   = UiTheme.CodeBlockBg;
        var codeFg   = UiTheme.IsDark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(30, 30, 30);
        var hdrColor = Color.FromArgb(130, 130, 160);

        var panel = new Panel { BackColor = codeBg };

        var pillBg  = UiTheme.IsDark ? Color.FromArgb(38, 65, 110) : Color.FromArgb(215, 230, 255);
        var langLbl = new Label
        {
            Text      = string.IsNullOrEmpty(lang) ? "code" : lang,
            AutoSize  = false,
            Font      = CodeHdrFont,
            ForeColor = hdrColor,
            BackColor = pillBg,
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(CodePadH, 4),
            Height    = 18,
        };
        langLbl.Width = TextRenderer.MeasureText(langLbl.Text, langLbl.Font).Width + 12;
        UiTheme.ApplyRoundedRegion(langLbl, 9);

        var copyLbl = new Label
        {
            Text      = "Copy code",
            AutoSize  = true,
            Font      = CodeHdrFont,
            ForeColor = hdrColor,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Hand,
        };
        copyLbl.Click += (_, _) => Clipboard.SetText(code);

        var codeBox = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = ScrollBars.Horizontal,
            WordWrap    = false,
            Font        = CodeFont,
            BackColor   = codeBg,
            ForeColor   = codeFg,
            Text        = code.TrimEnd('\n'),
            TabStop     = false,
        };

        panel.Controls.Add(langLbl);
        panel.Controls.Add(copyLbl);
        panel.Controls.Add(codeBox);
        panel.Tag = new CodePanelRefs(langLbl, copyLbl, codeBox);
        return panel;
    }

    private record CodePanelRefs(Label LangLbl, Label CopyLbl, TextBox CodeBox);

    // â”€â”€ Layout â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void Relayout()
    {
        if (_hasCodeBlocks && _segControls.Count > 0) RelayoutSegments();
        else                                           RelayoutSingle();
        Invalidate();
    }

    private void RelayoutSingle()
    {
        var maxBubble   = (int)(_availableWidth * MaxFraction);
        if (maxBubble < 240) maxBubble = Math.Min(_availableWidth, 240);
        var maxTextW    = Math.Max(80, maxBubble - InnerPadX * 2);

        var content = string.IsNullOrEmpty(_mainText.Text) ? " " : _mainText.Text;
        var measured = TextRenderer.MeasureText(content, _mainText.Font,
            new Size(maxTextW, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);

        // Ensure enough width to display the three-dot animation when no text yet
        int dotMinW = (IsStreaming && string.IsNullOrEmpty(_mainText.Text)) ? 64 : 0;
        var textW = Math.Min(maxTextW, Math.Max(Math.Max(40, dotMinW), measured.Width + 4));
        var textH = Math.Max(_mainText.Font.Height, measured.Height) + 16; // +16 prevents last-line descender clipping
        var bW    = textW + InnerPadX * 2;
        var bH    = textH + InnerPadY * 2;

        bool right  = Kind == BubbleKind.User;
        bool center = Kind == BubbleKind.Note || Kind == BubbleKind.Error;
        int  bX     = right  ? _availableWidth - bW
                    : center ? Math.Max(0, (_availableWidth - bW) / 2)
                             : 0;

        _bubbleBounds = new Rectangle(bX, 0, bW, bH);
        _mainText.SetBounds(bX + InnerPadX, InnerPadY, textW, textH);
        PositionMetaAndCopy(bX, bW, bH, right, center);

        Width  = _availableWidth;
        Height = bH + BubbleBottomGap + Math.Max(MetaHeight, _meta.Height);
    }

    private void RelayoutSegments()
    {
        var maxBubble = (int)(_availableWidth * MaxFraction);
        if (maxBubble < 240) maxBubble = Math.Min(_availableWidth, 240);
        int innerW = maxBubble - InnerPadX * 2;
        int y      = InnerPadY;

        foreach (var seg in _segControls)
        {
            if (seg is Panel cp && cp.Tag is CodePanelRefs refs)
            {
                // Measure code lines
                int lineCount = Math.Max(1, refs.CodeBox.Text.Split('\n').Length);
                int codeH     = lineCount * (refs.CodeBox.Font.Height + 2) + 6;
                int panelH    = CodeTopBar + codeH + CodePadV * 2;

                cp.SetBounds(0, y, maxBubble, panelH);
                refs.CopyLbl.Location = new Point(maxBubble - refs.CopyLbl.PreferredWidth - CodePadH, 5);
                refs.CodeBox.SetBounds(CodePadH, CodeTopBar + CodePadV,
                    maxBubble - CodePadH * 2, codeH);
                y += panelH + SegGap;
            }
            else if (seg is TextBox tb)
            {
                var content  = string.IsNullOrEmpty(tb.Text) ? " " : tb.Text;
                var measured = TextRenderer.MeasureText(content, tb.Font,
                    new Size(innerW, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                int tw = Math.Min(innerW, Math.Max(40, measured.Width + 4));
                int th = Math.Max(tb.Font.Height, measured.Height) + 16; // +16 prevents last-line descender clipping
                tb.SetBounds(InnerPadX, y, tw, th);
                y += th + SegGap;
            }
        }

        int bH = y - SegGap + InnerPadY;
        _bubbleBounds = new Rectangle(0, 0, maxBubble, bH);
        PositionMetaAndCopy(0, maxBubble, bH, false, false);

        Width  = _availableWidth;
        Height = bH + BubbleBottomGap + Math.Max(MetaHeight, _meta.Height);
    }

    private void PositionMetaAndCopy(int bX, int bW, int bH, bool right, bool center)
    {
        UpdateMeta();
        var ms = TextRenderer.MeasureText(_meta.Text, _meta.Font);
        int my = bH + BubbleBottomGap;
        _meta.Location = new Point(
            right  ? bX + bW - ms.Width
                   : center ? bX + (bW - ms.Width) / 2
                            : bX + 4,
            my);
        _copyBtn.Visible  = Kind == BubbleKind.User || Kind == BubbleKind.Assistant;
        _copyBtn.Location = new Point(
            right  ? _meta.Left - _copyBtn.PreferredWidth - 6
                   : _meta.Right + 6,
            my);
    }

    private void UpdateMeta()
    {
        var ts   = CreatedAt.ToLocalTime().ToString("HH:mm");
        var base_ = string.IsNullOrEmpty(SpeakerName) ? ts : $"{SpeakerName}  \u00b7  {ts}";
        _meta.Text = base_ + _statsSuffix;
    }

    // â”€â”€ Painting â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill   = GetFill(Kind);
        var border = GetBorder(Kind);

        using var path = UiTheme.MakeRoundedPath(_bubbleBounds, BubbleRadius);
        using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
        if (border != Color.Empty)
        {
            using var p = new Pen(border, 1f);
            e.Graphics.DrawPath(p, path);
        }

        // Three sequentially-lit dots while waiting for first token
        if (IsStreaming && string.IsNullOrEmpty(_fullText))
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int cy  = _bubbleBounds.Y + _bubbleBounds.Height / 2;
            int cx  = _bubbleBounds.X + InnerPadX + 6;
            const int DotSz = 7, DotGap = 13;
            for (int i = 0; i < 3; i++)
            {
                int alpha = _dotsPhase == i ? 210 : 80;
                using var db = new SolidBrush(Color.FromArgb(alpha, GetTextColor(Kind)));
                e.Graphics.FillEllipse(db, cx + i * DotGap, cy - DotSz / 2, DotSz, DotSz);
            }
        }
    }

    // Theme refresh

    public void RefreshTheme()
    {
        _mainText.BackColor = GetFill(Kind);
        _mainText.ForeColor = GetTextColor(Kind);
        _meta.ForeColor     = UiTheme.TextSecondary;
        _copyBtn.ForeColor  = UiTheme.TextSecondary;

        foreach (var seg in _segControls)
        {
            if (seg is TextBox tb)
            {
                tb.BackColor = GetFill(Kind);
                tb.ForeColor = GetTextColor(Kind);
            }
            else if (seg is Panel cp && cp.Tag is CodePanelRefs refs)
            {
                var codeBg = UiTheme.CodeBlockBg;
                var codeFg = UiTheme.IsDark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(30, 30, 30);
                var pillBg = UiTheme.IsDark ? Color.FromArgb(38, 65, 110) : Color.FromArgb(215, 230, 255);
                cp.BackColor            = codeBg;
                refs.LangLbl.BackColor  = pillBg;
                refs.CodeBox.BackColor  = codeBg;
                refs.CodeBox.ForeColor  = codeFg;
            }
        }
        Invalidate();
    }

    private static Color GetFill(BubbleKind k) => k switch
    {
        BubbleKind.User      => UiTheme.UserBubbleFill,
        BubbleKind.Assistant => UiTheme.AssistantBubbleFill,
        BubbleKind.Note      => UiTheme.NoteBg,
        BubbleKind.Error     => UiTheme.ErrorBg,
        _                    => UiTheme.SurfaceCard,
    };

    private static Color GetTextColor(BubbleKind k) => k switch
    {
        BubbleKind.User      => UiTheme.TextPrimary,
        BubbleKind.Assistant => UiTheme.TextPrimary,
        BubbleKind.Note      => UiTheme.IsDark ? Color.FromArgb(230, 190, 100) : Color.FromArgb(116, 86, 12),
        BubbleKind.Error     => UiTheme.Danger,
        _                    => UiTheme.TextPrimary,
    };

    private static Color GetBorder(BubbleKind k) => k switch
    {
        BubbleKind.User      => UiTheme.UserBubbleBorder,
        BubbleKind.Assistant => UiTheme.Border,
        BubbleKind.Note      => UiTheme.IsDark ? Color.FromArgb(100, 80, 30) : Color.FromArgb(245, 220, 160),
        BubbleKind.Error     => UiTheme.IsDark ? Color.FromArgb(150, 50, 50) : Color.FromArgb(240, 180, 175),
        _                    => Color.Empty,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dotsTimer.Dispose();
        base.Dispose(disposing);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ImageBubble
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Renders an image generated by the ImageGen plugin tool inline in the chat transcript.
/// Shows a PictureBox (Zoom) with a filename label beneath. Handles missing/unreadable files.
/// </summary>
internal sealed class ImageBubble : Panel
{
    private const int MaxDisplayWidth  = 480;
    private const int MaxDisplayHeight = 360;
    private const int LabelHeight      = 20;
    private const int ImagePadding     = 8;

    private readonly PictureBox _pic;
    private readonly Label      _label;
    private int                 _availableWidth = 600;

    public ImageBubble(string imagePath, string filename)
    {
        BackColor = Color.Transparent;
        Margin    = new Padding(0, 0, 0, 8);

        _pic = new PictureBox
        {
            SizeMode  = PictureBoxSizeMode.Zoom,
            BackColor = UiTheme.SurfaceCard,
            Cursor    = Cursors.Hand,
        };

        _label = new Label
        {
            Text      = filename,
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Default,
        };

        if (File.Exists(imagePath))
        {
            try
            {
                // Load into memory so the file handle is released immediately.
                using var fs  = File.OpenRead(imagePath);
                var ms        = new System.IO.MemoryStream();
                fs.CopyTo(ms);
                ms.Position  = 0;
                _pic.Image   = System.Drawing.Image.FromStream(ms);
            }
            catch (Exception ex)
            {
                _label.Text = $"[Image load error: {ex.Message}]";
            }
        }

        // Double-click opens the image in the default viewer.
        _pic.DoubleClick += (_, _) =>
        {
            if (File.Exists(imagePath))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(imagePath) { UseShellExecute = true }); }
                catch { /* ignore */ }
        };

        Controls.Add(_pic);
        Controls.Add(_label);
        Relayout();
    }

    public void SetAvailableWidth(int w)
    {
        _availableWidth = Math.Max(240, w);
        Relayout();
    }

    private void Relayout()
    {
        var img       = _pic.Image;
        int maxW      = Math.Min(MaxDisplayWidth, _availableWidth - ImagePadding * 2);
        int maxH      = MaxDisplayHeight;

        int picW, picH;
        if (img is null)
        {
            picW = maxW;
            picH = 80;
        }
        else
        {
            double scale = Math.Min((double)maxW / img.Width, (double)maxH / img.Height);
            scale        = Math.Min(scale, 1.0); // never upscale
            picW         = Math.Max(40, (int)(img.Width  * scale));
            picH         = Math.Max(40, (int)(img.Height * scale));
        }

        _pic.SetBounds(ImagePadding, ImagePadding, picW, picH);
        _label.SetBounds(ImagePadding, ImagePadding + picH + 2, picW, LabelHeight);
        Width  = _availableWidth;
        Height = ImagePadding + picH + 2 + LabelHeight + ImagePadding;
    }

    public void RefreshTheme()
    {
        _pic.BackColor   = UiTheme.SurfaceCard;
        _label.ForeColor = UiTheme.TextSecondary;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pic.Image?.Dispose();
        base.Dispose(disposing);
    }
}

