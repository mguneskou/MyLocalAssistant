using System.Diagnostics;
using MyLocalAssistant.Client.Services;
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
        MinimumSize = new Size(820, 560);
        Size = new Size(1080, 720);
        Font = new Font("Segoe UI", 10F);

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, ImageScalingSize = new Size(16, 16) };
        _agentCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        _agentCombo.ComboBox!.DisplayMember = nameof(AgentDto.Name);
        _agentCombo.SelectedIndexChanged += async (_, _) => await OnAgentChangedAsync();
        _agentDescription = new ToolStripLabel("") { ForeColor = SystemColors.GrayText };
        _changePwdBtn = new ToolStripButton("Change password\u2026");
        _changePwdBtn.Click += (_, _) => { using var d = new ChangePasswordForm(_client, forced: false); d.ShowDialog(this); };
        _signOutBtn = new ToolStripButton("Sign out");
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
            Panel1MinSize = 180,
            Panel2MinSize = 380,
        };

        // Left pane: conversations.
        var leftHeader = new Label
        {
            Text = "Conversations",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(8, 4, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        };
        var leftButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(4),
        };
        _newChatBtn = new Button { Text = "New chat", Width = 100, Height = 26 };
        _newChatBtn.Click += (_, _) => StartNewChat();
        _deleteChatBtn = new Button { Text = "Delete", Width = 80, Height = 26, Enabled = false };
        _deleteChatBtn.Click += async (_, _) => await OnDeleteConversationAsync();
        leftButtons.Controls.Add(_newChatBtn);
        leftButtons.Controls.Add(_deleteChatBtn);

        _conversationList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            DisplayMember = nameof(ConversationSummaryDto.Title),
        };
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
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.White,
            DetectUrls = false,
        };
        var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 140, Padding = new Padding(8) };

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
            Font = new Font("Segoe UI", 10F),
        };
        _input.KeyDown += OnInputKeyDown;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 110,
            WrapContents = false,
            Padding = new Padding(4, 0, 0, 0),
        };
        _attachBtn = new Button { Text = "Attach\u2026", Width = 100, Height = 28 };
        _attachBtn.Click += async (_, _) => await OnAttachAsync();
        _send = new Button { Text = "Send", Width = 100, Height = 28 };
        _send.Click += async (_, _) => await OnSendOrCancelAsync();
        buttons.Controls.Add(_attachBtn);
        buttons.Controls.Add(_send);

        inputPanel.Controls.Add(_input);
        inputPanel.Controls.Add(buttons);
        inputPanel.Controls.Add(_attachChip);
        _split.Panel2.Controls.Add(_history);
        _split.Panel2.Controls.Add(inputPanel);

        _statusLabel = new ToolStripStatusLabel("Ready");
        _statsLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_statsLabel);

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        _split.SplitterDistance = 240;

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
                    AppendRoleLine("You", Color.SteelBlue);
                    AppendBody((m.Body ?? "(empty)") + "\n\n");
                }
                else if (string.Equals(m.Role, "Assistant", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRoleLine(assistantName, Color.SeaGreen);
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
        AppendRoleLine("You", Color.SteelBlue);
        AppendBody(displayMessage + "\n\n");
        AppendRoleLine(agent.Name, Color.SeaGreen);

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
        _history.SelectionFont = new Font(_history.Font, FontStyle.Bold);
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
}
