using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

/// <summary>
/// Lightweight prompt-engineering workbench that lets admins test an agent via live chat
/// without leaving the Admin UI.
/// </summary>
internal sealed class PromptTestForm : Form
{
    private readonly string _agentId;
    private readonly ServerClient _client;
    private readonly RichTextBox _transcript;
    private readonly TextBox _input;
    private readonly Button _sendBtn;
    private readonly Button _clearBtn;
    private readonly Label _statusLabel;
    private Guid? _conversationId;
    private CancellationTokenSource? _cts;

    public PromptTestForm(string agentId, string agentName, ServerClient client)
    {
        _agentId = agentId;
        _client = client;

        Text = $"Prompt Workbench — {agentName}";
        Width = 700;
        Height = 540;
        MinimumSize = new Size(500, 400);
        StartPosition = FormStartPosition.CenterParent;

        _transcript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Segoe UI", 10),
            BackColor = SystemColors.Window,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
        };

        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 10),
            Height = 70,
        };
        _input.KeyDown += OnInputKeyDown;

        _sendBtn = new Button { Text = "Send", Width = 70, Dock = DockStyle.Right };
        _sendBtn.Click += async (_, _) => await SendAsync();

        _clearBtn = new Button { Text = "Clear", Width = 70, Dock = DockStyle.Right };
        _clearBtn.Click += (_, _) => { _transcript.Clear(); _conversationId = null; _statusLabel.Text = "Cleared."; };

        _statusLabel = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 9),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var bottomButtons = new Panel { Dock = DockStyle.Right, Width = 150 };
        bottomButtons.Controls.Add(_sendBtn);
        bottomButtons.Controls.Add(_clearBtn);

        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(4) };
        inputRow.Controls.Add(_input);
        inputRow.Controls.Add(bottomButtons);

        var statusStrip = new Panel { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(4, 2, 4, 2) };
        statusStrip.Controls.Add(_statusLabel);

        Controls.Add(_transcript);
        Controls.Add(inputRow);
        Controls.Add(statusStrip);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return && !e.Shift)
        {
            e.SuppressKeyPress = true;
            _ = SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var text = _input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (_cts is not null) { _cts.Cancel(); _cts = null; }

        _input.Clear();
        AppendLine($"You: {text}", Color.FromArgb(0, 100, 200));

        _sendBtn.Enabled = false;
        _statusLabel.Text = "Generating\u2026";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var req = new ChatRequest(_agentId, text, ConversationId: _conversationId);
            var assistantStart = _transcript.TextLength;
            _transcript.AppendText("Assistant: ");
            await foreach (var frame in _client.StreamChatAsync(req, ct))
            {
                if (frame.Kind == TokenStreamFrameKind.Meta && frame.ConversationId is Guid mid)
                    _conversationId = mid;
                else if (frame.Kind == TokenStreamFrameKind.Token && frame.Text is not null)
                    _transcript.AppendText(frame.Text);
                else if (frame.Kind == TokenStreamFrameKind.Error)
                    { _transcript.AppendText($"\n[Error: {frame.ErrorMessage}]"); break; }
                else if (frame.Kind == TokenStreamFrameKind.End)
                    break;
            }
            _transcript.AppendText("\n\n");
            _transcript.ScrollToCaret();
            _statusLabel.Text = "Done.";
        }
        catch (OperationCanceledException) { _transcript.AppendText("\n[Cancelled]\n\n"); _statusLabel.Text = "Cancelled."; }
        catch (Exception ex) { _transcript.AppendText($"\n[Error: {ex.Message}]\n\n"); _statusLabel.Text = "Error."; }
        finally { _sendBtn.Enabled = true; _cts = null; }
    }

    private void AppendLine(string text, Color color)
    {
        var start = _transcript.TextLength;
        _transcript.AppendText(text + "\n");
        _transcript.Select(start, text.Length);
        _transcript.SelectionColor = color;
        _transcript.SelectionLength = 0;
        _transcript.ScrollToCaret();
    }
}
