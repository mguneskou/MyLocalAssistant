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
    private readonly RichTextBox _history;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _statsLabel;

    private CancellationTokenSource? _streamCts;
    private bool _streaming;
    private List<AgentDto> _agents = new();

    public ChatForm(ChatApiClient client, ClientSettingsStore store)
    {
        _client = client;
        _store = store;

        Text = $"MyLocalAssistant — {_client.CurrentUser?.DisplayName ?? "?"} @ {_client.BaseUrl}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 520);
        Size = new Size(960, 700);
        Font = new Font("Segoe UI", 10F);

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, ImageScalingSize = new Size(16, 16) };
        _agentCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        _agentCombo.ComboBox!.DisplayMember = nameof(AgentDto.Name);
        _agentCombo.SelectedIndexChanged += (_, _) => OnAgentChanged();
        _agentDescription = new ToolStripLabel("") { ForeColor = SystemColors.GrayText };
        _changePwdBtn = new ToolStripButton("Change password…");
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

        _history = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F),
            BackColor = Color.White,
            DetectUrls = false,
        };

        var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(8) };
        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = false,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10F),
        };
        _input.KeyDown += OnInputKeyDown;
        _send = new Button { Text = "Send", Dock = DockStyle.Right, Width = 110 };
        _send.Click += async (_, _) => await OnSendOrCancelAsync();
        inputPanel.Controls.Add(_input);
        inputPanel.Controls.Add(_send);

        _statusLabel = new ToolStripStatusLabel("Ready");
        _statsLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_statsLabel);

        // Z-order: Fill control first, then Top/Bottom-docked siblings.
        Controls.Add(_history);
        Controls.Add(inputPanel);
        Controls.Add(_status);
        Controls.Add(_toolbar);

        Load += async (_, _) => await ReloadAgentsAsync();
        FormClosing += (_, _) => _streamCts?.Cancel();
    }

    private async Task ReloadAgentsAsync()
    {
        try
        {
            _statusLabel.Text = "Loading agents…";
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

    private void OnAgentChanged()
    {
        var a = _agentCombo.SelectedItem as AgentDto;
        _agentDescription.Text = a is null ? "" : "  " + a.Description;
        if (a is not null)
        {
            var s = _store.Load();
            s.LastAgentId = a.Id;
            _store.Save(s);
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
        var message = _input.Text.Trim();
        if (message.Length == 0) return;

        _input.Clear();
        AppendRoleLine("You", Color.SteelBlue);
        AppendBody(message + "\n\n");
        AppendRoleLine(agent.Name, Color.SeaGreen);
        var assistantStart = _history.TextLength;

        SetStreaming(true);
        var sw = Stopwatch.StartNew();
        var tokens = 0;
        try
        {
            _streamCts = new CancellationTokenSource();
            await foreach (var frame in _client.StreamChatAsync(new ChatRequest(agent.Id, message), _streamCts.Token))
            {
                if (frame.Kind == TokenStreamFrameKind.Token && frame.Text is not null)
                {
                    AppendBody(frame.Text);
                    tokens++;
                    var rate = sw.Elapsed.TotalSeconds > 0 ? tokens / sw.Elapsed.TotalSeconds : 0;
                    _statsLabel.Text = $"{tokens} tokens · {rate:F1} tok/s";
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
        if (streaming) { _statusLabel.Text = "Generating…"; _statsLabel.Text = ""; }
    }
}
