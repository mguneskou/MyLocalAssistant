using System.Drawing.Drawing2D;
using System.Text;

namespace MyLocalAssistant.Client.UI;

internal enum BubbleKind { User, Assistant, Note, Error }

/// <summary>
/// A modern chat transcript control. Each turn is rendered as a rounded "bubble":
/// user messages right-aligned with the accent fill, assistant messages left-aligned
/// on a light card, tool/system notes centered. Text inside each bubble is selectable
/// (so testers can copy answers) and a context menu gives quick copy actions.
/// </summary>
internal sealed class ChatTranscript : Panel
{
    private readonly FlowLayoutPanel _flow;
    private ChatBubble? _streaming;
    private bool _pinnedToBottom = true;

    public ChatTranscript()
    {
        BackColor = UiTheme.SurfaceCard;
        DoubleBuffered = true;

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = UiTheme.SurfaceCard,
            Padding = new Padding(20, 16, 20, 16),
        };
        Controls.Add(_flow);

        _flow.Resize += (_, _) => RelayoutAll();
        _flow.Scroll += (_, _) => _pinnedToBottom = IsScrolledToBottom();
        _flow.MouseWheel += (_, _) => _pinnedToBottom = IsScrolledToBottom();

        // Right-click on empty area: copy whole transcript / clear.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy whole transcript", null, (_, _) =>
        {
            var t = GetTranscriptText();
            if (!string.IsNullOrEmpty(t)) Clipboard.SetText(t);
        });
        _flow.ContextMenuStrip = menu;
    }

    /// <summary>Remove every message and reset streaming state.</summary>
    public void Clear()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls) c.Dispose();
        _flow.Controls.Clear();
        _flow.ResumeLayout();
        _streaming = null;
        _pinnedToBottom = true;
    }

    public void AppendUserMessage(string text)
        => Add(new ChatBubble(BubbleKind.User, text, "You", DateTime.UtcNow));

    public void AppendAssistantMessage(string speaker, string text)
        => Add(new ChatBubble(BubbleKind.Assistant, text, speaker, DateTime.UtcNow));

    public void AppendNote(string text, BubbleKind kind = BubbleKind.Note)
        => Add(new ChatBubble(kind, text, "", DateTime.UtcNow));

    /// <summary>Begin a new assistant bubble that subsequent <see cref="AppendAssistantText"/> calls flow into.</summary>
    public void BeginAssistantStream(string speaker)
    {
        _streaming = new ChatBubble(BubbleKind.Assistant, "", speaker, DateTime.UtcNow);
        _streaming.BeginStreaming();
        Add(_streaming);
    }

    public void AppendAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_streaming is null) BeginAssistantStream("Assistant");
        _streaming!.AppendStreamingText(text);
        if (_pinnedToBottom) ScrollToEnd();
    }

    public void EndAssistantStream()
    {
        _streaming?.FinishStreaming();
        _streaming = null;
    }

    private void Add(ChatBubble b)
    {
        _pinnedToBottom = IsScrolledToBottom();
        b.SetAvailableWidth(ContentWidth());
        _flow.Controls.Add(b);
        if (_pinnedToBottom) ScrollToEnd();
    }

    private void RelayoutAll()
    {
        _flow.SuspendLayout();
        var w = ContentWidth();
        foreach (Control c in _flow.Controls)
            if (c is ChatBubble b) b.SetAvailableWidth(w);
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
        var last = _flow.Controls[_flow.Controls.Count - 1];
        _flow.ScrollControlIntoView(last);
        _pinnedToBottom = true;
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
}

/// <summary>
/// One message rendered as a rounded bubble plus a small meta line (speaker + time).
/// Uses an inner read-only multiline TextBox for selectable text; the rounded
/// background and alignment are owner-painted by the surrounding panel.
/// </summary>
internal sealed class ChatBubble : Panel
{
    private static readonly Font BodyFont   = new("Segoe UI", 10.5F);
    private static readonly Font NoteFont   = new("Segoe UI", 9.5F, FontStyle.Italic);

    private const int BubbleRadius   = 12;
    private const int InnerPadX      = 14;
    private const int InnerPadY      = 9;
    private const int BubbleBottomGap = 4;
    private const int MetaHeight     = 16;
    private const double MaxBubbleFraction = 0.78;

    private readonly TextBox _text;
    private readonly Label   _meta;
    private Rectangle _bubbleBounds;
    private int _availableWidth = 600;
    private int _pendingCharsSinceMeasure;

    public BubbleKind Kind { get; }
    public string SpeakerName { get; private set; }
    public DateTime CreatedAt { get; }
    public bool IsStreaming { get; private set; }
    public string PlainText => _text.Text;

