using System.Diagnostics;
using MyLocalAssistant.Client.Services;
using MyLocalAssistant.Client.UI;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Client.Forms;

internal sealed class ChatForm : Form
{
    private readonly ChatApiClient _client;
    private readonly ClientSettingsStore _store;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripComboBox _agentCombo;
    private readonly ToolStripLabel _agentDescription;
    private readonly ToolStripButton _changePwdBtn;
    private readonly ToolStripButton _signOutBtn;
    private readonly SplitContainer _split;
    private readonly ListBox _conversationList;
    private readonly Button _newChatBtn;
    private readonly Button _deleteChatBtn;
    private readonly RichTextBox _history;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _statsLabel;

    private CancellationTokenSource? _streamCts;
    private bool _streaming;
    private List<AgentDto> _agents = new();
    private List<ConversationSummaryDto> _conversations = new();
    private Guid? _currentConversationId;
    private bool _suppressConversationSelection;

    // One-shot attachment for the next turn. Cleared after send.
    private AttachmentExtractResult? _pendingAttachment;
    private readonly Panel _attachChip;
    private readonly Label _attachLabel;
    private readonly Button _attachClear;
    private readonly Button _attachBtn;

    public ChatForm(ChatApiClient client, ClientSettingsStore store)
    {
        _client = client;
        _store = store;

        Text = $"MyLocalAssistant \u2014 {_client.CurrentUser?.DisplayName ?? "?"} @ {_client.BaseUrl}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1180, 760);
        UiTheme.ApplyForm(this);

        _toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Dock = DockStyle.Top,
            ImageScalingSize = new Size(16, 16),
            BackColor = UiTheme.SurfaceCard,
            Padding = new Padding(8, 4, 8, 4),
            Renderer = new UiTheme.ModernRenderer(),
        };
        _agentCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = UiTheme.BaseFont };
        _agentCombo.ComboBox!.DisplayMember = nameof(AgentDto.Name);
        _agentCombo.SelectedIndexChanged += async (_, _) => await OnAgentChangedAsync();
        _agentDescription = new ToolStripLabel("") { ForeColor = UiTheme.TextSecondary, Font = UiTheme.Caption };
        _changePwdBtn = new ToolStripButton("Change password\u2026") { Font = UiTheme.BaseFont };
        _changePwdBtn.Click += (_, _) => { using var d = new ChangePasswordForm(_client, forced: false); d.ShowDialog(this); };
        _signOutBtn = new ToolStripButton("Sign out") { Font = UiTheme.BaseFont };
        _signOutBtn.Click += (_, _) => { DialogResult = DialogResult.Retry; Close(); };
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("Agent: "),
            _agentCombo,
            new ToolStripSeparator(),
            _agentDescription,
            new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
            _signOutBtn,
            _changePwdBtn,
        });
        _changePwdBtn.Alignment = ToolStripItemAlignment.Right;
        _signOutBtn.Alignment = ToolStripItemAlignment.Right;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            BackColor = UiTheme.Border,
            SplitterWidth = 1,
            // NOTE: Panel1MinSize / Panel2MinSize / SplitterDistance are deliberately NOT
            // set here. SplitContainer re-validates SplitterDistance every time you touch
            // any of those, against the container's CURRENT width (which is the default
            // 150 px until docking takes effect on the first layout pass). Setting them
            // now throws InvalidOperationException; we apply them in HandleCreated below
            // once the container has been resized to the form's full width.
        };
        _split.Panel1.BackColor = UiTheme.SurfaceAlt;
        _split.Panel2.BackColor = UiTheme.Surface;
        _split.HandleCreated += (_, _) =>
        {
            try
            {
                _split.SuspendLayout();
                // Always satisfy Panel1MinSize <= SplitterDistance <= Width - Panel2MinSize.
                var width = Math.Max(_split.Width, 600);
                var p1Min = Math.Min(120, width / 4);
                var p2Min = Math.Min(320, width / 2);
                var dist = Math.Clamp(260, p1Min, Math.Max(p1Min + 1, width - p2Min));
                _split.Panel1MinSize = p1Min;
                _split.Panel2MinSize = p2Min;
                _split.SplitterDistance = dist;
            }
            catch { /* keep defaults rather than crash */ }
            finally { _split.ResumeLayout(); }
        };

        // Left pane: conversations.
        var leftHeader = new Label
        {
            Text = "  Conversations",
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = UiTheme.BaseBold,
            ForeColor = UiTheme.TextPrimary,
            BackColor = UiTheme.SurfaceAlt,
        };
        var leftButtons = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8, 6, 8, 8),
            BackColor = UiTheme.SurfaceAlt,
        };
        _newChatBtn = new Button { Text = "+  New chat", Width = 110, Height = 32, Dock = DockStyle.Left };
        UiTheme.Primary(_newChatBtn);
        _newChatBtn.Click += (_, _) => StartNewChat();
        _deleteChatBtn = new Button { Text = "Delete", Width = 80, Height = 32, Dock = DockStyle.Right, Enabled = false };
        UiTheme.Secondary(_deleteChatBtn);
        _deleteChatBtn.Click += async (_, _) => await OnDeleteConversationAsync();
        leftButtons.Controls.Add(_newChatBtn);
        leftButtons.Controls.Add(_deleteChatBtn);

        _conversationList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            DisplayMember = nameof(ConversationSummaryDto.Title),
            BackColor = UiTheme.SurfaceAlt,
            ForeColor = UiTheme.TextPrimary,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 46,
        };
        _conversationList.DrawItem += OnDrawConversationItem;
        _conversationList.SelectedIndexChanged += async (_, _) => await OnConversationSelectedAsync();
        _split.Panel1.Controls.Add(_conversationList);
        _split.Panel1.Controls.Add(leftButtons);
        _split.Panel1.Controls.Add(leftHeader);

        // Right pane: history + input.
        _history = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10.5F),
            BackColor = UiTheme.SurfaceCard,
            ForeColor = UiTheme.TextPrimary,
            DetectUrls = false,
            Margin = new Padding(0),
        };
        var inputPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Padding = new Padding(12, 10, 12, 12),
            BackColor = UiTheme.Surface,
        };

        // Attachment chip strip (hidden when no attachment is pending).
        _attachChip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            Visible = false,
            BackColor = Color.FromArgb(232, 240, 254),
            Padding = new Padding(8, 4, 4, 4),
        };
        _attachLabel = new Label { AutoSize = true, Dock = DockStyle.Left, ForeColor = SystemColors.ControlText };
        _attachClear = new Button { Text = "\u2715", Dock = DockStyle.Right, Width = 26, FlatStyle = FlatStyle.Flat, TabStop = false };
        _attachClear.FlatAppearance.BorderSize = 0;
        _attachClear.Click += (_, _) => ClearAttachment();
        _attachChip.Controls.Add(_attachLabel);
        _attachChip.Controls.Add(_attachClear);

        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = false,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5F),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.SurfaceCard,
        };
        _input.KeyDown += OnInputKeyDown;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 120,
            WrapContents = false,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = UiTheme.Surface,
        };
        _attachBtn = new Button { Text = "\uD83D\uDCCE  Attach", Width = 110, Height = 34 };
        UiTheme.Secondary(_attachBtn);
        _attachBtn.Click += async (_, _) => await OnAttachAsync();
        _send = new Button { Text = "Send  \u23CE", Width = 110, Height = 60, Margin = new Padding(0, 8, 0, 0) };
        UiTheme.Primary(_send);
        _send.Click += async (_, _) => await OnSendOrCancelAsync();
        buttons.Controls.Add(_attachBtn);
        buttons.Controls.Add(_send);

        inputPanel.Controls.Add(_input);
        inputPanel.Controls.Add(buttons);
        inputPanel.Controls.Add(_attachChip);
        _split.Panel2.Controls.Add(_history);
        _split.Panel2.Controls.Add(inputPanel);

        _statusLabel = new ToolStripStatusLabel("Ready");
        _statsLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = UiTheme.TextSecondary };
        _status = new StatusStrip { BackColor = UiTheme.SurfaceCard, SizingGrip = false };
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_statsLabel);

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_toolbar);

        Load += async (_, _) => await ReloadAgentsAsync();
        FormClosing += (_, _) => _streamCts?.Cancel();
    }

    private async Task ReloadAgentsAsync()
    {
        try
        {
            _statusLabel.Text = "Loading agents\u2026";
            _agents = await _client.ListAgentsAsync();
            _agentCombo.ComboBox!.DataSource = _agents;
            if (_agents.Count == 0)
            {
                _statusLabel.Text = "No agents available. Ask your administrator to enable one.";
                _send.Enabled = false;
                return;
            }
            var settings = _store.Load();
            var preferIdx = !string.IsNullOrEmpty(settings.LastAgentId)
                ? _agents.FindIndex(a => a.Id == settings.LastAgentId)
                : -1;
            _agentCombo.SelectedIndex = preferIdx >= 0 ? preferIdx : 0;
            _statusLabel.Text = $"{_agents.Count} agent(s) available.";
            _send.Enabled = true;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed to load agents: " + ex.Message;
            _send.Enabled = false;
        }
    }

    private async Task OnAgentChangedAsync()
    {
        var a = _agentCombo.SelectedItem as AgentDto;
        _agentDescription.Text = a is null ? "" : "  " + a.Description;
        if (a is not null)
        {
            var s = _store.Load();
            s.LastAgentId = a.Id;
            _store.Save(s);
            StartNewChat();
            await ReloadConversationsAsync();
        }
    }

    private async Task ReloadConversationsAsync()
    {
        var agent = _agentCombo.SelectedItem as AgentDto;
        if (agent is null) return;
        try
        {
            _conversations = await _client.ListConversationsAsync(agent.Id);
            _suppressConversationSelection = true;
            _conversationList.BeginUpdate();
            _conversationList.Items.Clear();
            foreach (var c in _conversations)
                _conversationList.Items.Add(c);
            if (_currentConversationId is Guid cid)
            {
                var idx = _conversations.FindIndex(c => c.Id == cid);
                if (idx >= 0) _conversationList.SelectedIndex = idx;
            }
            _conversationList.EndUpdate();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed to load conversations: " + ex.Message;
        }
        finally
        {
            _suppressConversationSelection = false;
            _deleteChatBtn.Enabled = _conversationList.SelectedIndex >= 0;
        }
    }

    private void StartNewChat()
    {
        _currentConversationId = null;
        _suppressConversationSelection = true;
        _conversationList.ClearSelected();
        _suppressConversationSelection = false;
        _deleteChatBtn.Enabled = false;
        _history.Clear();
        _statusLabel.Text = "New chat.";
    }

    private async Task OnConversationSelectedAsync()
    {
        if (_suppressConversationSelection) return;
        if (_streaming) return;
        if (_conversationList.SelectedItem is not ConversationSummaryDto sel) return;
        _currentConversationId = sel.Id;
        _deleteChatBtn.Enabled = true;
        try
        {
            _statusLabel.Text = "Loading conversation\u2026";
            var detail = await _client.GetConversationAsync(sel.Id);
            _history.Clear();
            if (detail is null) { _statusLabel.Text = "Conversation not found."; return; }
            var agent = _agents.FirstOrDefault(a => a.Id == detail.AgentId);
            var assistantName = agent?.Name ?? "Assistant";
            foreach (var m in detail.Messages)
            {
                if (string.Equals(m.Role, "User", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRoleLine("You", UiTheme.UserName);
                    AppendBody((m.Body ?? "(empty)") + "\n\n");
                }
                else if (string.Equals(m.Role, "Assistant", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRoleLine(assistantName, UiTheme.AssistantName);
                    AppendBody((m.Body ?? "(empty)") + "\n\n");
                }
            }
            _statusLabel.Text = $"{detail.Messages.Count} message(s) loaded.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed to load conversation: " + ex.Message;
        }
    }

    private async Task OnDeleteConversationAsync()
    {
        if (_conversationList.SelectedItem is not ConversationSummaryDto sel) return;
        var ok = MessageBox.Show(this,
            $"Delete conversation '{sel.Title}'? This cannot be undone.",
            "Delete conversation", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (ok != DialogResult.OK) return;
        try
        {
            await _client.DeleteConversationAsync(sel.Id);
            if (_currentConversationId == sel.Id) StartNewChat();
            await ReloadConversationsAsync();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Delete failed: " + ex.Message;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            _ = OnSendOrCancelAsync();
        }
    }

    private async Task OnSendOrCancelAsync()
    {
        if (_streaming)
        {
            _streamCts?.Cancel();
            return;
        }
        var agent = _agentCombo.SelectedItem as AgentDto;
        if (agent is null) return;
        var typed = _input.Text.Trim();
        if (typed.Length == 0 && _pendingAttachment is null) return;

        // Compose: attached text first, then user's prompt. Format mirrors what the
        // server's ChatService prefixes to history lines, so the model treats it as context.
        string message;
        string displayMessage;
        if (_pendingAttachment is { } att)
        {
            var header = $"[Attached: {att.FileName}";
            if (att.Truncated) header += " (truncated)";
            header += "]\n";
            message = header + att.Text + "\n\n" + typed;
            displayMessage = $"\uD83D\uDCCE {att.FileName} ({att.CharCount:N0} chars)\n{typed}";
        }
        else
        {
            message = typed;
            displayMessage = typed;
        }

        _input.Clear();
        ClearAttachment();
        AppendRoleLine("You", UiTheme.UserName);
        AppendBody(displayMessage + "\n\n");
        AppendRoleLine(agent.Name, UiTheme.AssistantName);

        SetStreaming(true);
        var sw = Stopwatch.StartNew();
        var tokens = 0;
        var wasNew = _currentConversationId is null;
        try
        {
            _streamCts = new CancellationTokenSource();
            var req = new ChatRequest(agent.Id, message, ConversationId: _currentConversationId);
            await foreach (var frame in _client.StreamChatAsync(req, _streamCts.Token))
            {
                if (frame.Kind == TokenStreamFrameKind.Meta && frame.ConversationId is Guid mid)
                {
                    _currentConversationId = mid;
                }
                else if (frame.Kind == TokenStreamFrameKind.Token && frame.Text is not null)
                {
                    AppendBody(frame.Text);
                    tokens++;
                    var rate = sw.Elapsed.TotalSeconds > 0 ? tokens / sw.Elapsed.TotalSeconds : 0;
                    _statsLabel.Text = $"{tokens} tokens \u00b7 {rate:F1} tok/s";
                }
                else if (frame.Kind == TokenStreamFrameKind.Error)
                {
                    AppendBody("\n\n[Error: " + (frame.ErrorMessage ?? "unknown") + "]");
                    break;
                }
                else if (frame.Kind == TokenStreamFrameKind.End)
                {
                    break;
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolUnavailable)
                {
                    AppendBody($"\n[tool unavailable: {frame.ToolName ?? "?"} \u2014 {frame.ToolReason ?? "?"}]\n");
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolCall)
                {
                    AppendBody($"\n[\u2192 tool call: {frame.ToolName ?? "?"} {frame.ToolJson ?? ""}]\n");
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolResult)
                {
                    var label = string.Equals(frame.ToolReason, "error", StringComparison.Ordinal) ? "tool error" : "tool result";
                    AppendBody($"[\u2190 {label}: {frame.ToolName ?? "?"} {frame.ToolJson ?? ""}]\n");
                }
            }
            AppendBody("\n\n");
        }
        catch (OperationCanceledException)
        {
            AppendBody("\n\n[Cancelled]\n\n");
        }
        catch (Exception ex)
        {
            AppendBody("\n\n[Error: " + ex.Message + "]\n\n");
        }
        finally
        {
            sw.Stop();
            SetStreaming(false);
            _streamCts?.Dispose();
            _streamCts = null;
            if (tokens > 0)
                _statusLabel.Text = $"Done in {sw.Elapsed.TotalSeconds:F1}s ({tokens} tokens).";
            else
                _statusLabel.Text = "Done.";

            if (wasNew || tokens > 0)
                await ReloadConversationsAsync();
        }
    }

    private void AppendRoleLine(string who, Color color)
    {
        _history.SelectionStart = _history.TextLength;
        _history.SelectionLength = 0;
        _history.SelectionColor = color;
        _history.SelectionFont = new Font("Segoe UI Semibold", 10.5F);
        _history.AppendText(who + "\n");
        _history.SelectionColor = _history.ForeColor;
        _history.SelectionFont = _history.Font;
        _history.ScrollToCaret();
    }

    private void AppendBody(string text)
    {
        _history.SelectionStart = _history.TextLength;
        _history.SelectionLength = 0;
        _history.SelectionColor = _history.ForeColor;
        _history.SelectionFont = _history.Font;
        _history.AppendText(text);
        _history.ScrollToCaret();
    }

    private void SetStreaming(bool streaming)
    {
        _streaming = streaming;
        _send.Text = streaming ? "Cancel" : "Send";
        _agentCombo.Enabled = !streaming;
        _input.Enabled = !streaming;
        _newChatBtn.Enabled = !streaming;
        _conversationList.Enabled = !streaming;
        _attachBtn.Enabled = !streaming;
        if (streaming) { _statusLabel.Text = "Generating\u2026"; _statsLabel.Text = ""; }
    }

    private async Task OnAttachAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Attach a file to this turn",
            Filter = "Supported (*.txt;*.md;*.pdf;*.docx;*.html;*.htm)|*.txt;*.md;*.markdown;*.pdf;*.docx;*.html;*.htm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var oldAttach = _pendingAttachment;
        try
        {
            _attachBtn.Enabled = false;
            _statusLabel.Text = "Extracting attachment\u2026";
            var result = await _client.ExtractAttachmentAsync(dlg.FileName);
            _pendingAttachment = result;
            UpdateAttachmentChip();
            var note = result.Truncated ? " (truncated)" : "";
            _statusLabel.Text = $"Attached: {result.FileName} \u00b7 {result.PageCount} page(s) \u00b7 {result.CharCount:N0} chars{note}.";
        }
        catch (Exception ex)
        {
            _pendingAttachment = oldAttach;
            UpdateAttachmentChip();
            MessageBox.Show(this, ex.Message, "Attach failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "Attach failed.";
        }
        finally
        {
            _attachBtn.Enabled = !_streaming;
        }
    }

    private void ClearAttachment()
    {
        _pendingAttachment = null;
        UpdateAttachmentChip();
    }

    private void UpdateAttachmentChip()
    {
        if (_pendingAttachment is null)
        {
            _attachChip.Visible = false;
            _attachLabel.Text = "";
            return;
        }
        var a = _pendingAttachment;
        var note = a.Truncated ? " (truncated)" : "";
        _attachLabel.Text = $"\uD83D\uDCCE  {a.FileName}  \u00b7  {a.PageCount} page(s)  \u00b7  {a.CharCount:N0} chars{note}";
        _attachChip.Visible = true;
    }

    /// <summary>
    /// Owner-drawn conversation row: title on top, last-activity timestamp below in muted gray.
    /// Falls back to the raw title for empty rows.
    /// </summary>
    private void OnDrawConversationItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var item = _conversationList.Items[e.Index] as ConversationSummaryDto;

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bg = selected ? Color.FromArgb(225, 238, 251) : UiTheme.SurfaceAlt;
        using (var bgBrush = new SolidBrush(bg)) e.Graphics.FillRectangle(bgBrush, e.Bounds);

        if (item is null)
        {
            e.DrawFocusRectangle();
            return;
        }

        var titleFont = UiTheme.BaseBold;
        var subFont = UiTheme.Caption;
        var titleColor = UiTheme.TextPrimary;
        var subColor = UiTheme.TextSecondary;

        var rect = e.Bounds;
        var titleRect = new Rectangle(rect.Left + 12, rect.Top + 6, rect.Width - 16, 20);
        var subRect = new Rectangle(rect.Left + 12, rect.Top + 24, rect.Width - 16, 18);

        TextRenderer.DrawText(e.Graphics, item.Title, titleFont, titleRect, titleColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var stamp = item.UpdatedAt.ToLocalTime();
        var subText = stamp.Date == DateTime.Today
            ? $"Today {stamp:HH:mm}"
            : (DateTime.Today - stamp.Date).TotalDays < 7
                ? stamp.ToString("ddd HH:mm")
                : stamp.ToString("yyyy-MM-dd HH:mm");
        TextRenderer.DrawText(e.Graphics, subText, subFont, subRect, subColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        // Subtle bottom separator.
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, rect.Left + 8, rect.Bottom - 1, rect.Right - 8, rect.Bottom - 1);
    }
}
