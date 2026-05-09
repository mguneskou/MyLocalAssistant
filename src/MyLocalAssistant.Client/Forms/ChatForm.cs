using System.Diagnostics;
using MyLocalAssistant.Client.Bridge;
using MyLocalAssistant.Client.Services;
using MyLocalAssistant.Client.UI;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Client.Forms;

internal sealed class ChatForm : Form
{
    private readonly ChatApiClient _client;
    private readonly ClientSettingsStore _store;
    private readonly Panel _topBar;
    private readonly Button _themeBtn;
    private readonly Button _changePwdBtn;
    private readonly Button _signOutBtn;
    private readonly Button _bridgeFolderBtn;
    private readonly SplitContainer _split;
    private readonly ListBox _conversationList;
    private readonly TextBox _searchBox;
    private readonly Button _newChatBtn;
    private readonly Button _deleteChatBtn;
    private readonly ComboBox _agentCombo;
    private readonly Button _agentInfoBtn;
    private readonly Label _agentDescription;
    private readonly Panel _agentRow;
    private readonly ChatTranscript _history;
    private readonly Panel _inputPanel;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _statsLabel;
    private readonly ToolStripStatusLabel _bridgeLabel;
    private readonly Panel _attachChip;
    private readonly Label _attachLabel;
    private readonly Button _attachClear;
    private readonly Button _attachBtn;
    private readonly Label _topBarLabel;

    private BridgeClient? _bridge;

    private CancellationTokenSource? _streamCts;
    private bool _streaming;
    private List<AgentDto> _agents = new();
    private List<ConversationSummaryDto> _conversations = new();
    private Guid? _currentConversationId;
    private bool _suppressConversationSelection;

    // One-shot attachment for the next turn. Cleared after send.
    private AttachmentExtractResult? _pendingAttachment;

    public ChatForm(ChatApiClient client, ClientSettingsStore store)
    {
        _client = client;
        _store = store;

        Text = $"MyLocalAssistant {Program.AppVersion} \u2014 {_client.CurrentUser?.DisplayName ?? "?"} @ {_client.BaseUrl}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1180, 760);
        UiTheme.ApplyForm(this);

        // \u2500\u2500 Flat top bar (replaces ToolStrip for a cleaner look) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        Button MakeBarBtn(string text, string? tip = null)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = UiTheme.BaseFont,
                BackColor = Color.Transparent,
                ForeColor = UiTheme.TextPrimary,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Right,
                Height = 40,
                Padding = new Padding(8, 0, 8, 0),
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceAlt;
            b.FlatAppearance.MouseDownBackColor = UiTheme.Border;
            if (tip != null) new ToolTip().SetToolTip(b, tip);
            return b;
        }

        _themeBtn = MakeBarBtn("\uD83C\uDF19", "Toggle light / dark theme");
        _themeBtn.Click += (_, _) => ToggleTheme();
        _bridgeFolderBtn = MakeBarBtn("\uD83D\uDCC1  Folder\u2026", "Pick the folder tools may read/write on this PC.");
        _bridgeFolderBtn.Click += (_, _) => PickBridgeFolder();
        _changePwdBtn = MakeBarBtn("Change password\u2026");
        _changePwdBtn.Click += (_, _) => { using var d = new ChangePasswordForm(_client, forced: false); d.ShowDialog(this); };
        _signOutBtn = MakeBarBtn("Sign out");
        _signOutBtn.Click += (_, _) => { DialogResult = DialogResult.Retry; Close(); };