    public ChatBubble(BubbleKind kind, string text, string speakerName, DateTime when)
    {
        Kind = kind;
        SpeakerName = speakerName ?? "";
        CreatedAt = when;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 8);

        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.None,
            WordWrap = true,
            Font = kind == BubbleKind.Note ? NoteFont : BodyFont,
            BackColor = GetFill(kind),
            ForeColor = GetTextColor(kind),
            TabStop = false,
            Cursor = Cursors.IBeam,
            Text = text ?? "",
        };
        Controls.Add(_text);

        _meta = new Label
        {
            AutoSize = true,
            Font = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(_meta);
        UpdateMeta();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy message", null, (_, _) =>
        {
            if (!string.IsNullOrEmpty(_text.Text)) Clipboard.SetText(_text.Text);
        });
        menu.Items.Add("Select all in this message", null, (_, _) =>
        {
            _text.Focus();
            _text.SelectAll();
        });
        ContextMenuStrip = menu;
        _text.ContextMenuStrip = menu;
    }

    /// <summary>Append more streamed text (called many times during generation, must be cheap).</summary>
    public void AppendStreamingText(string s)
    {
        _text.AppendText(s);
        _pendingCharsSinceMeasure += s.Length;
        // Re-measure occasionally instead of per-token (prevents O(n^2) behaviour for long replies).
        if (_pendingCharsSinceMeasure >= 64 || s.IndexOf('\n') >= 0)
        {
            _pendingCharsSinceMeasure = 0;
            Relayout();
        }
    }

    public void FinishStreaming()
    {
        IsStreaming = false;
        _pendingCharsSinceMeasure = 0;
        Relayout();
        Invalidate();
    }

    public void BeginStreaming() { IsStreaming = true; Invalidate(); }

    public void SetAvailableWidth(int w)
    {
        _availableWidth = Math.Max(240, w);
        Relayout();
    }

    private void UpdateMeta()
    {
        var ts = CreatedAt.ToLocalTime().ToString("HH:mm");
        _meta.Text = string.IsNullOrEmpty(SpeakerName) ? ts : $"{SpeakerName}  \u00b7  {ts}";
    }

    private void Relayout()
    {
        var maxBubble = (int)(_availableWidth * MaxBubbleFraction);
        if (maxBubble < 240) maxBubble = Math.Min(_availableWidth, 240);
        var maxTextWidth = Math.Max(80, maxBubble - InnerPadX * 2);

        var content = string.IsNullOrEmpty(_text.Text) ? " " : _text.Text;
        // TextRenderer with WordBreak gives wrapped size that matches the TextBox layout closely enough.
        var measured = TextRenderer.MeasureText(content, _text.Font, new Size(maxTextWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);

        var textWidth  = Math.Min(maxTextWidth, Math.Max(40, measured.Width + 4));
        var textHeight = Math.Max(_text.Font.Height, measured.Height);
        var bubbleW    = textWidth + InnerPadX * 2;
        var bubbleH    = textHeight + InnerPadY * 2;

        var alignRight = Kind == BubbleKind.User;
        var center     = Kind == BubbleKind.Note || Kind == BubbleKind.Error;
        int bubbleX = alignRight ? _availableWidth - bubbleW
                    : center     ? Math.Max(0, (_availableWidth - bubbleW) / 2)
                                 : 0;

        _bubbleBounds = new Rectangle(bubbleX, 0, bubbleW, bubbleH);
        _text.SetBounds(bubbleX + InnerPadX, InnerPadY, textWidth, textHeight);

        UpdateMeta();
        var metaSize = TextRenderer.MeasureText(_meta.Text, _meta.Font);
        _meta.Location = new Point(
            alignRight ? bubbleX + bubbleW - metaSize.Width
                       : center ? bubbleX + (bubbleW - metaSize.Width) / 2
                                : bubbleX + 4,
            bubbleH + BubbleBottomGap);

        Width  = _availableWidth;
        Height = bubbleH + BubbleBottomGap + Math.Max(MetaHeight, metaSize.Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill   = GetFill(Kind);
        var border = GetBorder(Kind);

        using var path = UiTheme.MakeRoundedPath(_bubbleBounds, BubbleRadius);
        using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
        if (border != Color.Empty)
        {
            using var p = new Pen(border);
            e.Graphics.DrawPath(p, path);
        }

        // Pulsing ellipsis while we wait for the very first token.
        if (IsStreaming && string.IsNullOrEmpty(_text.Text))
        {
            using var dotBrush = new SolidBrush(GetTextColor(Kind));
            int cy = _bubbleBounds.Y + _bubbleBounds.Height / 2 - 2;
            int cx = _bubbleBounds.X + InnerPadX;
            for (int i = 0; i < 3; i++)
                e.Graphics.FillEllipse(dotBrush, cx + i * 10, cy, 5, 5);
        }
    }

    private static Color GetFill(BubbleKind k) => k switch
    {
        BubbleKind.User      => UiTheme.Accent,
        BubbleKind.Assistant => Color.FromArgb(243, 244, 248),
        BubbleKind.Note      => Color.FromArgb(255, 247, 225),
        BubbleKind.Error     => Color.FromArgb(253, 232, 230),
        _                    => UiTheme.SurfaceCard,
    };

    private static Color GetTextColor(BubbleKind k) => k switch
    {
        BubbleKind.User      => Color.White,
        BubbleKind.Assistant => UiTheme.TextPrimary,
        BubbleKind.Note      => Color.FromArgb(116, 86, 12),
        BubbleKind.Error     => UiTheme.Danger,
        _                    => UiTheme.TextPrimary,
    };

    private static Color GetBorder(BubbleKind k) => k switch
    {
        BubbleKind.Assistant => UiTheme.Border,
        BubbleKind.Note      => Color.FromArgb(245, 220, 160),
        BubbleKind.Error     => Color.FromArgb(240, 180, 175),
        _                    => Color.Empty,
    };
}