        _topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = UiTheme.SurfaceCard,
            Padding = new Padding(8, 0, 8, 0),
        };
        _topBar.Paint += (_, e) => UiTheme.DrawBottomBorder(e.Graphics, _topBar.ClientRectangle);
        // Add right-to-left (Dock=Right, last added = rightmost).
        _topBar.Controls.Add(_themeBtn);
        _topBar.Controls.Add(_bridgeFolderBtn);
        _topBar.Controls.Add(_changePwdBtn);
        _topBar.Controls.Add(_signOutBtn);

        _topBarLabel = new Label
        {
            Text = "MyLocalAssistant",
            Dock = DockStyle.Left,
            Font = UiTheme.BaseBold,
            ForeColor = UiTheme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            AutoSize = true,
        };
        _topBar.Controls.Add(_topBarLabel);

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
        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            Font = UiTheme.BaseFont,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.SurfaceCard,
            ForeColor = UiTheme.TextPrimary,
            PlaceholderText = "Filter conversations\u2026 (Enter to search server)",
            Height = 28,
        };
        _searchBox.TextChanged += (_, _) => FilterConversations(_searchBox.Text);
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Return && !string.IsNullOrWhiteSpace(_searchBox.Text))
            {
                e.SuppressKeyPress = true;
                await SearchConversationsAsync(_searchBox.Text);
            }
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
            DrawMode = DrawMode.OwnerDrawVariable,
        };
        _conversationList.MeasureItem += (_, me) =>
            me.ItemHeight = _conversationList.Items[me.Index] is ConvHeader ? 22 : 48;
        _conversationList.DrawItem += OnDrawConversationItem;
        _conversationList.SelectedIndexChanged += async (_, _) =>
        {
            if (_conversationList.SelectedItem is ConvHeader)
            {
                _suppressConversationSelection = true;
                _conversationList.SelectedIndex = -1;
                _suppressConversationSelection = false;
                return;
            }
            await OnConversationSelectedAsync();
        };
        _split.Panel1.Controls.Add(_conversationList);
        _split.Panel1.Controls.Add(leftButtons);
        _split.Panel1.Controls.Add(_searchBox);
        _split.Panel1.Controls.Add(leftHeader);

        // Right pane: history + input.
        _history = new ChatTranscript { Dock = DockStyle.Fill };

        // Agent selector row (lives above the text input, below it visually).
        _agentCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = UiTheme.BaseFont,
            Dock = DockStyle.Fill,
            BackColor = UiTheme.SurfaceCard,
            ForeColor = UiTheme.TextPrimary,
        };
        _agentCombo.DisplayMember = nameof(AgentDto.Name);
        _agentCombo.SelectedIndexChanged += async (_, _) => await OnAgentChangedAsync();
        _agentInfoBtn = new Button
        {
            Text = "\u24D8", // ⓘ
            Font = UiTheme.Caption,
            Width = 24,
            Height = 24,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TabStop = false,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.TextSecondary,
            Enabled = false,
        };
        _agentInfoBtn.FlatAppearance.BorderSize = 0;
        _agentInfoBtn.Click += OnAgentInfoClicked;
        _agentDescription = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };
        var agentLbl = new Label
        {
            Text = "Agent:",
            AutoSize = true,
            Dock = DockStyle.Left,
            Font = UiTheme.Caption,
            ForeColor = UiTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 6, 0),
        };
        // Agent row: TableLayoutPanel keeps label fixed, combo + description flexible.
        var agentTlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface,
        };
        agentTlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // "Agent:" label
        agentTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));       // combo
        agentTlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // ⓘ button
        agentTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));       // description
        agentTlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        agentTlp.Controls.Add(agentLbl, 0, 0);
        agentTlp.Controls.Add(_agentCombo, 1, 0);
        agentTlp.Controls.Add(_agentInfoBtn, 2, 0);
        agentTlp.Controls.Add(_agentDescription, 3, 0);
        _agentCombo.Dock = DockStyle.Fill;
        _agentInfoBtn.Dock = DockStyle.Fill;
        _agentDescription.Dock = DockStyle.Fill;
        _agentRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = UiTheme.Surface,
            Padding = new Padding(4, 3, 4, 3),
        };
        _agentRow.Controls.Add(agentTlp);

        _inputPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 160,   // recalculated on first layout by UpdateInputHeight
            BackColor = UiTheme.Surface,
        };
        _inputPanel.Paint += (_, e) =>
        {
            using var p = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(p, 0, 0, _inputPanel.Width, 0);
        };

        // Attachment chip strip (hidden when no attachment is pending).
        _attachChip = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = UiTheme.AttachChipBg,
            Padding = new Padding(8, 2, 4, 2),
        };
        _attachLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Font = UiTheme.Caption,
        };
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
            ForeColor = UiTheme.TextPrimary,
            PlaceholderText = "Message\u2026",
        };
        _input.KeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => UpdateInputHeight();

        // Send / Attach as horizontal row: Attach left, Send right.
        _attachBtn = new Button { Text = "\uD83D\uDCCE  Attach", Height = 34, AutoSize = true, Padding = new Padding(10, 0, 10, 0) };
        UiTheme.Secondary(_attachBtn);
        _attachBtn.Click += async (_, _) => await OnAttachAsync();
        _send = new Button { Text = "Send  \u23CE", Height = 34, AutoSize = true, Padding = new Padding(20, 0, 20, 0) };
        UiTheme.Primary(_send);
        _send.Click += async (_, _) => await OnSendOrCancelAsync();
        var btnRow = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, 6, 0, 6),
        };
        _send.Dock    = DockStyle.Right;
        _attachBtn.Dock = DockStyle.Left;
        btnRow.Controls.Add(_send);
        btnRow.Controls.Add(_attachBtn);

        // TableLayoutPanel keeps chip / text / button row in fixed lanes — no overlap.
        var inputTlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10, 4, 10, 0),
            BackColor = UiTheme.Surface,
        };
        inputTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputTlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // attach chip
        inputTlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // text input
        inputTlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // button row
        var inputBorder = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(3),
            BackColor = Color.Transparent,
        };
        inputBorder.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, inputBorder.Width - 1, inputBorder.Height - 1);
            using var path = UiTheme.MakeRoundedPath(r, 8);
            using var fill = new SolidBrush(UiTheme.SurfaceCard);
            e.Graphics.FillPath(fill, path);
            using var p = new Pen(UiTheme.Border, 1.5f);
            e.Graphics.DrawPath(p, path);
        };
        inputBorder.Controls.Add(_input);
        inputTlp.Controls.Add(_attachChip,   0, 0);
        inputTlp.Controls.Add(inputBorder,   0, 1);
        inputTlp.Controls.Add(btnRow,        0, 2);

        _inputPanel.Controls.Add(inputTlp);   // Fill
        _inputPanel.Controls.Add(_agentRow);  // Top — absolute top
        _split.Panel2.Controls.Add(_history);
        _split.Panel2.Controls.Add(_inputPanel);

        _statusLabel = new ToolStripStatusLabel("Ready");
        _statsLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = UiTheme.TextSecondary };
        _bridgeLabel = new ToolStripStatusLabel("\uD83D\uDCC1 not configured") { ForeColor = UiTheme.TextSecondary };
        _bridgeLabel.Click += (_, _) => PickBridgeFolder();
        _bridgeLabel.IsLink = true;
        _status = new StatusStrip { BackColor = UiTheme.SurfaceCard, SizingGrip = false };
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_statsLabel);
        _status.Items.Add(_bridgeLabel);

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_topBar);

        Load += async (_, _) =>
        {
            var saved = _store.Load();
            if (saved.DarkTheme) { UiTheme.SetDark(true); ReapplyTheme(); }
            StartBridge();
            await ReloadAgentsAsync();
        };
        FormClosing += (_, _) =>
        {
            _streamCts?.Cancel();
            try { _bridge?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        };
    }

    private void StartBridge()
    {
        var settings = _store.Load();
        UpdateBridgeLabel(settings.BridgeRoot);
        _bridge = new BridgeClient(_client, settings.BridgeRoot);
        _bridge.StatusChanged += s => BeginInvoke(() =>
        {
            var prefix = settings.BridgeRoot is null ? "not configured" : System.IO.Path.GetFileName(settings.BridgeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            _bridgeLabel.Text = $"\uD83D\uDCC1 {prefix} \u2014 {s}";
        });
        _bridge.Start();
    }

    private void ToggleTheme()
    {
        var s = _store.Load();
        s.DarkTheme = !s.DarkTheme;
        _store.Save(s);
        UiTheme.SetDark(s.DarkTheme);
        ReapplyTheme();
    }

    private void ReapplyTheme()
    {
        _themeBtn.Text = UiTheme.IsDark ? "\u2600" : "\uD83C\uDF19";
        _themeBtn.ForeColor       = UiTheme.TextPrimary;
        _signOutBtn.ForeColor     = UiTheme.TextPrimary;
        _changePwdBtn.ForeColor   = UiTheme.TextPrimary;
        _bridgeFolderBtn.ForeColor= UiTheme.TextPrimary;
        _topBarLabel.ForeColor    = UiTheme.TextPrimary;
        _topBar.BackColor = UiTheme.SurfaceCard;
        foreach (Control c in _topBar.Controls)
            if (c is Button b) { b.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceAlt; b.FlatAppearance.MouseDownBackColor = UiTheme.Border; }
        UiTheme.ApplyForm(this);
        _status.BackColor = UiTheme.SurfaceCard;
        _split.BackColor = UiTheme.Border;
        _split.Panel1.BackColor = UiTheme.SurfaceAlt;
        _split.Panel2.BackColor = UiTheme.Surface;
        _conversationList.BackColor = UiTheme.SurfaceAlt;
        _conversationList.ForeColor = UiTheme.TextPrimary;
        _searchBox.BackColor = UiTheme.SurfaceCard;
        _searchBox.ForeColor = UiTheme.TextPrimary;
        _agentRow.BackColor = UiTheme.Surface;
        _agentDescription.ForeColor = UiTheme.TextSecondary;
        _agentCombo.BackColor = UiTheme.SurfaceCard;
        _agentCombo.ForeColor = UiTheme.TextPrimary;
        _inputPanel.BackColor = UiTheme.Surface;
        _input.BackColor = UiTheme.SurfaceCard;
        _input.ForeColor = UiTheme.TextPrimary;
        _attachChip.BackColor = UiTheme.AttachChipBg;
        UiTheme.Primary(_newChatBtn);
        UiTheme.Secondary(_deleteChatBtn);
        UiTheme.Secondary(_attachBtn);
        if (_streaming) _send.BackColor = UiTheme.Danger; else UiTheme.Primary(_send);
        _history.RefreshTheme();
        _conversationList.Invalidate();
        _topBar.Invalidate();
        _inputPanel.Invalidate();
        Invalidate(true);
    }

    private void UpdateInputHeight()
    {
        const int MinLines = 2, MaxLines = 6;
        int lineH    = _input.Font.Height + 3;
        int visLines = Math.Clamp(_input.GetLineFromCharIndex(_input.TextLength) + 1, MinLines, MaxLines);
        // agent row (34) + chip (26 if visible, else 0) + text rows + button row (46) + tlp padding (4)
        int chipH   = _attachChip.Visible ? 26 : 0;
        int textH   = visLines * lineH + 8;
        int desired = _agentRow.Height + chipH + textH + 46 + 4;
        if (_inputPanel.Height != desired) _inputPanel.Height = desired;
    }

    private void FilterConversations(string query)
    {
        _suppressConversationSelection = true;
        _conversationList.BeginUpdate();
        _conversationList.Items.Clear();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _conversations
            : _conversations.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in filtered) _conversationList.Items.Add(c);
        _conversationList.EndUpdate();
        _suppressConversationSelection = false;
    }

    private void FillConversationList(IEnumerable<ConversationSummaryDto> items)
    {
        var today = DateTime.Today;
        string? lastGroup = null;
        foreach (var c in items)
        {
            var days  = (today - c.UpdatedAt.ToLocalTime().Date).TotalDays;
            var group = days < 1  ? "Today"
                      : days < 2  ? "Yesterday"
                      : days < 8  ? "This Week"
                      : days < 31 ? "This Month"
                      : "Older";
            if (group != lastGroup) { _conversationList.Items.Add(new ConvHeader(group)); lastGroup = group; }
            _conversationList.Items.Add(c);
        }
    }

    private async Task SearchConversationsAsync(string query)
    {
        try
        {
            _statusLabel.Text = "Searching\u2026";
            var results = await _client.SearchConversationsAsync(query, semantic: true);
            _suppressConversationSelection = true;
            _conversationList.BeginUpdate();
            _conversationList.Items.Clear();
            foreach (var c in results) _conversationList.Items.Add(c);
            _conversationList.EndUpdate();
            _suppressConversationSelection = false;
            _statusLabel.Text = $"{results.Count} result(s) for \"{query}\"";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Search error: " + ex.Message;
        }
    }

    private void PickBridgeFolder()
    {
        var current = _store.Load().BridgeRoot;
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick a folder on THIS PC the assistant may read and write. Skills will be confined to this folder and any subfolders you create.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = current ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var picked = LocalFsHandler.NormalizeRoot(dlg.SelectedPath);
        if (picked is null) { MessageBox.Show(this, "That folder is not valid.", "Folder"); return; }
        var s = _store.Load();
        s.BridgeRoot = picked;
        _store.Save(s);
        if (_bridge is not null) _bridge.Root = picked;
        UpdateBridgeLabel(picked);
    }

    private void UpdateBridgeLabel(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            _bridgeLabel.Text = "\uD83D\uDCC1 click to share a folder";
            _bridgeLabel.ToolTipText = "No folder shared. Skills cannot read or write on this PC.";
        }
        else
        {
            _bridgeLabel.Text = "\uD83D\uDCC1 " + root;
            _bridgeLabel.ToolTipText = $"Skills may read/write inside:\n{root}";
        }
    }

    private async Task ReloadAgentsAsync()
    {
        try
        {
            _statusLabel.Text = "Loading agents\u2026";
            _agents = await _client.ListAgentsAsync();
            _agentCombo.DataSource = _agents;
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
        _agentDescription.Text = a is null ? "" : a.Description;
        _agentInfoBtn.Enabled = a is not null;
        if (a is not null)
        {
            var s = _store.Load();
            s.LastAgentId = a.Id;
            _store.Save(s);
            _history.SetAgentName(a.Name);
            _input.PlaceholderText = $"Message {a.Name}\u2026";
            StartNewChat();
            await ReloadConversationsAsync();
        }
    }

    private void OnAgentInfoClicked(object? sender, EventArgs e)
    {
        var agent = _agentCombo.SelectedItem as AgentDto;
        if (agent is null) return;

        var toolCount = agent.ToolIds?.Count ?? 0;
        var ragInfo = agent.RagEnabled ? "Yes" : "No";
        var modelInfo = string.IsNullOrWhiteSpace(agent.DefaultModelId) ? "(server default)" : agent.DefaultModelId;

        var info = $"{agent.Name}\n\n" +
                   $"Description: {(string.IsNullOrWhiteSpace(agent.Description) ? "(none)" : agent.Description)}\n" +
                   $"Model: {modelInfo}\n" +
                   $"Tools: {toolCount}\n" +
                   $"RAG: {ragInfo}\n" +
                   $"Max tool calls/turn: {agent.MaxToolCalls?.ToString() ?? "3 (default)"}";

        MessageBox.Show(info, $"Agent: {agent.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                for (int i = 0; i < _conversationList.Items.Count; i++)
                    if (_conversationList.Items[i] is ConversationSummaryDto c && c.Id == cid)
                    { _conversationList.SelectedIndex = i; break; }
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
                    _history.AppendUserMessage(m.Body ?? "(empty)");
                else if (string.Equals(m.Role, "Assistant", StringComparison.OrdinalIgnoreCase))
                    _history.AppendAssistantMessage(assistantName, m.Body ?? "(empty)");
            }
            _history.ScrollToEnd();
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
        _history.AppendUserMessage(displayMessage);
        _history.BeginAssistantStream(agent.Name);

        SetStreaming(true);
        var sw = Stopwatch.StartNew();
        var tokens = 0;
        var outputChars = 0;
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
                    _history.AppendAssistantText(frame.Text);
                    outputChars += frame.Text.Length;
                    tokens++;
                    var rate = sw.Elapsed.TotalSeconds > 0 ? tokens / sw.Elapsed.TotalSeconds : 0;
                    _statsLabel.Text = $"{tokens} tokens \u00b7 {rate:F1} tok/s";
                }
                else if (frame.Kind == TokenStreamFrameKind.Error)
                {
                    _history.EndAssistantStream();
                    _history.AppendNote("Error: " + (frame.ErrorMessage ?? "unknown"), BubbleKind.Error);
                    break;
                }
                else if (frame.Kind == TokenStreamFrameKind.End)
                {
                    // Estimate cost for cloud models (local = free).
                    var inputEst  = message.Length / 4;
                    var outputEst = outputChars / 4;
                    var cost = MyLocalAssistant.Client.Services.ModelPricing.Estimate(
                        agent.DefaultModelId, inputEst, outputEst);
                    var costStr = MyLocalAssistant.Client.Services.ModelPricing.Format(cost);
                    var rate = sw.Elapsed.TotalSeconds > 0 ? tokens / sw.Elapsed.TotalSeconds : 0;
                    _statsLabel.Text = string.IsNullOrEmpty(costStr)
                        ? $"{tokens} tok \u00b7 {rate:F1} tok/s"
                        : $"{tokens} tok \u00b7 {rate:F1} tok/s \u00b7 {costStr}";
                    break;
                }
                else if (frame.Kind == TokenStreamFrameKind.Queued)
                {
                    var pos = frame.QueuePosition ?? 0;
                    _statsLabel.Text = pos > 1 ? $"Queued — position {pos} in line\u2026" : "Starting\u2026";
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolUnavailable)
                {
                    _history.AppendNote($"tool unavailable: {frame.ToolName ?? "?"} \u2014 {frame.ToolReason ?? "?"}");
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolCall)
                {
                    _history.AppendNote($"\u2192 tool call: {frame.ToolName ?? "?"} {frame.ToolJson ?? ""}");
                }
                else if (frame.Kind == TokenStreamFrameKind.ToolResult)
                {
                    var isErr = string.Equals(frame.ToolReason, "error", StringComparison.Ordinal);
                    // If the tool returned an image, render it inline instead of (or after) the note.
                    if (!isErr && _history.TryAppendImageResult(frame.ToolJson))
                    {
                        // Image bubble was added; still show a compact note for audit.
                        _history.AppendNote($"\u2190 image: {frame.ToolName ?? "?"}");
                    }
                    else
                    {
                        var displayJson = isErr ? ExtractErrorMessage(frame.ToolJson) : frame.ToolJson ?? "";
                        _history.AppendNote($"\u2190 {(isErr ? "tool error" : "tool result")}: {frame.ToolName ?? "?"} {displayJson}",
                            isErr ? BubbleKind.Error : BubbleKind.Note);
                    }
                }
            }
            _history.EndAssistantStream();
        }
        catch (OperationCanceledException)
        {
            _history.EndAssistantStream();
            _history.AppendNote("Cancelled");
        }
        catch (Exception ex)
        {
            _history.EndAssistantStream();
            _history.AppendNote("Error: " + ex.Message, BubbleKind.Error);
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

    private void SetStreaming(bool streaming)
    {
        _streaming = streaming;
        if (streaming)
        {
            _send.Text = "\u23F9  Stop";
            _send.BackColor = UiTheme.Danger;
            _send.FlatAppearance.MouseOverBackColor = UiTheme.IsDark
                ? Color.FromArgb(200, 60, 50) : Color.FromArgb(160, 30, 20);
        }
        else
        {
            _send.Text = "Send  \u23CE";
            _send.BackColor = UiTheme.Accent;
            _send.FlatAppearance.MouseOverBackColor = UiTheme.AccentHover;
        }
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
            Filter = "Supported (*.txt;*.md;*.pdf;*.docx;*.xlsx;*.html;*.htm)|*.txt;*.md;*.markdown;*.pdf;*.docx;*.xlsx;*.xls;*.html;*.htm|All files (*.*)|*.*",
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
        var rawItem = _conversationList.Items[e.Index];

        // ── Group header ──────────────────────────────────────────────────
        if (rawItem is ConvHeader hdr)
        {
            using (var bgBrush = new SolidBrush(UiTheme.SurfaceAlt))
                e.Graphics.FillRectangle(bgBrush, e.Bounds);
            var textRect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, e.Bounds.Width - 16, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, hdr.GroupName.ToUpperInvariant(), UiTheme.Caption,
                textRect, UiTheme.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var hp = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(hp, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            return;
        }

        var item     = rawItem as ConversationSummaryDto;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bg       = selected
            ? (UiTheme.IsDark ? Color.FromArgb(30, 65, 120) : Color.FromArgb(219, 234, 254))
            : UiTheme.SurfaceAlt;
        using (var bgBrush = new SolidBrush(bg)) e.Graphics.FillRectangle(bgBrush, e.Bounds);

        if (selected)
        {
            using var ab = new SolidBrush(UiTheme.Accent);
            e.Graphics.FillRectangle(ab, new Rectangle(e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height));
        }

        if (item is null) { e.DrawFocusRectangle(); return; }

        // ── Avatar circle ──────────────────────────────────────────────────
        const int AvatarSize = 32;
        int avatarX = e.Bounds.Left + 10;
        int avatarY = e.Bounds.Top + (e.Bounds.Height - AvatarSize) / 2;
        var avatarRect = new Rectangle(avatarX, avatarY, AvatarSize, AvatarSize);
        Color[] palette =
        [
            Color.FromArgb( 99, 102, 241), Color.FromArgb(236,  72, 153),
            Color.FromArgb(245, 158,  11), Color.FromArgb( 16, 185, 129),
            Color.FromArgb( 59, 130, 246), Color.FromArgb(239,  68,  68),
        ];
        var avatarColor = palette[Math.Abs((item.Title ?? "").GetHashCode()) % palette.Length];
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var avBrush = new SolidBrush(avatarColor))
            e.Graphics.FillEllipse(avBrush, avatarRect);
        var initial = item.Title is { Length: > 0 } t ? t[0].ToString().ToUpperInvariant() : "?";
        TextRenderer.DrawText(e.Graphics, initial, UiTheme.BaseBold, avatarRect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

        // ── Text ───────────────────────────────────────────────────────────────────
        int textLeft  = avatarX + AvatarSize + 8;
        var rect      = e.Bounds;
        var titleRect = new Rectangle(textLeft, rect.Top + 8,  rect.Right - textLeft - 6, 20);
        var subRect   = new Rectangle(textLeft, rect.Top + 26, rect.Right - textLeft - 6, 18);

        TextRenderer.DrawText(e.Graphics, item.Title, UiTheme.BaseBold, titleRect, UiTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var stamp = item.UpdatedAt.ToLocalTime();
        var subText = stamp.Date == DateTime.Today
            ? $"Today {stamp:HH:mm}"
            : (DateTime.Today - stamp.Date).TotalDays < 7
                ? stamp.ToString("ddd HH:mm")
                : stamp.ToString("yyyy-MM-dd HH:mm");
        TextRenderer.DrawText(e.Graphics, subText, UiTheme.Caption, subRect, UiTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, rect.Left + 8, rect.Bottom - 1, rect.Right - 8, rect.Bottom - 1);
    }

    /// <summary>
    /// Extracts the human-readable "error" field from a JSON payload like <c>{"error":"message"}</c>.
    /// Falls back to the raw string (truncated) when the JSON can't be parsed.
    /// </summary>
    private static string ExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errProp)
                && errProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return errProp.GetString() ?? json;
            }
        }
        catch { /* fall through */ }
        return json.Length > 200 ? json[..200] + "\u2026" : json;
    }

    private sealed record ConvHeader(string GroupName);
}
